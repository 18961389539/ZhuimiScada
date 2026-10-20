using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class ManualResetAndOrderTests
{
    [Fact]
    public async Task ResetAllWritesZeroToBoundOutputsAndSkipsUnbound()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var first = await SaveCoilTagAsync(system, "复位测试一", 20);
        var second = await SaveCoilTagAsync(system, "复位测试二", 21);
        await system.Devices.ConnectAsync(system.Device.Id);
        var manual = new ManualOutputService(system.Store, system.Communication);
        var points = (await manual.GetOutputBindingsAsync(system.Device.Id)).Select(x => x.Point).ToArray();
        await manual.SaveOutputPointConfigurationAsync(system.Device.Id, points[0].Id, points[0].Name, first.Id);
        await manual.SaveOutputPointConfigurationAsync(system.Device.Id, points[1].Id, points[1].Name, second.Id);
        await system.Communication.WriteCoilAsync(system.Device, 20, true, CancellationToken.None);
        await system.Communication.WriteCoilAsync(system.Device, 21, true, CancellationToken.None);

        var result = await manual.ResetAllOutputsAsync(system.Device.Id);

        Assert.Equal(2, result.ResetCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(12, result.SkippedCount);
        Assert.Equal(2, result.Items.Count(x => x.Status == "已归零"));
        var firstCoil = await system.Communication.ReadAsync(
            system.Device, AddressType.Coil, 20, 1, CancellationToken.None);
        var secondCoil = await system.Communication.ReadAsync(
            system.Device, AddressType.Coil, 21, 1, CancellationToken.None);
        Assert.Equal(0, firstCoil[0] & 1);
        Assert.Equal(0, secondCoil[0] & 1);
        Assert.Equal(2, (await system.Store.GetLogsAsync())
            .Count(x => x.Message.Contains("全部归零写入 0")));
    }

    [Fact]
    public async Task ResetAllContinuesAfterSingleFailureAndAuditsIt()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var first = await SaveCoilTagAsync(system, "复位失败一", 20);
        var second = await SaveCoilTagAsync(system, "复位继续二", 21);
        await system.Devices.ConnectAsync(system.Device.Id);
        var manual = new ManualOutputService(system.Store, system.Communication);
        var points = (await manual.GetOutputBindingsAsync(system.Device.Id)).Select(x => x.Point).ToArray();
        await manual.SaveOutputPointConfigurationAsync(system.Device.Id, points[0].Id, points[0].Name, first.Id);
        await manual.SaveOutputPointConfigurationAsync(system.Device.Id, points[1].Id, points[1].Name, second.Id);
        await system.Communication.WriteCoilAsync(system.Device, 20, true, CancellationToken.None);
        await system.Communication.WriteCoilAsync(system.Device, 21, true, CancellationToken.None);

        var failing = new ManualOutputService(
            system.Store, new FailAddressWrite(system.Communication, failAddress: 20));
        var result = await failing.ResetAllOutputsAsync(system.Device.Id);

        Assert.Equal(1, result.ResetCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(12, result.SkippedCount);
        var failedItem = Assert.Single(result.Items, x => x.Status == "失败");
        Assert.Equal(points[0].Name, failedItem.PointName);
        // 首个按钮失败不中断其余按钮：第二个地址应已被归零
        var secondCoil = await system.Communication.ReadAsync(
            system.Device, AddressType.Coil, 21, 1, CancellationToken.None);
        Assert.Equal(0, secondCoil[0] & 1);
        Assert.Contains(await system.Store.GetLogsAsync(),
            x => x.Level == "Warning" && x.Message.Contains("全部归零写入失败"));
    }

    [Fact]
    public async Task ResetAllWithNoBindingsReportsAllSkipped()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var manual = new ManualOutputService(system.Store, system.Communication);

        var result = await manual.ResetAllOutputsAsync(system.Device.Id);

        Assert.Equal(0, result.ResetCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(14, result.SkippedCount);
    }

    [Fact]
    public async Task MoveOutputPointPersistsOrderAndHandlesBoundaries()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var manual = new ManualOutputService(system.Store, system.Communication);
        var initial = (await manual.GetOutputBindingsAsync(system.Device.Id)).Select(x => x.Point.Id).ToArray();
        Assert.Equal(14, initial.Length);

        await manual.MoveOutputPointAsync(system.Device.Id, initial[1], -1);
        var moved = (await manual.GetOutputBindingsAsync(system.Device.Id)).Select(x => x.Point.Id).ToArray();
        Assert.Equal(new[] { initial[1], initial[0] }.Concat(initial[2..]), moved);

        await manual.MoveOutputPointAsync(system.Device.Id, moved[0], -1);
        await manual.MoveOutputPointAsync(system.Device.Id, moved[^1], 1);
        var unchanged = (await manual.GetOutputBindingsAsync(system.Device.Id)).Select(x => x.Point.Id).ToArray();
        Assert.Equal(moved, unchanged);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            manual.MoveOutputPointAsync(system.Device.Id, moved[0], 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.MoveOutputPointAsync(system.Device.Id, Guid.NewGuid(), 1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.UpdateManualOutputPointOrderAsync(system.Device.Id, [moved[0]]));
        var duplicated = moved.ToArray();
        duplicated[0] = duplicated[1];
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.UpdateManualOutputPointOrderAsync(system.Device.Id, duplicated));
        var unknown = moved.ToArray();
        unknown[0] = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.UpdateManualOutputPointOrderAsync(system.Device.Id, unknown));

        var reversed = moved.Reverse().ToArray();
        await system.Store.UpdateManualOutputPointOrderAsync(system.Device.Id, reversed);
        var persisted = (await manual.GetOutputBindingsAsync(system.Device.Id)).Select(x => x.Point.Id).ToArray();
        Assert.Equal(reversed, persisted);
        Assert.Contains(await system.Store.GetLogsAsync(), x => x.Message.Contains("调整输出按钮顺序"));
    }

    private static async Task<Tag> SaveCoilTagAsync(TestSystem system, string name, int address)
    {
        var tag = new Tag
        {
            Name = name,
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = address,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(tag);
        return tag;
    }

    private sealed class FailAddressWrite(IDeviceCommunication inner, ushort failAddress) : IDeviceCommunication
    {
        public DeviceSnapshot GetSnapshot(Guid id) => inner.GetSnapshot(id);
        public IReadOnlyList<DeviceSnapshot> GetSnapshots() => inner.GetSnapshots();
        public Task<CommunicationTestResult> TestAsync(Device device, CancellationToken ct = default)
            => inner.TestAsync(device, ct);
        public Task ConnectAsync(Device device, CancellationToken ct = default) => inner.ConnectAsync(device, ct);
        public Task DisconnectAsync(Guid id, CancellationToken ct = default) => inner.DisconnectAsync(id, ct);
        public Task InvalidateAsync(Guid id, CancellationToken ct = default) => inner.InvalidateAsync(id, ct);
        public Task PollAsync(Device device, CancellationToken ct) => inner.PollAsync(device, ct);
        public Task<byte[]> ReadAsync(Device device, AddressType type, ushort address, ushort count, CancellationToken ct)
            => inner.ReadAsync(device, type, address, count, ct);

        public Task WriteCoilAsync(Device device, ushort address, bool value, CancellationToken ct = default)
            => address == failAddress
                ? Task.FromException(new IOException("测试注入：该地址线圈写入失败。"))
                : inner.WriteCoilAsync(device, address, value, ct);

        public Task WriteSingleRegisterAsync(Device device, ushort address, ushort value, CancellationToken ct = default)
            => inner.WriteSingleRegisterAsync(device, address, value, ct);
        public Task WriteRegistersAsync(Device device, ushort address, byte[] networkBytes, CancellationToken ct = default)
            => inner.WriteRegistersAsync(device, address, networkBytes, ct);
    }
}
