using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class RecipeParameterOrderTests
{
    [Fact]
    public async Task ParameterOrderIsPersistedRestoredAndShownInHistory()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tagA = await SaveTagAsync(system, "参数A", 100);
        var tagB = await SaveTagAsync(system, "参数B", 102);
        var tagC = await SaveTagAsync(system, "参数C", 104);
        var service = new RecipeService(system.Store);

        var recipeId = await service.SaveAsync(
            null, null, system.Device.Id, "排序工况", "",
            [new(tagC.Id, 30), new(tagA.Id, 10), new(tagB.Id, 20)]);
        var created = Assert.Single(await service.GetAllAsync());
        Assert.Equal(new[] { "参数C", "参数A", "参数B" },
            created.Parameters.Select(x => x.Tag!.Name));

        // 编辑重排 B、C、A
        await service.SaveAsync(
            recipeId, created.Revision, system.Device.Id, "排序工况", "",
            [new(tagB.Id, 20), new(tagC.Id, 30), new(tagA.Id, 10)]);
        var edited = Assert.Single(await service.GetAllAsync());
        Assert.Equal(new[] { "参数B", "参数C", "参数A" },
            edited.Parameters.Select(x => x.Tag!.Name));

        // 历史快照保留各自版本的顺序
        var history = await service.GetHistoryAsync(recipeId);
        Assert.Equal(new[] { "参数B", "参数C", "参数A" },
            history.Items[0].Parameters.Select(x => x.TagName));
        Assert.Equal(new[] { "参数C", "参数A", "参数B" },
            history.Items[1].Parameters.Select(x => x.TagName));

        // 恢复 v1 保留原始顺序（并生成新修订）
        await service.RestoreRevisionAsync(recipeId, sourceRevision: 1, expectedCurrentRevision: 2);
        var restored = Assert.Single(await service.GetAllAsync());
        Assert.Equal(new[] { "参数C", "参数A", "参数B" },
            restored.Parameters.Select(x => x.Tag!.Name));

        // 下发预览按当前顺序展示（设备离线时返回阻断信息，但条目顺序仍应保持）
        var applier = new RecipeApplyService(system.Store, system.Communication, system.RecipeApplyLock);
        var preview = await applier.PreviewAsync(recipeId);
        Assert.False(preview.CanApply);
        Assert.Equal(new[] { "参数C", "参数A", "参数B" },
            preview.Items.Select(x => x.TagName));
    }

    [Fact]
    public async Task DuplicateAndImportPreserveParameterOrder()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tagA = await SaveTagAsync(system, "参数A", 100);
        var tagB = await SaveTagAsync(system, "参数B", 102);
        var service = new RecipeService(system.Store);
        var recipeId = await service.SaveAsync(
            null, null, system.Device.Id, "基准工况", "",
            [new(tagB.Id, 20), new(tagA.Id, 10)]);
        var source = (await service.GetAllAsync()).Single();

        var duplicateId = await service.DuplicateAsync(recipeId);
        var copy = (await service.GetAllAsync()).Single(x => x.Id == duplicateId);
        Assert.Equal(new[] { "参数B", "参数A" }, copy.Parameters.Select(x => x.Tag!.Name));

        var json = service.ExportJson(source, system.Device.Name);
        var package = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        package["name"] = "导入工况";
        var importedId = await service.ImportJsonAsync(package.ToJsonString());
        var imported = (await service.GetAllAsync()).Single(x => x.Id == importedId);
        Assert.Equal(new[] { "参数B", "参数A" }, imported.Parameters.Select(x => x.Tag!.Name));
    }

    private static async Task<Tag> SaveTagAsync(TestSystem system, string name, int address)
    {
        var tag = new Tag
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
        await system.Tags.SaveAsync(tag);
        return tag;
    }
}
