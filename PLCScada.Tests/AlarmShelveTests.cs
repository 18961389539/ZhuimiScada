using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class AlarmShelveTests
{
    [Fact]
    public async Task ShelveSetsExpiryWithReasonAndAuditTrail()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await system.Store.SetCommunicationAlarmAsync(system.Device.Id, system.Device.Name, "通信中断");
        var alarm = Assert.Single(await system.Store.GetAlarmsAsync());

        var before = DateTimeOffset.UtcNow;
        await system.Store.ShelveAlarmAsync(alarm.AlarmId, 30, "调试期间屏蔽");

        var shelved = Assert.Single(await system.Store.GetAlarmsAsync());
        Assert.NotNull(shelved.ShelvedUntil);
        Assert.InRange(shelved.ShelvedUntil!.Value, before.AddMinutes(29), before.AddMinutes(31));
        Assert.Equal("调试期间屏蔽", shelved.ShelveReason);
        Assert.Contains(await system.Store.GetLogsAsync(),
            x => x.Message.Contains("搁置报警") && x.Message.Contains("调试期间屏蔽"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.ShelveAlarmAsync(alarm.AlarmId, 0, "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.ShelveAlarmAsync(alarm.AlarmId, 2000, "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.ShelveAlarmAsync(alarm.AlarmId, 10, "  "));

        // 已解除的报警不可搁置
        await system.Store.SetCommunicationAlarmAsync(system.Device.Id, system.Device.Name, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.ShelveAlarmAsync(alarm.AlarmId, 10, "再试"));
    }

    [Fact]
    public async Task UnshelveClearsFieldsAndAudits()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await system.Store.SetTagAlarmAsync(Guid.NewGuid(), "功率上限", "超限");
        var alarm = Assert.Single(await system.Store.GetAlarmsAsync());
        await system.Store.ShelveAlarmAsync(alarm.AlarmId, 15, "临时屏蔽");

        await system.Store.UnShelveAlarmAsync(alarm.AlarmId);

        var restored = Assert.Single(await system.Store.GetAlarmsAsync());
        Assert.Null(restored.ShelvedUntil);
        Assert.Null(restored.ShelveReason);
        Assert.Contains(await system.Store.GetLogsAsync(), x => x.Message.Contains("取消搁置报警"));

        // 重复取消是无害的空操作
        await system.Store.UnShelveAlarmAsync(alarm.AlarmId);
    }

    [Fact]
    public async Task RepeatWithinWindowKeepsShelveStateAndSingleRecord()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await system.Store.SetCommunicationAlarmAsync(system.Device.Id, system.Device.Name, "通信中断");
        var alarm = Assert.Single(await system.Store.GetAlarmsAsync());
        await system.Store.ShelveAlarmAsync(alarm.AlarmId, 60, "维护中");

        await system.Store.SetCommunicationAlarmAsync(system.Device.Id, system.Device.Name, null);
        await system.Store.SetCommunicationAlarmAsync(system.Device.Id, system.Device.Name, "通信中断");

        var merged = Assert.Single(await system.Store.GetAlarmsAsync());
        Assert.Equal(AlarmStatus.Active, merged.Status);
        Assert.Equal(2, merged.RepeatCount);
        Assert.NotNull(merged.ShelvedUntil);
        Assert.Equal("维护中", merged.ShelveReason);
    }
}
