using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class RecipeRevisionDiffTests
{
    [Fact]
    public async Task RevisionComparisonReportsChangesAddedRemovedAndMetadata()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var power = NewTag(system, "功率上限", 100);
        var legacy = NewTag(system, "旧参数", 102);
        var keeper = NewTag(system, "保持参数", 104);
        var added = NewTag(system, "新增参数", 106);
        await system.Tags.SaveAsync(power);
        await system.Tags.SaveAsync(legacy);
        await system.Tags.SaveAsync(keeper);
        await system.Tags.SaveAsync(added);
        var service = new RecipeService(system.Store);

        var recipeId = await service.SaveAsync(
            null, null, system.Device.Id, "工况A", "初始说明",
            [new(power.Id, 10), new(legacy.Id, 5), new(keeper.Id, 7)],
            changeNote: "初始版本");
        var created = Assert.Single(await service.GetAllAsync());
        await service.SaveAsync(
            recipeId, created.Revision, system.Device.Id, "工况B", "更新说明",
            [new(power.Id, 25), new(keeper.Id, 7), new(added.Id, 3)],
            changeNote: "调整参数");

        var diff = await service.CompareRevisionsAsync(recipeId, 1, 2);

        Assert.Equal(1, diff.LeftRevision);
        Assert.Equal(2, diff.RightRevision);
        Assert.Equal(1, diff.AddedCount);
        Assert.Equal(1, diff.RemovedCount);
        Assert.Equal(1, diff.ChangedCount);
        Assert.Equal(1, diff.UnchangedCount);
        Assert.True(diff.NameChanged);
        Assert.Equal("工况A", diff.LeftName);
        Assert.Equal("工况B", diff.RightName);
        Assert.True(diff.DescriptionChanged);
        Assert.True(diff.ChangeNoteChanged);
        Assert.Equal("初始版本", diff.LeftChangeNote);
        Assert.Equal("调整参数", diff.RightChangeNote);

        var changed = Assert.Single(diff.Items, x => x.Kind == RecipeService.RevisionDiffKind.Changed);
        Assert.Equal("功率上限", changed.TagName);
        Assert.Equal("10", changed.LeftValue);
        Assert.Equal("25", changed.RightValue);
        Assert.Null(changed.Note);
        var addedItem = Assert.Single(diff.Items, x => x.Kind == RecipeService.RevisionDiffKind.Added);
        Assert.Equal("新增参数", addedItem.TagName);
        Assert.Null(addedItem.LeftValue);
        Assert.Equal("3", addedItem.RightValue);
        var removedItem = Assert.Single(diff.Items, x => x.Kind == RecipeService.RevisionDiffKind.Removed);
        Assert.Equal("旧参数", removedItem.TagName);
        Assert.Equal("5", removedItem.LeftValue);
        Assert.Null(removedItem.RightValue);
        Assert.DoesNotContain(diff.Items, x => x.TagName == "保持参数");

        // 反向对比：新旧方向互换
        var reversed = await service.CompareRevisionsAsync(recipeId, 2, 1);
        var reversedChanged = Assert.Single(reversed.Items, x => x.Kind == RecipeService.RevisionDiffKind.Changed);
        Assert.Equal("25", reversedChanged.LeftValue);
        Assert.Equal("10", reversedChanged.RightValue);
        Assert.Contains(reversed.Items,
            x => x.Kind == RecipeService.RevisionDiffKind.Added && x.TagName == "旧参数");
        Assert.Contains(reversed.Items,
            x => x.Kind == RecipeService.RevisionDiffKind.Removed && x.TagName == "新增参数");
    }

    [Fact]
    public async Task RevisionComparisonGuardsInvalidRequestsAndFlagsSignatureChanges()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = NewTag(system, "被改点", 100, TagDataType.Float32);
        await system.Tags.SaveAsync(tag);
        var service = new RecipeService(system.Store);
        var recipeId = await service.SaveAsync(
            null, null, system.Device.Id, "工况", "", [new(tag.Id, 12.5)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompareRevisionsAsync(recipeId, 1, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompareRevisionsAsync(recipeId, 0, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompareRevisionsAsync(recipeId, 1, 99));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompareRevisionsAsync(Guid.NewGuid(), 1, 2));

        // Tag 重新指向后保存 v2：值不变但签名变化，同样应标记为“修改”并备注
        var saved = (await system.Tags.GetAllAsync()).Single(x => x.Id == tag.Id);
        saved.Address = 102;
        await system.Tags.SaveAsync(saved);
        await service.SaveAsync(recipeId, 1, system.Device.Id, "工况", "", [new(tag.Id, 12.5)]);

        var diff = await service.CompareRevisionsAsync(recipeId, 1, 2);
        Assert.Equal(0, diff.AddedCount);
        Assert.Equal(0, diff.RemovedCount);
        Assert.Equal(1, diff.ChangedCount);
        Assert.Equal(0, diff.UnchangedCount);
        Assert.False(diff.NameChanged);
        Assert.False(diff.DescriptionChanged);
        Assert.False(diff.ChangeNoteChanged);
        var item = Assert.Single(diff.Items);
        Assert.Equal(RecipeService.RevisionDiffKind.Changed, item.Kind);
        Assert.Equal("12.5", item.LeftValue);
        Assert.Equal("12.5", item.RightValue);
        Assert.Contains("配置", item.Note);
    }

    private static Tag NewTag(
        TestSystem system, string name, int address, TagDataType dataType = TagDataType.UInt16) => new()
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
}
