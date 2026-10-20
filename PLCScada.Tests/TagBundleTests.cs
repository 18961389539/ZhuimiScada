using System.Text.Json.Nodes;
using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class TagBundleTests
{
    [Fact]
    public async Task BundleRoundTripsAllTagFieldsAcrossSystems()
    {
        await using var source = await TestSystem.CreateAsync();
        await source.Devices.SaveAsync(source.Device);
        var numeric = new Tag
        {
            Name = "功率上限设定",
            Description = "迁移用",
            DeviceId = source.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.Float32,
            AccessMode = AccessMode.ReadWrite,
            ScanInterval = 500,
            Scale = 0.1,
            Offset = 2,
            Unit = "kW",
            MinValue = 0,
            MaxValue = 100,
            ByteOrder = ByteOrder.LittleEndian,
            WordOrder = WordOrder.LowWordFirst,
            AlarmHigh = 90,
            AlarmLow = 10,
            AlarmDeadband = 2,
            QualityAlarmAfterFailures = 5
        };
        await source.Tags.SaveAsync(numeric);
        var toggle = new Tag
        {
            Name = "扫码启用",
            DeviceId = source.Device.Id,
            AddressType = AddressType.Coil,
            Address = 20,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite,
            Enabled = false
        };
        await source.Tags.SaveAsync(toggle);
        var sourceService = new TagService(source.Store);
        var json = sourceService.ExportBundleJson(
            await sourceService.GetAllAsync(),
            new Dictionary<Guid, string> { [source.Device.Id] = source.Device.Name });

        await using var target = await TestSystem.CreateAsync();
        await target.Devices.SaveAsync(target.Device);
        var targetService = new TagService(target.Store);
        var result = await targetService.ImportBundleJsonAsync(json);

        Assert.Equal(2, result.Imported);
        Assert.Equal(0, result.Failed);
        var imported = await targetService.GetAllAsync();
        var importedNumeric = imported.Single(x => x.Name == "功率上限设定");
        Assert.Equal(target.Device.Id, importedNumeric.DeviceId);
        Assert.Equal(AddressType.HoldingRegister, importedNumeric.AddressType);
        Assert.Equal(100, importedNumeric.Address);
        Assert.Equal(TagDataType.Float32, importedNumeric.DataType);
        Assert.Equal(AccessMode.ReadWrite, importedNumeric.AccessMode);
        Assert.Equal(500, importedNumeric.ScanInterval);
        Assert.Equal(0.1, importedNumeric.Scale);
        Assert.Equal(2, importedNumeric.Offset);
        Assert.Equal("kW", importedNumeric.Unit);
        Assert.Equal(0, importedNumeric.MinValue);
        Assert.Equal(100, importedNumeric.MaxValue);
        Assert.Equal(ByteOrder.LittleEndian, importedNumeric.ByteOrder);
        Assert.Equal(WordOrder.LowWordFirst, importedNumeric.WordOrder);
        Assert.Equal(90, importedNumeric.AlarmHigh);
        Assert.Equal(10, importedNumeric.AlarmLow);
        Assert.Equal(2, importedNumeric.AlarmDeadband);
        Assert.Equal(5, importedNumeric.QualityAlarmAfterFailures);
        Assert.Equal("迁移用", importedNumeric.Description);
        var importedToggle = imported.Single(x => x.Name == "扫码启用");
        Assert.Equal(AddressType.Coil, importedToggle.AddressType);
        Assert.Equal(TagDataType.Bool, importedToggle.DataType);
        Assert.False(importedToggle.Enabled);
    }

    [Fact]
    public async Task BundleImportReportsPerItemFailuresWithoutPartialWrites()
    {
        await using var source = await TestSystem.CreateAsync();
        await source.Devices.SaveAsync(source.Device);
        var sourceService = new TagService(source.Store);
        await sourceService.SaveAsync(NewTag(source, "正常导入项", 10));
        await sourceService.SaveAsync(NewTag(source, "已存在项", 12));
        await sourceService.SaveAsync(NewTag(source, "缺设备项", 14));
        var json = sourceService.ExportBundleJson(
            await sourceService.GetAllAsync(),
            new Dictionary<Guid, string> { [source.Device.Id] = source.Device.Name });

        await using var target = await TestSystem.CreateAsync();
        await target.Devices.SaveAsync(target.Device);
        var targetService = new TagService(target.Store);
        await targetService.SaveAsync(NewTag(target, "已存在项", 30));

        var root = JsonNode.Parse(json)!;
        foreach (var item in root["tags"]!.AsArray())
        {
            if (item!["name"]!.GetValue<string>() == "缺设备项")
                item["deviceName"] = "不存在设备";
        }
        var result = await targetService.ImportBundleJsonAsync(root.ToJsonString());

        Assert.Equal(1, result.Imported);
        Assert.Equal(2, result.Failed);
        Assert.Contains(result.Items, x => x.Name == "正常导入项" && x.Status == "已导入");
        Assert.Contains(result.Items, x =>
            x.Name == "已存在项" && x.Status == "未导入" && x.Message.Contains("名称已存在"));
        Assert.Contains(result.Items, x =>
            x.Name == "缺设备项" && x.Status == "未导入" && x.Message.Contains("找不到唯一匹配设备"));
        var targetTags = await targetService.GetAllAsync();
        Assert.Equal(2, targetTags.Count);
        Assert.Contains(targetTags, x => x.Name == "正常导入项");
        Assert.Contains(targetTags, x => x.Name == "已存在项" && x.Address == 30);
    }

    [Fact]
    public async Task BundleFormatSchemaAndContentAreValidated()
    {
        await using var system = await TestSystem.CreateAsync();
        var service = new TagService(system.Store);

        var malformed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportBundleJsonAsync("not json"));
        Assert.Contains("格式无效", malformed.Message);

        var wrongShape = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportBundleJsonAsync("[1,2,3]"));
        Assert.Contains("格式无效", wrongShape.Message);

        var wrongFormat = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportBundleJsonAsync(
                """{"format":"PLCScada.Other","schemaVersion":1,"exportedAt":"2026-01-01T00:00:00+00:00","tags":[]}"""));
        Assert.Contains("不支持", wrongFormat.Message);

        var wrongVersion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportBundleJsonAsync(
                """{"format":"PLCScada.TagBundle","schemaVersion":2,"exportedAt":"2026-01-01T00:00:00+00:00","tags":[]}"""));
        Assert.Contains("不支持", wrongVersion.Message);

        var emptyTags = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportBundleJsonAsync(
                """{"format":"PLCScada.TagBundle","schemaVersion":1,"exportedAt":"2026-01-01T00:00:00+00:00","tags":[]}"""));
        Assert.Contains("1 到 5000", emptyTags.Message);

        var oversized = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportBundleJsonAsync(new string('x', 5_000_001)));
        Assert.Contains("5 MB", oversized.Message);
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
