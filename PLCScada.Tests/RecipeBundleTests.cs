using System.Text.Json.Nodes;
using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class RecipeBundleTests
{
    [Fact]
    public async Task BundleRoundTripsAcrossSystemsAndRenamesOnConflict()
    {
        await using var source = await TestSystem.CreateAsync();
        await source.Devices.SaveAsync(source.Device);
        var numeric = NewTag(source, "功率上限", 100, TagDataType.UInt16);
        var toggle = new Tag
        {
            Name = "扫码启用",
            DeviceId = source.Device.Id,
            AddressType = AddressType.Coil,
            Address = 20,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await source.Tags.SaveAsync(numeric);
        await source.Tags.SaveAsync(toggle);
        var sourceService = new RecipeService(source.Store);
        await sourceService.SaveAsync(
            null, null, source.Device.Id, "N1", "第一套",
            [new(numeric.Id, 25), new RecipeService.ParameterInput(toggle.Id, BooleanValue: true)]);
        await sourceService.SaveAsync(
            null, null, source.Device.Id, "N2", "第二套", [new(numeric.Id, 40)]);
        var deviceNames = new Dictionary<Guid, string> { [source.Device.Id] = source.Device.Name };

        var export = sourceService.ExportBundleJson(await sourceService.GetAllAsync(), deviceNames);
        Assert.Equal(2, export.ExportedCount);
        Assert.Equal(0, export.SkippedCount);

        await using var target = await TestSystem.CreateAsync();
        await target.Devices.SaveAsync(target.Device);
        // 配方导入按 Tag 签名匹配目标系统已有 Tag（迁移流程：先导入 Tags 包，再导入配方包）
        await target.Tags.SaveAsync(new Tag
        {
            Name = "功率上限",
            DeviceId = target.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.UInt16,
            AccessMode = AccessMode.ReadWrite,
            MinValue = 0,
            MaxValue = 1000
        });
        await target.Tags.SaveAsync(new Tag
        {
            Name = "扫码启用",
            DeviceId = target.Device.Id,
            AddressType = AddressType.Coil,
            Address = 20,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        });
        var targetService = new RecipeService(target.Store);

        var first = await targetService.ImportBundleJsonAsync(export.Json);
        Assert.True(first.ImportedCount == 2,
            string.Join(" | ", first.Items.Select(x => $"{x.Name}:{x.Status}:{x.Message}")));
        Assert.Equal(0, first.RenamedCount);
        Assert.Equal(0, first.FailedCount);
        var importedN1 = (await targetService.GetAllAsync()).Single(x => x.Name == "N1");
        Assert.Equal("第一套", importedN1.Description);
        Assert.Equal(2, importedN1.Parameters.Count);
        Assert.Equal("由配方包导入", importedN1.ChangeNote);

        // 再次导入同名包：自动重命名为「（导入）」后缀
        var second = await targetService.ImportBundleJsonAsync(export.Json);
        Assert.Equal(0, second.ImportedCount);
        Assert.Equal(2, second.RenamedCount);
        Assert.Equal(0, second.FailedCount);
        Assert.Contains(second.Items, x => x.Status == "已重命名导入" && x.Message.Contains("N1（导入）"));
        var all = await targetService.GetAllAsync();
        Assert.Equal(4, all.Count);
        Assert.Contains(all, x => x.Name == "N1（导入）");
        Assert.Contains(all, x => x.Name == "N2（导入）");

        // 第三次导入：后缀递增
        var third = await targetService.ImportBundleJsonAsync(export.Json);
        Assert.Equal(2, third.RenamedCount);
        Assert.Contains(await targetService.GetAllAsync(), x => x.Name == "N1（导入 2）");
    }

    [Fact]
    public async Task BundleImportReportsPartialFailuresWithoutStopping()
    {
        await using var source = await TestSystem.CreateAsync();
        await source.Devices.SaveAsync(source.Device);
        var tag = NewTag(source, "参数点", 100, TagDataType.UInt16);
        await source.Tags.SaveAsync(tag);
        var sourceService = new RecipeService(source.Store);
        await sourceService.SaveAsync(null, null, source.Device.Id, "正常配方", "", [new(tag.Id, 10)]);
        await sourceService.SaveAsync(null, null, source.Device.Id, "缺设备配方", "", [new(tag.Id, 20)]);
        await sourceService.SaveAsync(null, null, source.Device.Id, "地址不匹配配方", "", [new(tag.Id, 30)]);
        var bundle = sourceService.ExportBundleJson(
            await sourceService.GetAllAsync(),
            new Dictionary<Guid, string> { [source.Device.Id] = source.Device.Name });

        var root = JsonNode.Parse(bundle.Json)!;
        foreach (var item in root["recipes"]!.AsArray())
        {
            var name = item!["name"]!.GetValue<string>();
            if (name == "缺设备配方")
                item["deviceName"] = "不存在设备";
            if (name == "地址不匹配配方")
                item["parameters"]!.AsArray()[0]!["address"] = 999;
        }

        await using var target = await TestSystem.CreateAsync();
        await target.Devices.SaveAsync(target.Device);
        await target.Tags.SaveAsync(new Tag
        {
            Name = "参数点",
            DeviceId = target.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.UInt16,
            AccessMode = AccessMode.ReadWrite,
            MinValue = 0,
            MaxValue = 1000
        });
        var targetService = new RecipeService(target.Store);
        var result = await targetService.ImportBundleJsonAsync(root.ToJsonString());

        Assert.True(result.ImportedCount == 1,
            string.Join(" | ", result.Items.Select(x => $"{x.Name}:{x.Status}:{x.Message}")));
        Assert.Equal(0, result.RenamedCount);
        Assert.Equal(2, result.FailedCount);
        Assert.Contains(result.Items, x => x.Name == "正常配方" && x.Status == "已导入");
        Assert.Contains(result.Items, x => x.Name == "缺设备配方" &&
            x.Status == "未导入" && x.Message.Contains("找不到唯一匹配设备"));
        Assert.Contains(result.Items, x => x.Name == "地址不匹配配方" &&
            x.Status == "未导入" && x.Message.Contains("找不到与文件匹配的可写 Tag"));
        Assert.Single(await targetService.GetAllAsync());
    }

    [Fact]
    public async Task BundleFormatSchemaAndContentAreValidated()
    {
        await using var system = await TestSystem.CreateAsync();
        var service = new RecipeService(system.Store);

        var malformed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportBundleJsonAsync("not json"));
        Assert.Contains("格式无效", malformed.Message);

        var wrongShape = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportBundleJsonAsync("[1,2,3]"));
        Assert.Contains("格式无效", wrongShape.Message);

        var wrongFormat = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportBundleJsonAsync(
                """{"format":"PLCScada.Other","schemaVersion":1,"exportedAt":"2026-01-01T00:00:00+00:00","recipes":[]}"""));
        Assert.Contains("不支持", wrongFormat.Message);

        var wrongVersion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportBundleJsonAsync(
                """{"format":"PLCScada.RecipeBundle","schemaVersion":2,"exportedAt":"2026-01-01T00:00:00+00:00","recipes":[]}"""));
        Assert.Contains("不支持", wrongVersion.Message);

        var empty = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportBundleJsonAsync(
                """{"format":"PLCScada.RecipeBundle","schemaVersion":1,"exportedAt":"2026-01-01T00:00:00+00:00","recipes":[]}"""));
        Assert.Contains("1 到 1000", empty.Message);

        var oversized = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportBundleJsonAsync(new string('x', 5_000_001)));
        Assert.Contains("5 MB", oversized.Message);
    }

    [Fact]
    public void RecipeListFilterCombinesDeviceAndSearch()
    {
        var deviceId = Guid.NewGuid();
        var recipe = new Recipe
        {
            Name = "工况A",
            Description = "高功率测试",
            DeviceId = deviceId,
            Parameters = [new RecipeParameter { Tag = new Tag { Name = "功率上限" } }]
        };

        Assert.True(RecipeListFilter.Matches(recipe, null, "", "设备一"));
        Assert.True(RecipeListFilter.Matches(recipe, deviceId, null, "设备一"));
        Assert.False(RecipeListFilter.Matches(recipe, Guid.NewGuid(), null, "设备一"));
        Assert.True(RecipeListFilter.Matches(recipe, null, "工况", "设备一"));
        Assert.True(RecipeListFilter.Matches(recipe, null, "高功率", "设备一"));
        Assert.True(RecipeListFilter.Matches(recipe, null, "设备一", "设备一"));
        Assert.True(RecipeListFilter.Matches(recipe, null, "功率上限", "设备一"));
        Assert.False(RecipeListFilter.Matches(recipe, null, "不存在", "设备一"));
        Assert.True(RecipeListFilter.Matches(recipe, deviceId, "工况", "设备一"));
        Assert.False(RecipeListFilter.Matches(recipe, Guid.NewGuid(), "工况", "设备一"));
    }

    private static Tag NewTag(TestSystem system, string name, int address, TagDataType dataType) => new()
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
