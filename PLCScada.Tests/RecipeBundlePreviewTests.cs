using System.Text.Json.Nodes;
using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class RecipeBundlePreviewTests
{
    [Fact]
    public async Task PreviewSimulatesRenamesWithoutWritingAndMatchesActualImport()
    {
        await using var source = await TestSystem.CreateAsync();
        await source.Devices.SaveAsync(source.Device);
        var tag = NewTag(source, "预览参数", 100);
        await source.Tags.SaveAsync(tag);
        var sourceService = new RecipeService(source.Store);
        await sourceService.SaveAsync(null, null, source.Device.Id, "P1", "", [new(tag.Id, 10)]);
        await sourceService.SaveAsync(null, null, source.Device.Id, "P2", "", [new(tag.Id, 20)]);
        var bundle = sourceService.ExportBundleJson(
            await sourceService.GetAllAsync(),
            new Dictionary<Guid, string> { [source.Device.Id] = source.Device.Name });

        await using var target = await TestSystem.CreateAsync();
        await target.Devices.SaveAsync(target.Device);
        var targetTag = NewTag(target, "预览参数", 100);
        await target.Tags.SaveAsync(targetTag);
        var targetService = new RecipeService(target.Store);
        await targetService.SaveAsync(null, null, target.Device.Id, "P1", "", [new(targetTag.Id, 1)]);

        var before = (await targetService.GetAllAsync()).Count;
        var preview = await targetService.PreviewBundleImportAsync(bundle.Json);

        Assert.Equal(1, preview.ImportableCount);
        Assert.Equal(1, preview.RenamedCount);
        Assert.Equal(0, preview.FailedCount);
        Assert.Contains(preview.Items, x =>
            x.Name == "P1" && x.Action == "重命名导入" && x.Message.Contains("P1（导入）"));
        Assert.Contains(preview.Items, x => x.Name == "P2" && x.Action == "直接导入");
        Assert.Equal(before, (await targetService.GetAllAsync()).Count);

        var result = await targetService.ImportBundleJsonAsync(bundle.Json);
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.RenamedCount);
        Assert.Contains(await targetService.GetAllAsync(), x => x.Name == "P1（导入）");

        var second = await targetService.PreviewBundleImportAsync(bundle.Json);
        Assert.Equal(0, second.ImportableCount);
        Assert.Equal(2, second.RenamedCount);
        Assert.Contains(second.Items, x => x.Name == "P1" && x.Message.Contains("P1（导入 2）"));
    }

    [Fact]
    public async Task PreviewHandlesBundleDuplicatesAndFailures()
    {
        await using var source = await TestSystem.CreateAsync();
        await source.Devices.SaveAsync(source.Device);
        var tag = NewTag(source, "重复预览参数", 100);
        await source.Tags.SaveAsync(tag);
        var sourceService = new RecipeService(source.Store);
        await sourceService.SaveAsync(null, null, source.Device.Id, "X", "", [new(tag.Id, 5)]);
        var bundle = sourceService.ExportBundleJson(
            await sourceService.GetAllAsync(),
            new Dictionary<Guid, string> { [source.Device.Id] = source.Device.Name });

        var root = JsonNode.Parse(bundle.Json)!;
        var recipes = root["recipes"]!.AsArray();
        recipes[0]!["name"] = "DUP";
        var duplicate = recipes[0]!.DeepClone();
        recipes.Add(duplicate);
        var broken = recipes[0]!.DeepClone();
        broken["name"] = "缺设备";
        broken["deviceName"] = "不存在设备";
        recipes.Add(broken);

        await using var target = await TestSystem.CreateAsync();
        await target.Devices.SaveAsync(target.Device);
        await target.Tags.SaveAsync(NewTag(target, "重复预览参数", 100));
        var targetService = new RecipeService(target.Store);

        var preview = await targetService.PreviewBundleImportAsync(root.ToJsonString());

        Assert.Equal(1, preview.ImportableCount);
        Assert.Equal(1, preview.RenamedCount);
        Assert.Equal(1, preview.FailedCount);
        Assert.Contains(preview.Items, x => x.Name == "DUP" && x.Action == "直接导入");
        Assert.Contains(preview.Items, x =>
            x.Name == "DUP" && x.Action == "重命名导入" && x.Message.Contains("DUP（导入）"));
        Assert.Contains(preview.Items, x =>
            x.Name == "缺设备" && x.Action == "未导入" && x.Message.Contains("找不到唯一匹配设备"));
        Assert.Empty(await targetService.GetAllAsync());
    }

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
}
