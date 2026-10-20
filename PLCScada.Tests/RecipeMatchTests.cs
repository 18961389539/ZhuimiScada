using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class RecipeMatchTests
{
    [Fact]
    public async Task MatchingIdentifiesFullyMatchingAndPartialRecipes()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tagA = await SaveTagAsync(system, "识别参数A", 100, TagDataType.UInt16);
        var tagB = await SaveTagAsync(system, "识别参数B", 101, TagDataType.UInt16);
        var tagC = new Tag
        {
            Name = "识别开关",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 20,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(tagC);
        await system.Devices.ConnectAsync(system.Device.Id);
        var recipes = new RecipeService(system.Store);
        await recipes.SaveAsync(
            null, null, system.Device.Id, "匹配工况", "",
            [new(tagA.Id, 10), new(tagB.Id, 20), new RecipeService.ParameterInput(tagC.Id, BooleanValue: true)]);
        await recipes.SaveAsync(
            null, null, system.Device.Id, "偏差工况", "",
            [new(tagA.Id, 10), new(tagB.Id, 99)]);
        await system.Communication.WriteSingleRegisterAsync(system.Device, 100, 10, CancellationToken.None);
        await system.Communication.WriteSingleRegisterAsync(system.Device, 101, 20, CancellationToken.None);
        await system.Communication.WriteCoilAsync(system.Device, 20, true, CancellationToken.None);

        var applier = new RecipeApplyService(system.Store, system.Communication, system.RecipeApplyLock);
        var result = await applier.MatchRecipesForDeviceAsync(system.Device.Id);

        Assert.Null(result.BlockReason);
        Assert.Equal(system.Device.Name, result.DeviceName);
        Assert.Equal(2, result.Items.Count);
        var matching = Assert.Single(result.Items, x => x.RecipeName == "匹配工况");
        Assert.True(matching.FullyMatches);
        Assert.Equal(3, matching.MatchingItems);
        Assert.Equal(3, matching.TotalItems);
        Assert.Null(matching.Error);
        var partial = Assert.Single(result.Items, x => x.RecipeName == "偏差工况");
        Assert.False(partial.FullyMatches);
        Assert.Equal(1, partial.MatchingItems);
        Assert.Equal(2, partial.TotalItems);
        Assert.Null(partial.Error);
    }

    [Fact]
    public async Task MatchingBlocksOnOfflineEmptyOrMissingDevice()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveTagAsync(system, "识别单参数", 100, TagDataType.UInt16);
        var recipes = new RecipeService(system.Store);
        await recipes.SaveAsync(null, null, system.Device.Id, "待识别", "", [new(tag.Id, 5)]);
        var applier = new RecipeApplyService(system.Store, system.Communication, system.RecipeApplyLock);

        // 未在线
        var offline = await applier.MatchRecipesForDeviceAsync(system.Device.Id);
        Assert.Contains("未在线", offline.BlockReason);
        Assert.Empty(offline.Items);

        // 没有配方的设备
        var emptyDevice = new Device { Name = "无配方设备" };
        await system.Devices.SaveAsync(emptyDevice);
        var empty = await applier.MatchRecipesForDeviceAsync(emptyDevice.Id);
        Assert.Contains("还没有配方", empty.BlockReason);

        // 不存在的设备
        var missing = await applier.MatchRecipesForDeviceAsync(Guid.NewGuid());
        Assert.Contains("不存在或已禁用", missing.BlockReason);
    }

    private static async Task<Tag> SaveTagAsync(TestSystem system, string name, int address, TagDataType dataType)
    {
        var tag = new Tag
        {
            Name = name,
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = address,
            DataType = dataType,
            AccessMode = AccessMode.ReadWrite,
            MinValue = 0,
            MaxValue = 1000
        };
        await system.Tags.SaveAsync(tag);
        return tag;
    }
}
