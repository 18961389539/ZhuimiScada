using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class RunningStateMonitorTests
{
    [Fact]
    public async Task FallsBackToNamedTagAndSwitchesSelectionExplicitly()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);

        // 未配置且无同名 Tag → 未启用
        Assert.Null(await system.Store.GetRunningStateTagIdAsync());

        // 历史同名 Tag 作为默认回退（兼容旧自动创建）
        var named = new Tag
        {
            Name = MonitoringDefaults.RunningStateTagName,
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 10,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(named);
        Assert.Equal(named.Id, await system.Store.GetRunningStateTagIdAsync());

        // 从全部 Tag 中任选一个
        var other = new Tag
        {
            Name = "运行信号",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 11,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(other);
        await system.Store.SetRunningStateTagIdAsync(other.Id);
        Assert.Equal(other.Id, await system.Store.GetRunningStateTagIdAsync());
        Assert.Contains(await system.Store.GetLogsAsync(),
            x => x.Message.Contains("Monitoring.RunningStateTagId=" + other.Id));

        // 显式关闭：不再回退到同名 Tag
        await system.Store.SetRunningStateTagIdAsync(null);
        Assert.Null(await system.Store.GetRunningStateTagIdAsync());

        // 不存在的 Tag 拒绝
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SetRunningStateTagIdAsync(Guid.NewGuid()));

        // 非布尔 Tag 拒绝（运行状态监控只接受 Bool）
        var numeric = new Tag
        {
            Name = "数值状态字",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 200,
            DataType = TagDataType.UInt16,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(numeric);
        var typeError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SetRunningStateTagIdAsync(numeric.Id));
        Assert.Contains("布尔", typeError.Message);
    }

    [Fact]
    public async Task LegacyElementSettingDoesNotEnableMonitoring()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await system.Tags.SaveAsync(new Tag
        {
            Name = MonitoringDefaults.RunningStateTagName,
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 10,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        });
        // 旧版设置（M 编号）不再产生隐式监控，需用户显式选择 Tag
        await system.Store.SaveSettingsAsync(new Dictionary<string, string>
        {
            ["Monitoring.RunningStateElement"] = "10"
        });
        Assert.Null(await system.Store.GetRunningStateTagIdAsync());
    }
}