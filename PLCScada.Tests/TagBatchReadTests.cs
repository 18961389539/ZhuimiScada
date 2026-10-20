using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PLCScada.Application;
using PLCScada.Core;
using PLCScada.Infrastructure.Communication;

namespace PLCScada.Tests;

public sealed class TagBatchReadTests
{
    [Fact]
    public async Task BatchReadProducesSameValuesAsIndividualReads()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tagA = NewTag(system, "合批A", 100, TagDataType.UInt16);
        var tagB = NewTag(system, "合批B", 101, TagDataType.Int16);
        tagB.MinValue = -1000;
        var tagC = NewTag(system, "合批C", 102, TagDataType.Float32);
        await system.Tags.SaveAsync(tagA);
        await system.Tags.SaveAsync(tagB);
        await system.Tags.SaveAsync(tagC);
        await system.Communication.WriteSingleRegisterAsync(system.Device, 100, 111, CancellationToken.None);
        await system.Communication.WriteSingleRegisterAsync(
            system.Device, 101, unchecked((ushort)(short)-22), CancellationToken.None);
        await system.Communication.WriteRegistersAsync(
            system.Device, 102, TagValueCodec.Encode(tagC, 3.25), CancellationToken.None);

        var group = Assert.Single(TagReadGrouping.BuildGroups([tagA, tagB, tagC]));
        Assert.Equal(AddressType.HoldingRegister, group.AddressType);
        Assert.Equal(100, group.StartAddress);
        Assert.Equal(4, group.RegisterCount);

        var runtime = system.Services.GetRequiredService<TagRuntime>();
        await runtime.ReadGroupAsync(
            system.Device, group.AddressType, (ushort)group.StartAddress,
            (ushort)group.RegisterCount, group.Tags, CancellationToken.None);

        var snapshotA = system.Runtime.GetTagSnapshot(tagA.Id);
        Assert.True(snapshotA.Quality == TagQuality.Good, $"A: {snapshotA.Quality} / {snapshotA.LastError}");
        Assert.Equal(111d, snapshotA.CurrentValue);
        var snapshotB = system.Runtime.GetTagSnapshot(tagB.Id);
        Assert.True(snapshotB.Quality == TagQuality.Good, $"B: {snapshotB.Quality} / {snapshotB.LastError}");
        Assert.Equal(-22d, snapshotB.CurrentValue);
        var snapshotC = system.Runtime.GetTagSnapshot(tagC.Id);
        Assert.True(snapshotC.Quality == TagQuality.Good, $"C: {snapshotC.Quality} / {snapshotC.LastError}");
        Assert.Equal(3.25d, snapshotC.CurrentValue);

        // 与逐 Tag 读取结果一致
        Assert.Equal(111d, (await system.Runtime.GetTagValueAsync(tagA.Id)).CurrentValue);
        Assert.Equal(-22d, (await system.Runtime.GetTagValueAsync(tagB.Id)).CurrentValue);
        Assert.Equal(3.25d, (await system.Runtime.GetTagValueAsync(tagC.Id)).CurrentValue);
    }

    [Fact]
    public void GroupingSplitsByAddressTypeGapAndSize()
    {
        var contiguous = new[]
        {
            NewDetachedTag("连续一", 100, TagDataType.UInt16),
            NewDetachedTag("连续二", 101, TagDataType.UInt16),
            NewDetachedTag("跨双字", 103, TagDataType.Float32)
        };
        var single = Assert.Single(TagReadGrouping.BuildGroups(contiguous));
        Assert.Equal(100, single.StartAddress);
        Assert.Equal(5, single.RegisterCount);

        var withGap = contiguous.Append(NewDetachedTag("远隔", 200, TagDataType.UInt16)).ToArray();
        var gapGroups = TagReadGrouping.BuildGroups(withGap);
        Assert.Equal(2, gapGroups.Count);
        Assert.Equal((100, 5), (gapGroups[0].StartAddress, gapGroups[0].RegisterCount));
        Assert.Equal((200, 1), (gapGroups[1].StartAddress, gapGroups[1].RegisterCount));

        var withInputRegister = withGap
            .Append(new Tag
            {
                Name = "输入寄存器",
                AddressType = AddressType.InputRegister,
                Address = 100,
                DataType = TagDataType.UInt16
            })
            .ToArray();
        var typed = TagReadGrouping.BuildGroups(withInputRegister);
        Assert.Equal(3, typed.Count);
        Assert.Single(typed, x => x.AddressType == AddressType.InputRegister);
        Assert.Equal(2, typed.Count(x => x.AddressType == AddressType.HoldingRegister));

        var many = Enumerable.Range(0, 130)
            .Select(index => NewDetachedTag($"密排{index}", index, TagDataType.UInt16))
            .ToArray();
        var split = TagReadGrouping.BuildGroups(many);
        Assert.Equal(2, split.Count);
        Assert.Equal((0, 120), (split[0].StartAddress, split[0].RegisterCount));
        Assert.Equal((120, 10), (split[1].StartAddress, split[1].RegisterCount));
    }

    [Fact]
    public async Task BatchReadFailureMarksAllGroupTagsBadAndKeepsLastValues()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tagA = NewTag(system, "批读失败A", 100, TagDataType.UInt16);
        var tagB = NewTag(system, "批读失败B", 101, TagDataType.UInt16);
        await system.Tags.SaveAsync(tagA);
        await system.Tags.SaveAsync(tagB);
        var switchable = new SwitchableCommunication(system.Communication);
        var runtime = new TagRuntime(system.Store, switchable);

        await system.Communication.WriteSingleRegisterAsync(system.Device, 100, 555, CancellationToken.None);
        var before = await runtime.GetTagValueAsync(tagA.Id);
        Assert.Equal(TagQuality.Good, before.Quality);
        Assert.Equal(555d, before.CurrentValue);

        switchable.FailReads = true;
        await runtime.ReadGroupAsync(
            system.Device, AddressType.HoldingRegister, 100, 2, [tagA, tagB], CancellationToken.None);

        var afterA = runtime.GetTagSnapshot(tagA.Id);
        Assert.Equal(TagQuality.Bad, afterA.Quality);
        Assert.Equal(555d, afterA.CurrentValue);
        Assert.Contains("测试注入", afterA.LastError);
        Assert.Equal(TagQuality.Bad, runtime.GetTagSnapshot(tagB.Id).Quality);
    }

    [Fact]
    public async Task PollingReadsContiguousNumericTagsEndToEnd()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tagA = NewTag(system, "批读A", 100, TagDataType.UInt16);
        var tagB = NewTag(system, "批读B", 101, TagDataType.UInt16);
        await system.Tags.SaveAsync(tagA);
        await system.Tags.SaveAsync(tagB);
        await system.Communication.WriteSingleRegisterAsync(system.Device, 100, 111, CancellationToken.None);
        await system.Communication.WriteSingleRegisterAsync(system.Device, 101, 222, CancellationToken.None);

        using var polling = new ModbusPollingService(
            system.Store, system.Communication,
            system.Services.GetRequiredService<TagRuntime>(),
            NullLogger<ModbusPollingService>.Instance);
        await polling.StartAsync(default);
        try
        {
            await WaitUntilAsync(() =>
                system.Runtime.GetTagSnapshot(tagA.Id).Quality == TagQuality.Good &&
                system.Runtime.GetTagSnapshot(tagB.Id).Quality == TagQuality.Good);
            Assert.Equal(111d, system.Runtime.GetTagSnapshot(tagA.Id).CurrentValue);
            Assert.Equal(222d, system.Runtime.GetTagSnapshot(tagB.Id).CurrentValue);
        }
        finally
        {
            await polling.StopAsync(default);
        }
    }

    [Fact]
    public async Task BitBatchReadMatchesIndividualReads()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var bitA = NewBitTag(system, "位A", 50);
        var bitB = NewBitTag(system, "位B", 51);
        var bitC = NewBitTag(system, "位C", 57);
        await system.Tags.SaveAsync(bitA);
        await system.Tags.SaveAsync(bitB);
        await system.Tags.SaveAsync(bitC);
        await system.Devices.ConnectAsync(system.Device.Id);
        await system.Communication.WriteCoilAsync(system.Device, 50, true, CancellationToken.None);
        await system.Communication.WriteCoilAsync(system.Device, 51, false, CancellationToken.None);
        await system.Communication.WriteCoilAsync(system.Device, 57, true, CancellationToken.None);

        var group = Assert.Single(TagReadGrouping.BuildBitGroups([bitA, bitB, bitC]));
        Assert.Equal(AddressType.Coil, group.AddressType);
        Assert.Equal(50, group.StartAddress);
        Assert.Equal(8, group.BitCount);

        var runtime = system.Services.GetRequiredService<TagRuntime>();
        await runtime.ReadBitGroupAsync(
            system.Device, group.AddressType, (ushort)group.StartAddress,
            (ushort)group.BitCount, group.Tags, CancellationToken.None);

        Assert.True((bool)system.Runtime.GetTagSnapshot(bitA.Id).CurrentValue!);
        Assert.False((bool)system.Runtime.GetTagSnapshot(bitB.Id).CurrentValue!);
        Assert.True((bool)system.Runtime.GetTagSnapshot(bitC.Id).CurrentValue!);
        Assert.Equal(TagQuality.Good, system.Runtime.GetTagSnapshot(bitC.Id).Quality);

        // 与逐点读取一致
        Assert.True((bool)(await system.Runtime.GetTagValueAsync(bitC.Id)).CurrentValue!);
    }

    [Fact]
    public void BitGroupingSplitsByTypeGapAndSpan()
    {
        var coils = new[]
        {
            NewDetachedBitTag("线圈一", 50, AddressType.Coil),
            NewDetachedBitTag("线圈二", 51, AddressType.Coil),
            NewDetachedBitTag("线圈三", 58, AddressType.Coil)
        };
        var single = Assert.Single(TagReadGrouping.BuildBitGroups(coils));
        Assert.Equal(50, single.StartAddress);
        Assert.Equal(9, single.BitCount);

        var withDiscrete = coils
            .Append(NewDetachedBitTag("输入点", 50, AddressType.DiscreteInput))
            .ToArray();
        Assert.Equal(2, TagReadGrouping.BuildBitGroups(withDiscrete).Count);

        var many = Enumerable.Range(0, 600)
            .Select(index => NewDetachedBitTag($"位{index}", index, AddressType.Coil))
            .ToArray();
        var split = TagReadGrouping.BuildBitGroups(many);
        Assert.Equal(2, split.Count);
        Assert.Equal((0, 512), (split[0].StartAddress, split[0].BitCount));
        Assert.Equal((512, 88), (split[1].StartAddress, split[1].BitCount));
    }

    private static Tag NewBitTag(TestSystem system, string name, int address) => new()
    {
        Name = name,
        DeviceId = system.Device.Id,
        AddressType = AddressType.Coil,
        Address = address,
        DataType = TagDataType.Bool,
        AccessMode = AccessMode.ReadWrite
    };

    private static Tag NewDetachedBitTag(string name, int address, AddressType addressType) => new()
    {
        Name = name,
        AddressType = addressType,
        Address = address,
        DataType = TagDataType.Bool
    };

    private static Tag NewTag(TestSystem system, string name, int address, TagDataType dataType) => new()
    {
        Name = name,
        DeviceId = system.Device.Id,
        AddressType = AddressType.HoldingRegister,
        Address = address,
        DataType = dataType,
        AccessMode = AccessMode.ReadOnly,
        MinValue = 0,
        MaxValue = 100000
    };

    private static Tag NewDetachedTag(string name, int address, TagDataType dataType) => new()
    {
        Name = name,
        AddressType = AddressType.HoldingRegister,
        Address = address,
        DataType = dataType
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
            await Task.Delay(40, timeout.Token);
    }

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
