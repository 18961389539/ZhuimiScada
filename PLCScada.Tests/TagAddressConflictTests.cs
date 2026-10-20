using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class TagAddressConflictTests
{
    [Fact]
    public void AddressConflictDetectionCoversRangesTypesAndDevices()
    {
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var floatTag = Make("双字量", deviceA, AddressType.HoldingRegister, 100, TagDataType.Float32);
        var overlap = Make("重叠单字", deviceA, AddressType.HoldingRegister, 101, TagDataType.UInt16);
        var adjacent = Make("相邻单字", deviceA, AddressType.HoldingRegister, 102, TagDataType.UInt16);
        var coil = Make("同地址线圈", deviceA, AddressType.Coil, 100, TagDataType.Bool);
        var otherDevice = Make("他设备量", deviceB, AddressType.HoldingRegister, 100, TagDataType.Float32);
        var all = new[] { floatTag, overlap, adjacent, coil, otherDevice };

        var floatConflicts = TagService.FindAddressConflicts(all, floatTag);
        Assert.Single(floatConflicts);
        Assert.Equal(overlap.Id, floatConflicts[0].Id);

        var overlapConflicts = TagService.FindAddressConflicts(all, overlap);
        Assert.Single(overlapConflicts);
        Assert.Equal(floatTag.Id, overlapConflicts[0].Id);

        Assert.Empty(TagService.FindAddressConflicts(all, adjacent));
        Assert.Empty(TagService.FindAddressConflicts(all, coil));
        Assert.Empty(TagService.FindAddressConflicts(all, otherDevice));

        var map = TagService.FindAddressConflictsByTag([floatTag, overlap, adjacent]);
        Assert.Equal(2, map.Count);
        Assert.Single(map[floatTag.Id]);
        Assert.Single(map[overlap.Id]);

        var single = TagService.FindAddressConflictsByTag([floatTag]);
        Assert.Empty(single);
    }

    [Fact]
    public async Task OverlappingTagsStillSaveAndAreMarkedAsConflicts()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var aliasSource = new Tag
        {
            Name = "别名源",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.Float32,
            AccessMode = AccessMode.ReadWrite,
            Scale = 0.1,
            Unit = "kW"
        };
        await system.Tags.SaveAsync(aliasSource);
        var aliasView = new Tag
        {
            Name = "别名视图",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.Float32,
            AccessMode = AccessMode.ReadOnly
        };

        // 允许保存（只提示不硬阻断）：同一寄存器的别名视图是合法用法。
        await system.Tags.SaveAsync(aliasView);

        var all = await system.Tags.GetAllAsync();
        Assert.Equal(2, all.Count);
        var map = TagService.FindAddressConflictsByTag(all);
        Assert.Equal(2, map.Count);
        Assert.Equal("别名视图", Assert.Single(map[aliasSource.Id]).Name);
        Assert.Equal("别名源", Assert.Single(map[aliasView.Id]).Name);
    }

    private static Tag Make(
        string name, Guid deviceId, AddressType addressType, int address, TagDataType dataType) => new()
    {
        Name = name,
        DeviceId = deviceId,
        AddressType = addressType,
        Address = address,
        DataType = dataType,
        AccessMode = addressType is AddressType.Coil ? AccessMode.ReadWrite : AccessMode.ReadOnly
    };
}
