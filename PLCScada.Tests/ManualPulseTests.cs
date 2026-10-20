using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class ManualPulseTests
{
    [Fact]
    public async Task PulseWritesThenResetsWithConfiguredDurationAndAudits()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SavePulseTagAsync(system);
        await system.Devices.ConnectAsync(system.Device.Id);
        var manual = new ManualOutputService(system.Store, system.Communication);
        var point = (await manual.GetOutputBindingsAsync(system.Device.Id)).First().Point;
        await manual.SaveOutputPointConfigurationAsync(
            system.Device.Id, point.Id, point.Name, tag.Id,
            outputMode: ManualOutputMode.Pulse, pulseDurationMs: 120);
        var recording = new RecordingCoilWrites(system.Communication);

        var pulseService = new ManualOutputService(system.Store, recording);
        await pulseService.PulseOutputAsync(system.Device.Id, point.Id, tag.Id, (ushort)tag.Address);

        var writes = recording.Snapshot();
        Assert.Equal(2, writes.Count);
        Assert.True(writes[0].Value);
        Assert.False(writes[1].Value);
        Assert.True(writes[1].ElapsedMs - writes[0].ElapsedMs >= 100,
            $"脉冲间隔 {writes[1].ElapsedMs - writes[0].ElapsedMs} ms 小于配置的 120 ms");
        var coil = await system.Communication.ReadAsync(
            system.Device, AddressType.Coil, (ushort)tag.Address, 1, CancellationToken.None);
        Assert.Equal(0, coil[0] & 1);
        Assert.Contains(await system.Store.GetLogsAsync(),
            x => x.Message.Contains("完成 120 ms 脉冲"));
    }

    [Fact]
    public async Task PulseResetRunsWhenCallerCancelsDuringWait()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SavePulseTagAsync(system);
        await system.Devices.ConnectAsync(system.Device.Id);
        var manual = new ManualOutputService(system.Store, system.Communication);
        var point = (await manual.GetOutputBindingsAsync(system.Device.Id)).First().Point;
        await manual.SaveOutputPointConfigurationAsync(
            system.Device.Id, point.Id, point.Name, tag.Id,
            outputMode: ManualOutputMode.Pulse, pulseDurationMs: 500);
        using var cancellation = new CancellationTokenSource();
        var recording = new RecordingCoilWrites(system.Communication, cancellation);
        var pulseService = new ManualOutputService(system.Store, recording);

        // 置位写入完成后立即取消等待（模拟浏览器在脉冲期间断开）：复位仍必须执行
        await pulseService.PulseOutputAsync(
            system.Device.Id, point.Id, tag.Id, (ushort)tag.Address, cancellation.Token);

        var writes = recording.Snapshot();
        Assert.Equal(2, writes.Count);
        Assert.True(writes[0].Value);
        Assert.False(writes[1].Value);
        Assert.True(writes[1].ElapsedMs - writes[0].ElapsedMs < 400,
            $"等待被取消后未提前复位（间隔 {writes[1].ElapsedMs - writes[0].ElapsedMs} ms，配置 500 ms）");
        var coil = await system.Communication.ReadAsync(
            system.Device, AddressType.Coil, (ushort)tag.Address, 1, CancellationToken.None);
        Assert.Equal(0, coil[0] & 1);
    }

    [Fact]
    public async Task PulseValidatesModeAndDuration()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SavePulseTagAsync(system);
        var manual = new ManualOutputService(system.Store, system.Communication);
        var point = (await manual.GetOutputBindingsAsync(system.Device.Id)).First().Point;
        await manual.SaveOutputPointConfigurationAsync(
            system.Device.Id, point.Id, point.Name, tag.Id);

        var holdRejection = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.PulseOutputAsync(system.Device.Id, point.Id, tag.Id, (ushort)tag.Address));
        Assert.Contains("不是脉冲模式", holdRejection.Message);

        var tooShort = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.SaveOutputPointConfigurationAsync(
                system.Device.Id, point.Id, point.Name, tag.Id,
                outputMode: ManualOutputMode.Pulse, pulseDurationMs: 10));
        Assert.Contains("脉冲时长", tooShort.Message);
        var tooLong = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.SaveOutputPointConfigurationAsync(
                system.Device.Id, point.Id, point.Name, tag.Id,
                outputMode: ManualOutputMode.Pulse, pulseDurationMs: 20000));
        Assert.Contains("脉冲时长", tooLong.Message);

        await manual.SaveOutputPointConfigurationAsync(
            system.Device.Id, point.Id, point.Name, tag.Id,
            outputMode: ManualOutputMode.Pulse, pulseDurationMs: 500);
        var saved = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Id == point.Id);
        Assert.Equal(ManualOutputMode.Pulse, saved.Point.OutputMode);
        Assert.Equal(500, saved.Point.PulseDurationMs);

        // 仅改名（不传模式参数）必须保留既有脉冲配置
        await manual.UpdateOutputPointAsync(system.Device.Id, point.Id, $"{point.Name}改");
        var renamed = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Id == point.Id);
        Assert.Equal(ManualOutputMode.Pulse, renamed.Point.OutputMode);
        Assert.Equal(500, renamed.Point.PulseDurationMs);
    }

    [Fact]
    public async Task PulseFailuresAreReportedAndAudited()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SavePulseTagAsync(system);
        await system.Devices.ConnectAsync(system.Device.Id);
        var manual = new ManualOutputService(system.Store, system.Communication);
        var point = (await manual.GetOutputBindingsAsync(system.Device.Id)).First().Point;
        await manual.SaveOutputPointConfigurationAsync(
            system.Device.Id, point.Id, point.Name, tag.Id,
            outputMode: ManualOutputMode.Pulse, pulseDurationMs: 60);

        // 置位写入失败：审计 Warning 并向上抛出
        var setFailureService = new ManualOutputService(
            system.Store, new FailNthCoilWrite(system.Communication, 1));
        await Assert.ThrowsAsync<IOException>(() =>
            setFailureService.PulseOutputAsync(system.Device.Id, point.Id, tag.Id, (ushort)tag.Address));
        Assert.Contains(await system.Store.GetLogsAsync(),
            x => x.Level == "Warning" && x.Message.Contains("脉冲写入 1 失败"));

        // 复位写入失败：明确报告“可能保持为 1”并审计
        var resetFailureService = new ManualOutputService(
            system.Store, new FailNthCoilWrite(system.Communication, 2));
        var resetFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resetFailureService.PulseOutputAsync(system.Device.Id, point.Id, tag.Id, (ushort)tag.Address));
        Assert.Contains("保持为 1", resetFailure.Message);
        Assert.Contains("复位写入失败", resetFailure.Message);
        Assert.Contains(await system.Store.GetLogsAsync(),
            x => x.Level == "Warning" && x.Message.Contains("复位写入失败"));
    }

    private static async Task<Tag> SavePulseTagAsync(TestSystem system)
    {
        var tag = new Tag
        {
            Name = "脉冲线圈",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 20,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(tag);
        return tag;
    }

    private sealed class RecordingCoilWrites(IDeviceCommunication inner, CancellationTokenSource? cancelAfterFirstSet = null)
        : IDeviceCommunication
    {
        private readonly List<(bool Value, long ElapsedMs)> _writes = [];
        private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();

        public IReadOnlyList<(bool Value, long ElapsedMs)> Snapshot()
        {
            lock (_writes) return _writes.ToArray();
        }

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

        public async Task WriteCoilAsync(Device device, ushort address, bool value, CancellationToken ct = default)
        {
            await inner.WriteCoilAsync(device, address, value, ct);
            lock (_writes) _writes.Add((value, _watch.ElapsedMilliseconds));
            if (value) cancelAfterFirstSet?.Cancel();
        }

        public Task WriteSingleRegisterAsync(Device device, ushort address, ushort value, CancellationToken ct = default)
            => inner.WriteSingleRegisterAsync(device, address, value, ct);
        public Task WriteRegistersAsync(Device device, ushort address, byte[] networkBytes, CancellationToken ct = default)
            => inner.WriteRegistersAsync(device, address, networkBytes, ct);
    }

    private sealed class FailNthCoilWrite(IDeviceCommunication inner, int failOnWrite) : IDeviceCommunication
    {
        private int _writes;

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
            => Interlocked.Increment(ref _writes) == failOnWrite
                ? Task.FromException(new IOException($"测试注入：第 {failOnWrite} 次线圈写入失败。"))
                : inner.WriteCoilAsync(device, address, value, ct);

        public Task WriteSingleRegisterAsync(Device device, ushort address, ushort value, CancellationToken ct = default)
            => inner.WriteSingleRegisterAsync(device, address, value, ct);
        public Task WriteRegistersAsync(Device device, ushort address, byte[] networkBytes, CancellationToken ct = default)
            => inner.WriteRegistersAsync(device, address, networkBytes, ct);
    }
}
