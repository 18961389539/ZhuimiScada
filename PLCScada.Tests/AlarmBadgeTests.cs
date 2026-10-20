using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class AlarmBadgeTests
{
    [Fact]
    public async Task BadgeCountsUnacknowledgedExcludingShelvedAndTracksRecovery()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);

        await system.Store.SetCommunicationAlarmAsync(system.Device.Id, system.Device.Name, "通信中断");
        var powerTag = Guid.NewGuid();
        await system.Store.SetTagAlarmAsync(powerTag, "功率上限", "超限", "High");
        var voltageTag = Guid.NewGuid();
        await system.Store.SetTagAlarmAsync(voltageTag, "电压上限", "超限", "High");

        var alarms = await system.Store.GetAlarmsAsync();
        await system.Store.ShelveAlarmAsync(
            alarms.First(x => x.Message.Contains("电压上限")).AlarmId, 30, "调试屏蔽");

        var badge = await system.Store.GetAlarmBadgeSummaryAsync();
        Assert.Equal(2, badge.UnacknowledgedCount);   // 设备 P1 + 功率上限 P2（电压上限被搁置，排除）
        Assert.Equal(1, badge.P1UnacknowledgedCount);
        Assert.Equal(0, badge.ClearedUnacknowledgedCount);

        // 功率上限恢复但未确认 → 转为已恢复未确认，未确认数下降
        await system.Store.SetTagAlarmAsync(powerTag, "功率上限", null, "High");
        badge = await system.Store.GetAlarmBadgeSummaryAsync();
        Assert.Equal(1, badge.UnacknowledgedCount);
        Assert.Equal(1, badge.ClearedUnacknowledgedCount);

        // 确认设备报警 → 未确认归零，RTN 仍待确认
        await system.Store.AcknowledgeAlarmAsync(
            alarms.First(x => x.Message.Contains("通信中断")).AlarmId);
        badge = await system.Store.GetAlarmBadgeSummaryAsync();
        Assert.Equal(0, badge.UnacknowledgedCount);
        Assert.Equal(0, badge.P1UnacknowledgedCount);
        Assert.Equal(1, badge.ClearedUnacknowledgedCount);
    }
}
