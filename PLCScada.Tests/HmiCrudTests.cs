using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class HmiCrudTests
{
    [Fact]
    public async Task FirstLoadSeedsExistingNineteenHmiTagsWithTheirDisplayTypes()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);

        var defaults = new[]
        {
            ("运行状态", HmiDisplayType.Status, TagDataType.UInt16),
            ("总判定", HmiDisplayType.TotalResult, TagDataType.UInt16),
            ("转速方向判定", HmiDisplayType.Result, TagDataType.UInt16),
            ("正常电流判定", HmiDisplayType.Result, TagDataType.UInt16),
            ("正常功率判定", HmiDisplayType.Result, TagDataType.UInt16),
            ("正常转速判定", HmiDisplayType.Result, TagDataType.UInt16),
            ("灰尘感应电流判定", HmiDisplayType.Result, TagDataType.UInt16),
            ("灰尘感应功率判定", HmiDisplayType.Result, TagDataType.UInt16),
            ("灰尘感应转速判定", HmiDisplayType.Result, TagDataType.UInt16),
            ("相机灯亮判定", HmiDisplayType.Result, TagDataType.UInt16),
            ("相机灯灭判定", HmiDisplayType.Result, TagDataType.UInt16),
            ("正常电压", HmiDisplayType.Measurement, TagDataType.Float32),
            ("正常电流", HmiDisplayType.Measurement, TagDataType.Float32),
            ("正常功率", HmiDisplayType.Measurement, TagDataType.Float32),
            ("灰尘感应电压", HmiDisplayType.Measurement, TagDataType.Float32),
            ("灰尘感应电流", HmiDisplayType.Measurement, TagDataType.Float32),
            ("灰尘感应功率", HmiDisplayType.Measurement, TagDataType.Float32),
            ("正常功率差值", HmiDisplayType.Measurement, TagDataType.Float32),
            ("正常转速差值", HmiDisplayType.Measurement, TagDataType.Float32)
        };
        for (var index = 0; index < defaults.Length; index++)
        {
            var (name, _, dataType) = defaults[index];
            await system.Tags.SaveAsync(new Tag
            {
                Name = name,
                DeviceId = system.Device.Id,
                Address = 100 + index * 2,
                DataType = dataType,
                AccessMode = AccessMode.ReadOnly
            });
        }

        var service = new HmiService(system.Store);
        var items = await service.GetDisplayItemsAsync();

        Assert.Equal(19, items.Count);
        Assert.Equal(1, items.Count(item => item.Type == HmiDisplayType.Status));
        Assert.Equal(1, items.Count(item => item.Type == HmiDisplayType.TotalResult));
        Assert.Equal(9, items.Count(item => item.Type == HmiDisplayType.Result));
        Assert.Equal(8, items.Count(item => item.Type == HmiDisplayType.Measurement));
        Assert.Equal("正常工况", items.Single(item => item.Tag.Name == "正常电压").Group);
        Assert.Equal("灰尘感应工况", items.Single(item => item.Tag.Name == "灰尘感应电压").Group);
        Assert.Equal("差值", items.Single(item => item.Tag.Name == "正常功率差值").Group);
    }

    [Fact]
    public async Task DisplayItemCanBeCreatedEditedAndDeletedWithoutDeletingTag()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var firstTag = await SaveTagAsync(system, "自定义测量值", 100);
        var secondTag = await SaveTagAsync(system, "备用测量值", 102);
        var service = new HmiService(system.Store);

        var controlId = await service.SaveControlAsync(
            null, firstTag.Id, "工位 A 测量", HmiDisplayType.Measurement, "工位 A");
        var created = Assert.Single(await service.GetDisplayItemsAsync());
        Assert.Equal(controlId, created.Control.Id);
        Assert.Equal("工位 A", created.Group);

        await service.SaveControlAsync(
            controlId, secondTag.Id, "工位 B 测量", HmiDisplayType.Measurement, "工位 B");
        var edited = Assert.Single(await service.GetDisplayItemsAsync());
        Assert.Equal("工位 B 测量", edited.Control.Name);
        Assert.Equal(secondTag.Id, edited.Tag.Id);
        Assert.Equal("工位 B", edited.Group);

        await service.DeleteControlAsync(controlId);
        Assert.Empty(await service.GetDisplayItemsAsync());
        Assert.Contains(await system.Store.GetTagsAsync(), tag => tag.Id == firstTag.Id);
        Assert.Contains(await system.Store.GetTagsAsync(), tag => tag.Id == secondTag.Id);
    }

    [Fact]
    public async Task DuplicateTagNameAndSingletonDisplayTypesAreRejected()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var firstTag = await SaveTagAsync(system, "只读寄存器一", 100);
        var secondTag = await SaveTagAsync(system, "只读寄存器二", 102);
        var thirdTag = await SaveTagAsync(system, "只读寄存器三", 104);
        var service = new HmiService(system.Store);

        await service.SaveControlAsync(null, firstTag.Id, "主状态", HmiDisplayType.Status, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveControlAsync(null, secondTag.Id, "其他状态", HmiDisplayType.Status, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveControlAsync(null, firstTag.Id, "另一名称", HmiDisplayType.Measurement, "分组"));
        await service.SaveControlAsync(null, secondTag.Id, "普通项", HmiDisplayType.Result, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveControlAsync(null, thirdTag.Id, "主状态", HmiDisplayType.Result, null));
    }

    [Fact]
    public async Task DisabledAndFloatingPointTagsCannotBeUsedForStatusDisplays()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var floatTag = await SaveTagAsync(system, "浮点值", 100, TagDataType.Float32);
        var disabledTag = await SaveTagAsync(system, "禁用寄存器", 102);
        disabledTag.Enabled = false;
        await system.Tags.SaveAsync(disabledTag);
        var service = new HmiService(system.Store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveControlAsync(null, floatTag.Id, "浮点状态", HmiDisplayType.Status, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveControlAsync(null, disabledTag.Id, "禁用项", HmiDisplayType.Measurement, "分组"));
    }

    private static async Task<Tag> SaveTagAsync(
        TestSystem system, string name, int address, TagDataType dataType = TagDataType.UInt16)
    {
        var tag = new Tag
        {
            Name = name,
            DeviceId = system.Device.Id,
            Address = address,
            DataType = dataType,
            AccessMode = AccessMode.ReadOnly
        };
        await system.Tags.SaveAsync(tag);
        return tag;
    }
}
