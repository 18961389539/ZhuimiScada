using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class TagQualityAlarmTests
{
    [Fact]
    public async Task ConsecutiveFailuresRaiseQualityAlarmAndSuccessClears()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = NewTag(system, "质量报警点", 100);
        await system.Store.SaveQualityAlarmFailuresAsync(3);
        await system.Tags.SaveAsync(tag);
        var switchable = new SwitchableCommunication(system.Communication);
        var runtime = new TagRuntime(system.Store, switchable);

        switchable.FailReads = true;
        await runtime.GetTagValueAsync(tag.Id);
        await runtime.GetTagValueAsync(tag.Id);
        Assert.Empty(await QualityAlarmsAsync(system));

        await runtime.GetTagValueAsync(tag.Id);
        var alarm = Assert.Single(await QualityAlarmsAsync(system));
        Assert.Equal($"TagQuality:{tag.Id}", alarm.Source);
        Assert.Equal(AlarmStatus.Active, alarm.Status);
        Assert.Contains("连续 3 次读取失败", alarm.Message);

        // 继续失败不重复记录
        await runtime.GetTagValueAsync(tag.Id);
        Assert.Single(await QualityAlarmsAsync(system));

        // 读取恢复自动解除
        switchable.FailReads = false;
        var recovered = await runtime.GetTagValueAsync(tag.Id);
        Assert.Equal(TagQuality.Good, recovered.Quality);
        Assert.All(await QualityAlarmsAsync(system), x => Assert.Equal(AlarmStatus.ClearedUnacknowledged, x.Status));

        // 再次失败从 1 开始累计，不误报
        switchable.FailReads = true;
        await runtime.GetTagValueAsync(tag.Id);
        await runtime.GetTagValueAsync(tag.Id);
        Assert.All(await QualityAlarmsAsync(system), x => Assert.Equal(AlarmStatus.ClearedUnacknowledged, x.Status));

        // 设备禁用类失败（非通信失败）不计入
        var service = new TagService(system.Store);
        var reenableCandidate = (await service.GetAllAsync()).Single(x => x.Id == tag.Id);
        reenableCandidate.Enabled = false;
        await service.SaveAsync(reenableCandidate);
        await runtime.GetTagValueAsync(tag.Id);
        Assert.All(await QualityAlarmsAsync(system), x => Assert.Equal(AlarmStatus.ClearedUnacknowledged, x.Status));
    }

    [Fact]
    public async Task ThresholdZeroDisablesQualityAlarm()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = NewTag(system, "未配置质量报警", 100);
        await system.Tags.SaveAsync(tag);
        var switchable = new SwitchableCommunication(system.Communication);
        var runtime = new TagRuntime(system.Store, switchable);

        switchable.FailReads = true;
        for (var index = 0; index < 3; index++)
            await runtime.GetTagValueAsync(tag.Id);

        Assert.Empty(await QualityAlarmsAsync(system));
    }

    [Fact]
    public async Task BatchReadFailuresCountTowardQualityAlarm()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = NewTag(system, "合批质量报警点", 100);
        await system.Store.SaveQualityAlarmFailuresAsync(2);
        await system.Tags.SaveAsync(tag);
        var switchable = new SwitchableCommunication(system.Communication);
        var runtime = new TagRuntime(system.Store, switchable);

        switchable.FailReads = true;
        await runtime.ReadGroupAsync(
            system.Device, AddressType.HoldingRegister, 100, 1, [tag], CancellationToken.None);
        Assert.Empty(await QualityAlarmsAsync(system));
        await runtime.ReadGroupAsync(
            system.Device, AddressType.HoldingRegister, 100, 1, [tag], CancellationToken.None);
        var alarm = Assert.Single(await QualityAlarmsAsync(system));
        Assert.Equal(AlarmStatus.Active, alarm.Status);
        Assert.Contains("连续 2 次读取失败", alarm.Message);

        switchable.FailReads = false;
        await runtime.ReadGroupAsync(
            system.Device, AddressType.HoldingRegister, 100, 1, [tag], CancellationToken.None);
        Assert.All(await QualityAlarmsAsync(system), x => Assert.Equal(AlarmStatus.ClearedUnacknowledged, x.Status));
    }

    private static async Task<IReadOnlyList<AlarmRecord>> QualityAlarmsAsync(TestSystem system)
        => (await system.Store.GetAlarmsAsync())
            .Where(x => x.Source.StartsWith("TagQuality:", StringComparison.Ordinal))
            .ToArray();

    private static Tag NewTag(TestSystem system, string name, int address) => new()
    {
        Name = name,
        DeviceId = system.Device.Id,
        AddressType = AddressType.HoldingRegister,
        Address = address,
        DataType = TagDataType.UInt16,
        AccessMode = AccessMode.ReadWrite,
        MinValue = 0,
        MaxValue = 1000
    };

    private sealed class SwitchableCommunication(IDeviceCommunication inner) : IDeviceCommunication
    {
        public bool FailReads { get; set; }

        public DeviceSnapshot GetSnapshot(Guid id) => inner.GetSnapshot(id);
        public IReadOnlyList<DeviceSnapshot> GetSnapshots() => inner.GetSnapshots();
        public Task<CommunicationTestResult> TestAsync(Device device, CancellationToken ct = default)
            => inner.TestAsync(device, ct);
        public Task ConnectAsync(Device device, CancellationToken ct = default) => inner.ConnectAsync(device, ct);
        public Task DisconnectAsync(Guid id, CancellationToken ct = default) => inner.DisconnectAsync(id, ct);
        public Task InvalidateAsync(Guid id, CancellationToken ct = default) => inner.InvalidateAsync(id, ct);
        public Task PollAsync(Device device, CancellationToken ct) => inner.PollAsync(device, ct);

        public Task<byte[]> ReadAsync(Device device, AddressType type, ushort address, ushort count, CancellationToken ct)
            => FailReads
                ? Task.FromException<byte[]>(new IOException("测试注入：读取失败。"))
                : inner.ReadAsync(device, type, address, count, ct);

        public Task WriteCoilAsync(Device device, ushort address, bool value, CancellationToken ct = default)
            => inner.WriteCoilAsync(device, address, value, ct);
        public Task WriteSingleRegisterAsync(Device device, ushort address, ushort value, CancellationToken ct = default)
            => inner.WriteSingleRegisterAsync(device, address, value, ct);
        public Task WriteRegistersAsync(Device device, ushort address, byte[] networkBytes, CancellationToken ct = default)
            => inner.WriteRegistersAsync(device, address, networkBytes, ct);
    }
}
