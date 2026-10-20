using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class TagWriteTests
{
    [Fact]
    public async Task NumericWriteVerifiesReadbackAndAudits()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = NewNumericTag(system, "功率设定", address: 10, maximum: 5000);
        await system.Tags.SaveAsync(tag);
        await system.Devices.ConnectAsync(system.Device.Id);
        var writes = new TagWriteService(system.Store, system.Communication, system.RecipeApplyLock);

        var result = await writes.WriteAsync(tag.Id, engineeringValue: 500);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1234d, result.PreviousValue);
        Assert.Equal(500d, result.ReadbackValue);
        Assert.Contains("已写入并验证", result.Message);
        Assert.Contains("→", result.Message);
        var register = await system.Communication.ReadAsync(
            system.Device, AddressType.HoldingRegister, 10, 1, CancellationToken.None);
        Assert.Equal(500, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(register));
        Assert.Contains(await system.Store.GetLogsAsync(),
            x => x.Source == "Tag" && x.Message.Contains("已写入并验证"));

        var unchanged = await writes.WriteAsync(tag.Id, engineeringValue: 500);
        Assert.True(unchanged.Succeeded);
        Assert.Contains("未重复写入", unchanged.Message);
    }

    [Fact]
    public async Task BoolCoilWriteVerifiesReadback()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "使能开关",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 20,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(tag);
        await system.Devices.ConnectAsync(system.Device.Id);
        var writes = new TagWriteService(system.Store, system.Communication, system.RecipeApplyLock);

        var result = await writes.WriteAsync(tag.Id, booleanValue: true);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(false, result.PreviousValue);
        Assert.Equal(true, result.ReadbackValue);
        Assert.Contains("关 → 开", result.Message);
        var coil = await system.Communication.ReadAsync(
            system.Device, AddressType.Coil, 20, 1, CancellationToken.None);
        Assert.True((coil[0] & 1) != 0);
    }

    [Fact]
    public async Task WriteRejectionsCoverPolicyRangeEncodingAndOfflineRules()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var readOnly = NewNumericTag(system, "只读量", address: 100, maximum: 5000);
        readOnly.AccessMode = AccessMode.ReadOnly;
        await system.Tags.SaveAsync(readOnly);
        var disabled = NewNumericTag(system, "禁用量", address: 102, maximum: 5000);
        disabled.Enabled = false;
        await system.Tags.SaveAsync(disabled);
        var bounded = NewNumericTag(system, "有界量", address: 104, maximum: 100);
        await system.Tags.SaveAsync(bounded);
        var integer = NewNumericTag(system, "整数设定", address: 106, maximum: 5000);
        integer.DataType = TagDataType.Int16;
        await system.Tags.SaveAsync(integer);
        var manualBound = new Tag
        {
            Name = "手动输出绑定量",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 30,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(manualBound);
        var manualOutputs = new ManualOutputService(system.Store, system.Communication);
        var output = (await manualOutputs.GetOutputBindingsAsync(system.Device.Id)).First();
        await manualOutputs.SaveOutputPointConfigurationAsync(
            system.Device.Id, output.Point.Id, output.Point.Name, manualBound.Id);
        await system.Devices.ConnectAsync(system.Device.Id);
        var writes = new TagWriteService(system.Store, system.Communication, system.RecipeApplyLock);

        var readOnlyResult = await writes.WriteAsync(readOnly.Id, engineeringValue: 10);
        Assert.False(readOnlyResult.Succeeded);
        Assert.Contains("未授权读写", readOnlyResult.Message);

        var disabledResult = await writes.WriteAsync(disabled.Id, engineeringValue: 10);
        Assert.False(disabledResult.Succeeded);
        Assert.Contains("已禁用", disabledResult.Message);

        var manualResult = await writes.WriteAsync(manualBound.Id, booleanValue: true);
        Assert.False(manualResult.Succeeded);
        Assert.Contains("手动输出", manualResult.Message);

        var rangeResult = await writes.WriteAsync(bounded.Id, engineeringValue: 101);
        Assert.False(rangeResult.Succeeded);
        Assert.Contains("超出工程量程", rangeResult.Message);

        var encodingResult = await writes.WriteAsync(integer.Id, engineeringValue: 45.5);
        Assert.False(encodingResult.Succeeded);
        Assert.Contains("无法编码", encodingResult.Message);

        var typeResult = await writes.WriteAsync(bounded.Id, booleanValue: true);
        Assert.False(typeResult.Succeeded);
        Assert.Contains("需要有效的数值工程值", typeResult.Message);

        await system.Devices.DisconnectAsync(system.Device.Id);
        var offlineResult = await writes.WriteAsync(bounded.Id, engineeringValue: 50);
        Assert.False(offlineResult.Succeeded);
        Assert.Contains("未在线", offlineResult.Message);
    }

    [Fact]
    public async Task WriteReadbackMismatchIsReportedAndAuditedAsWarning()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = NewNumericTag(system, "读回不一致量", address: 10, maximum: 5000);
        await system.Tags.SaveAsync(tag);
        await system.Devices.ConnectAsync(system.Device.Id);
        var communication = new MismatchedReadback(
            system.Communication, address: 10, TagValueCodec.Encode(tag, 999));
        var writes = new TagWriteService(system.Store, communication, system.RecipeApplyLock);

        var result = await writes.WriteAsync(tag.Id, engineeringValue: 500);

        Assert.False(result.Succeeded);
        Assert.Contains("不一致", result.Message);
        Assert.Contains(await system.Store.GetLogsAsync(),
            x => x.Source == "Tag" && x.Message.Contains("不一致"));
    }

    [Fact]
    public async Task WriteIsBlockedWhenAnotherInstanceHoldsTheDeviceLock()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = NewNumericTag(system, "锁占用量", address: 10, maximum: 5000);
        await system.Tags.SaveAsync(tag);
        await system.Devices.ConnectAsync(system.Device.Id);
        var writes = new TagWriteService(system.Store, system.Communication, new BusyRecipeApplyLock());

        var result = await writes.WriteAsync(tag.Id, engineeringValue: 500);

        Assert.False(result.Succeeded);
        Assert.Contains("另一个应用实例", result.Message);
        var register = await system.Communication.ReadAsync(
            system.Device, AddressType.HoldingRegister, 10, 1, CancellationToken.None);
        Assert.Equal(1234, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(register));
    }

    private static Tag NewNumericTag(TestSystem system, string name, int address, double maximum) => new()
    {
        Name = name,
        DeviceId = system.Device.Id,
        AddressType = AddressType.HoldingRegister,
        Address = address,
        DataType = TagDataType.UInt16,
        AccessMode = AccessMode.ReadWrite,
        MinValue = 0,
        MaxValue = maximum
    };

    private sealed class BusyRecipeApplyLock : IRecipeApplyLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(
            string resourceKey, TimeSpan waitTimeout, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);
    }

    // 首次读取（读取原值）正常通过；写入后的读回返回错误值，模拟 PLC 未真正接受写入。
    private sealed class MismatchedReadback(IDeviceCommunication inner, ushort address, byte[] wrongValue)
        : IDeviceCommunication
    {
        private int _reads;

        public DeviceSnapshot GetSnapshot(Guid id) => inner.GetSnapshot(id);
        public IReadOnlyList<DeviceSnapshot> GetSnapshots() => inner.GetSnapshots();
        public Task<CommunicationTestResult> TestAsync(Device device, CancellationToken ct = default)
            => inner.TestAsync(device, ct);
        public Task ConnectAsync(Device device, CancellationToken ct = default) => inner.ConnectAsync(device, ct);
        public Task DisconnectAsync(Guid id, CancellationToken ct = default) => inner.DisconnectAsync(id, ct);
        public Task InvalidateAsync(Guid id, CancellationToken ct = default) => inner.InvalidateAsync(id, ct);
        public Task PollAsync(Device device, CancellationToken ct) => inner.PollAsync(device, ct);

        public Task<byte[]> ReadAsync(
            Device device, AddressType type, ushort readAddress, ushort count, CancellationToken ct)
            => readAddress == address && Interlocked.Increment(ref _reads) >= 2
                ? Task.FromResult(wrongValue.ToArray())
                : inner.ReadAsync(device, type, readAddress, count, ct);

        public Task WriteCoilAsync(Device device, ushort writeAddress, bool value, CancellationToken ct = default)
            => inner.WriteCoilAsync(device, writeAddress, value, ct);
        public Task WriteSingleRegisterAsync(Device device, ushort writeAddress, ushort value, CancellationToken ct = default)
            => inner.WriteSingleRegisterAsync(device, writeAddress, value, ct);
        public Task WriteRegistersAsync(Device device, ushort writeAddress, byte[] networkBytes, CancellationToken ct = default)
            => inner.WriteRegistersAsync(device, writeAddress, networkBytes, ct);
    }
}
