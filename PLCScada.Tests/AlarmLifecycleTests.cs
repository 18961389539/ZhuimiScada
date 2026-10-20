using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Core;
using PLCScada.Infrastructure.Persistence;

namespace PLCScada.Tests;

public sealed class AlarmLifecycleTests
{
    [Fact]
    public async Task UnacknowledgedRecoveryKeepsPendingAckThenAckClosesIt()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await system.Store.SetCommunicationAlarmAsync(system.Device.Id, system.Device.Name, "通信中断");
        var raised = Assert.Single(await system.Store.GetAlarmsAsync());
        Assert.Equal(AlarmStatus.Active, raised.Status);
        Assert.Equal(AlarmPriority.P1, raised.Priority);
        Assert.Null(raised.ClearedAt);
        Assert.Equal(1, raised.RepeatCount);

        await system.Store.SetCommunicationAlarmAsync(system.Device.Id, system.Device.Name, null);
        var recovered = Assert.Single(await system.Store.GetAlarmsAsync());
        Assert.Equal(AlarmStatus.ClearedUnacknowledged, recovered.Status);
        Assert.NotNull(recovered.ClearedAt);

        await system.Store.AcknowledgeAlarmAsync(recovered.AlarmId, "现场确认已恢复");
        var closed = Assert.Single(await system.Store.GetAlarmsAsync());
        Assert.Equal(AlarmStatus.Cleared, closed.Status);
        Assert.Equal("现场确认已恢复", closed.AcknowledgeNote);
        Assert.NotNull(closed.AcknowledgedAt);
        Assert.Contains(await system.Store.GetLogsAsync(),
            x => x.Message.Contains("确认报警") && x.Message.Contains("现场确认已恢复"));
    }

    [Fact]
    public async Task AcknowledgedAlarmClearsDirectlyAndDoubleAckRejected()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tagId = Guid.NewGuid();
        await system.Store.SetTagAlarmAsync(tagId, "功率上限", "读数 120 高于上限 100");
        var alarm = Assert.Single(await system.Store.GetAlarmsAsync());
        Assert.Equal(AlarmPriority.P2, alarm.Priority);

        await system.Store.AcknowledgeAlarmAsync(alarm.AlarmId);
        Assert.Equal(AlarmStatus.Acknowledged,
            (await system.Store.GetAlarmsAsync()).Single().Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.AcknowledgeAlarmAsync(alarm.AlarmId));

        await system.Store.SetTagAlarmAsync(tagId, "功率上限", null);
        Assert.Contains(await system.Store.GetAlarmsAsync(),
            x => x.Message.Contains("功率上限") && x.Status == AlarmStatus.Cleared);
    }

    [Fact]
    public async Task RepeatWithinWindowMergesAndOutsideCreatesNewRecord()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);

        Raise();
        Clear();
        Raise();
        var merged = Assert.Single(await system.Store.GetAlarmsAsync());
        Assert.Equal(AlarmStatus.Active, merged.Status);
        Assert.Equal(2, merged.RepeatCount);
        Assert.Null(merged.ClearedAt);
        Assert.Null(merged.AcknowledgedAt);

        // 把恢复时间推到 120 秒前模拟窗口外
        Clear();
        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var record = await db.AlarmRecords.SingleAsync();
            record.ClearedAt = DateTimeOffset.UtcNow.AddSeconds(-120);
            await db.SaveChangesAsync();
        }
        Raise();
        var alarms = await system.Store.GetAlarmsAsync();
        Assert.Equal(2, alarms.Count);
        Assert.Single(alarms, x => x.Status == AlarmStatus.Active && x.RepeatCount == 1);
        Assert.Single(alarms, x => x.Status == AlarmStatus.ClearedUnacknowledged);

        void Raise() => system.Store.SetCommunicationAlarmAsync(
            system.Device.Id, system.Device.Name, "通信中断").GetAwaiter().GetResult();
        void Clear() => system.Store.SetCommunicationAlarmAsync(
            system.Device.Id, system.Device.Name, null).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task CleanupNeverDeletesUnacknowledgedRecovery()
    {
        await using var system = await TestSystem.CreateAsync();
        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.AlarmRecords.Add(new AlarmRecord
            {
                OccurredAt = now.AddDays(-100), Source = "Test", Message = "旧已确认恢复", Status = AlarmStatus.Cleared
            });
            db.AlarmRecords.Add(new AlarmRecord
            {
                OccurredAt = now.AddDays(-100), Source = "Test", Message = "旧未确认恢复",
                Status = AlarmStatus.ClearedUnacknowledged
            });
            await db.SaveChangesAsync();
        }

        var result = await system.Store.CleanupHistoryAsync(new DataRetentionOptions
        {
            OperationLogRetentionDays = 0, ClearedAlarmRetentionDays = 30, RecipeApplyRecordRetentionDays = 0
        });

        Assert.Equal(1, result.ClearedAlarmsDeleted);
        var remaining = await system.Store.GetAlarmsAsync();
        Assert.Contains(remaining, x => x.Message == "旧未确认恢复");
        Assert.DoesNotContain(remaining, x => x.Message == "旧已确认恢复");
    }
}
