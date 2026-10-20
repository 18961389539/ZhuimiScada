using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Core;
using PLCScada.Infrastructure.Persistence;

namespace PLCScada.Tests;

public sealed class AlarmSuppressionTests
{
    [Fact]
    public async Task SuppressionBlocksDeviceScopedAlarmsUntilExpiry()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "抑制点位",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 20,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(tag);
        var other = new Device { Name = "其他设备" };
        await system.Devices.SaveAsync(other);

        await system.Store.AddSuppressionAsync(system.Device.Id, 30, "维护中");

        await system.Store.SetCommunicationAlarmAsync(system.Device.Id, system.Device.Name, "通信中断");
        await system.Store.SetTagAlarmAsync(tag.Id, tag.Name, "超限", "High");
        await system.Store.SetTagQualityAlarmAsync(tag.Id, tag.Name, "连续失败");
        var definition = new AlarmDefinition
        {
            TagId = tag.Id, Name = "抑制定义", Kind = AlarmDefinitionKind.Bit, Message = "命中"
        };
        await system.Store.SaveAlarmDefinitionAsync(definition);
        await system.Store.SetAlarmDefinitionAlarmAsync(definition.Id, "命中", "Error");
        await system.Store.SetCommunicationAlarmAsync(other.Id, other.Name, "其他设备断线");

        var suppressedScope = Assert.Single(await system.Store.GetAlarmsAsync());
        Assert.Contains("其他设备断线", suppressedScope.Message);

        // 抑制到期后恢复正常报警
        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            foreach (var item in await db.AlarmSuppressions.ToListAsync())
                item.Until = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        await system.Store.SetCommunicationAlarmAsync(system.Device.Id, system.Device.Name, "通信中断");
        Assert.Equal(2, (await system.Store.GetAlarmsAsync()).Count);
    }

    [Fact]
    public async Task AddExtendsExistingAndRemoveClearsWithAudit()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);

        await system.Store.AddSuppressionAsync(system.Device.Id, 15, "调试");
        var first = Assert.Single(await system.Store.GetSuppressionsAsync());
        var firstUntil = first.Until;

        await system.Store.AddSuppressionAsync(system.Device.Id, 60, "调试延长");
        var extended = Assert.Single(await system.Store.GetSuppressionsAsync());
        Assert.True(extended.Until >= firstUntil);
        Assert.Contains(await system.Store.GetLogsAsync(),
            x => x.Message.Contains("抑制设备") && x.Message.Contains("调试延长"));

        await system.Store.RemoveSuppressionAsync(extended.Id);
        Assert.Empty(await system.Store.GetSuppressionsAsync());
        Assert.Contains(await system.Store.GetLogsAsync(), x => x.Message.Contains("解除设备"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.AddSuppressionAsync(system.Device.Id, 0, "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.AddSuppressionAsync(system.Device.Id, 2000, "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.AddSuppressionAsync(system.Device.Id, 10, "  "));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.AddSuppressionAsync(Guid.NewGuid(), 10, "不存在设备"));
    }
}
