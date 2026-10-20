using PLCScada.Application;
using PLCScada.Core;
using System.Text.Json.Nodes;

namespace PLCScada.Tests;

public sealed class RecipeTests
{
    [Fact]
    public async Task RecipeCanBeCreatedEditedAndDeletedWithItsParameters()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var currentLimit = await SaveWritableTagAsync(system, "电流上限设定", 100, 20);
        var testTime = await SaveWritableTagAsync(system, "测试时间设定", 102, 60, TagDataType.Int16);
        var service = new RecipeService(system.Store);

        var recipeId = await service.SaveAsync(null, null, system.Device.Id, "标准工况", "默认测试参数",
        [
            new(currentLimit.Id, 12.5),
            new(testTime.Id, 45)
        ]);

        var created = Assert.Single(await service.GetAllAsync());
        Assert.Equal(recipeId, created.Id);
        Assert.Equal("标准工况", created.Name);
        Assert.Equal("默认测试参数", created.Description);
        Assert.Equal(1, created.Revision);
        Assert.Equal(2, created.Parameters.Count);
        Assert.Equal(12.5, created.Parameters.Single(x => x.TagId == currentLimit.Id).EngineeringValue);
        var createdAt = created.CreatedAt;

        await service.SaveAsync(recipeId, created.Revision, system.Device.Id, "轻载工况", "更新后的说明",
        [
            new(testTime.Id, 30)
        ]);

        var edited = Assert.Single(await service.GetAllAsync());
        Assert.Equal("轻载工况", edited.Name);
        Assert.Equal("更新后的说明", edited.Description);
        Assert.Equal(2, edited.Revision);
        Assert.Equal(createdAt, edited.CreatedAt);
        Assert.Single(edited.Parameters);
        Assert.Equal(testTime.Id, edited.Parameters[0].TagId);
        Assert.Equal(30, edited.Parameters[0].EngineeringValue);

        await service.DeleteAsync(recipeId);
        Assert.Empty(await service.GetAllAsync());
    }

    [Fact]
    public async Task RecipeHistoryPreservesChangesAndRestoresAsANewRevision()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var numeric = await SaveWritableTagAsync(system, "功率上限", 100, 100);
        var toggle = new Tag
        {
            Name = "相机启用",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 20,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(toggle);
        var service = new RecipeService(system.Store);
        var recipeId = await service.SaveAsync(
            null, null, system.Device.Id, "标准工况", "初始版本",
            [new(numeric.Id, 12.5), new RecipeService.ParameterInput(toggle.Id, BooleanValue: true)]);

        var first = Assert.Single(await service.GetAllAsync());
        await service.SaveAsync(
            recipeId, first.Revision, system.Device.Id, "高功率工况", "第二版本",
            [new(numeric.Id, 25), new RecipeService.ParameterInput(toggle.Id, BooleanValue: false)]);

        var history = await service.GetHistoryAsync(recipeId);
        Assert.Equal(2, history.CurrentRevision);
        Assert.Equal(2, history.TotalCount);
        Assert.Equal(new[] { 2, 1 }, history.Items.Select(x => x.Revision));
        Assert.Contains(history.Items[0].Parameters, x => x.TagId == numeric.Id && x.EngineeringValue == 25);
        Assert.Contains(history.Items[0].Parameters, x => x.TagId == toggle.Id && x.BooleanValue == false);
        Assert.Contains(history.Items[1].Parameters, x => x.TagId == numeric.Id && x.EngineeringValue == 12.5);
        Assert.Contains(system.Device.IpAddress, history.Items[0].DeviceEndpoint);

        await service.RestoreRevisionAsync(recipeId, sourceRevision: 1, expectedCurrentRevision: 2);

        var restored = Assert.Single(await service.GetAllAsync());
        Assert.Equal(3, restored.Revision);
        Assert.Equal("标准工况", restored.Name);
        Assert.Equal("初始版本", restored.Description);
        Assert.Equal(12.5, restored.Parameters.Single(x => x.TagId == numeric.Id).EngineeringValue);
        Assert.True(restored.Parameters.Single(x => x.TagId == toggle.Id).BooleanValue);
        var restoredHistory = await service.GetHistoryAsync(recipeId);
        Assert.Equal(3, restoredHistory.CurrentRevision);
        Assert.Equal(new[] { 3, 2, 1 }, restoredHistory.Items.Select(x => x.Revision));
    }

    [Fact]
    public async Task RecipeChangeNoteIsArchivedPerRevisionAndDerivedForRestoreAndDuplicate()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveWritableTagAsync(system, "功率上限", 100, 100);
        var service = new RecipeService(system.Store);

        var recipeId = await service.SaveAsync(
            null, null, system.Device.Id, "标准工况", "", [new(tag.Id, 12.5)],
            changeNote: "初始版本");
        var created = Assert.Single(await service.GetAllAsync());
        Assert.Equal("初始版本", created.ChangeNote);

        await service.SaveAsync(
            recipeId, created.Revision, system.Device.Id, "标准工况", "", [new(tag.Id, 25)],
            changeNote: "  提高功率上限至 25  ");
        var edited = Assert.Single(await service.GetAllAsync());
        Assert.Equal("提高功率上限至 25", edited.ChangeNote);

        var history = await service.GetHistoryAsync(recipeId);
        Assert.Equal("提高功率上限至 25", history.Items[0].ChangeNote);
        Assert.Equal("初始版本", history.Items[1].ChangeNote);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(
                recipeId, edited.Revision, system.Device.Id, "标准工况", "", [new(tag.Id, 30)],
                changeNote: new string('长', 201)));
        Assert.Equal(2, Assert.Single(await service.GetAllAsync()).Revision);

        await service.RestoreRevisionAsync(recipeId, sourceRevision: 1, expectedCurrentRevision: 2);
        var restored = Assert.Single(await service.GetAllAsync());
        Assert.Equal(3, restored.Revision);
        Assert.Equal("恢复自 v1", restored.ChangeNote);

        var duplicateId = await service.DuplicateAsync(recipeId);
        var copy = (await service.GetAllAsync()).Single(x => x.Id == duplicateId);
        Assert.Equal("复制自“标准工况”", copy.ChangeNote);
    }

    [Fact]
    public async Task RecipeHistoryPagesAreBoundedAndClampStalePageRequests()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveWritableTagAsync(system, "历史分页设定", 100, 100);
        var service = new RecipeService(system.Store);
        var recipeId = await service.SaveAsync(null, null, system.Device.Id, "分页历史", "", [new(tag.Id, 1)]);
        var revision = 1;

        for (var value = 2; value <= 13; value++)
        {
            await service.SaveAsync(
                recipeId, revision, system.Device.Id, "分页历史", "",
                [new(tag.Id, value)]);
            revision++;
        }

        var newest = await service.GetHistoryAsync(recipeId, pageSize: 5);
        var middle = await service.GetHistoryAsync(recipeId, pageIndex: 1, pageSize: 5);
        var oldest = await service.GetHistoryAsync(recipeId, pageIndex: 2, pageSize: 5);
        var clamped = await service.GetHistoryAsync(recipeId, pageIndex: 99, pageSize: 5);

        Assert.Equal(13, newest.TotalCount);
        Assert.Equal(13, newest.CurrentRevision);
        Assert.Equal(3, newest.PageCount);
        Assert.Equal(new[] { 13, 12, 11, 10, 9 }, newest.Items.Select(x => x.Revision));
        Assert.Equal(new[] { 8, 7, 6, 5, 4 }, middle.Items.Select(x => x.Revision));
        Assert.Equal(new[] { 3, 2, 1 }, oldest.Items.Select(x => x.Revision));
        Assert.Equal(3, oldest.Items.Count);
        Assert.Equal(2, clamped.PageIndex);
        Assert.Equal(oldest.Items.Select(x => x.Revision), clamped.Items.Select(x => x.Revision));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.GetHistoryAsync(recipeId, pageIndex: -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.GetHistoryAsync(recipeId, pageSize: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.GetHistoryAsync(recipeId, pageSize: 51));
    }

    [Fact]
    public async Task RecipeHistoryPageUsesLatestRevisionInsteadOfTheCallerSnapshot()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveWritableTagAsync(system, "并发历史设定", 100, 100);
        var service = new RecipeService(system.Store);
        var recipeId = await service.SaveAsync(null, null, system.Device.Id, "并发历史", "", [new(tag.Id, 1)]);
        var staleCallerSnapshot = Assert.Single(await service.GetAllAsync());

        await service.SaveAsync(
            recipeId, staleCallerSnapshot.Revision, system.Device.Id, "并发历史", "另一窗口修改",
            [new(tag.Id, 2)]);

        var page = await service.GetHistoryAsync(recipeId);
        Assert.Equal(2, page.CurrentRevision);
        Assert.Equal(new[] { 2, 1 }, page.Items.Select(x => x.Revision));
    }

    [Fact]
    public async Task RecipeHistoryRestoreRejectsChangedTagConfiguration()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveWritableTagAsync(system, "功率上限", 100, 100);
        var service = new RecipeService(system.Store);
        var recipeId = await service.SaveAsync(null, null, system.Device.Id, "工况", "", [new(tag.Id, 12.5)]);
        var first = Assert.Single(await service.GetAllAsync());
        await service.SaveAsync(recipeId, first.Revision, system.Device.Id, "工况", "更新", [new(tag.Id, 25)]);

        tag.Address = 102;
        await system.Tags.SaveAsync(tag);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreRevisionAsync(recipeId, sourceRevision: 1, expectedCurrentRevision: 2));
        Assert.Contains("配置已变化", error.Message);
        var current = Assert.Single(await service.GetAllAsync());
        Assert.Equal(2, current.Revision);
        Assert.Equal(25, current.Parameters.Single().EngineeringValue);
    }

    [Fact]
    public async Task StaleRecipeRevisionCannotOverwriteANewerEdit()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveWritableTagAsync(system, "设定值", 100, 100);
        var service = new RecipeService(system.Store);
        var recipeId = await service.SaveAsync(null, null, system.Device.Id, "配方", "", [new(tag.Id, 10)]);
        var firstRead = Assert.Single(await service.GetAllAsync());

        await service.SaveAsync(recipeId, firstRead.Revision, system.Device.Id, "配方", "", [new(tag.Id, 20)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(recipeId, firstRead.Revision, system.Device.Id, "配方", "", [new(tag.Id, 30)]));

        var latest = Assert.Single(await service.GetAllAsync());
        Assert.Equal(2, latest.Revision);
        Assert.Equal(20, latest.Parameters[0].EngineeringValue);
    }

    [Fact]
    public async Task RecipeCanBeDuplicatedWithAnIndependentRevisionAndValues()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var numeric = await SaveWritableTagAsync(system, "时间设定", 100, 60, TagDataType.Int16);
        var toggle = new Tag
        {
            Name = "相机灯设定",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 200,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(toggle);
        var service = new RecipeService(system.Store);
        var sourceId = await service.SaveAsync(null, null, system.Device.Id, "标准工况", "基础配方",
        [
            new(numeric.Id, 35),
            new RecipeService.ParameterInput(toggle.Id, BooleanValue: true)
        ]);

        var duplicateId = await service.DuplicateAsync(sourceId);
        var recipes = (await service.GetAllAsync()).ToDictionary(x => x.Id);
        var source = recipes[sourceId];
        var copy = recipes[duplicateId];

        Assert.Equal("标准工况 副本", copy.Name);
        Assert.Equal(source.DeviceId, copy.DeviceId);
        Assert.Equal(1, copy.Revision);
        Assert.Equal(source.Parameters.Count, copy.Parameters.Count);
        Assert.Contains(copy.Parameters, x => x.TagId == numeric.Id && x.EngineeringValue == 35);
        Assert.Contains(copy.Parameters, x => x.TagId == toggle.Id && x.BooleanValue == true);

        await service.SaveAsync(
            duplicateId, copy.Revision, system.Device.Id, copy.Name, copy.Description,
            [new(numeric.Id, 50)]);
        Assert.Equal(35, (await service.GetAllAsync()).Single(x => x.Id == sourceId)
            .Parameters.Single(x => x.TagId == numeric.Id).EngineeringValue);
    }

    [Fact]
    public async Task RecipeJsonExportImportRoundTripsTypedValuesAndValidatesTagSignatures()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var numeric = await SaveWritableTagAsync(system, "功率上限设定", 100, 1000);
        var toggle = new Tag
        {
            Name = "扫码启用",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 200,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(toggle);
        var service = new RecipeService(system.Store);
        var sourceId = await service.SaveAsync(null, null, system.Device.Id, "源配方", "可迁移",
        [
            new(numeric.Id, 125.5),
            new RecipeService.ParameterInput(toggle.Id, BooleanValue: true)
        ]);
        var source = (await service.GetAllAsync()).Single();

        var json = service.ExportJson(source, system.Device.Name);
        var package = JsonNode.Parse(json)!;
        package["name"] = "导入配方";
        var importedId = await service.ImportJsonAsync(package.ToJsonString());
        var imported = (await service.GetAllAsync()).Single(x => x.Id == importedId);

        Assert.NotEqual(sourceId, imported.Id);
        Assert.Equal(system.Device.Id, imported.DeviceId);
        Assert.Equal(1, imported.Revision);
        Assert.Equal("可迁移", imported.Description);
        Assert.Equal("由 JSON 文件导入", imported.ChangeNote);
        Assert.Equal(125.5, imported.Parameters.Single(x => x.TagId == numeric.Id).EngineeringValue);
        Assert.True(imported.Parameters.Single(x => x.TagId == toggle.Id).BooleanValue);

        package["name"] = "地址不匹配";
        package["parameters"]![0]!["address"] = 112;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportJsonAsync(package.ToJsonString()));
    }

    [Fact]
    public async Task RecipeRequiresUniqueWritableNumericTagsAndValidEngineeringValues()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var writable = await SaveWritableTagAsync(system, "速度设定", 100, 500);
        var readOnly = await SaveWritableTagAsync(system, "速度监控", 102, null, AccessMode.ReadOnly);
        var integer = await SaveWritableTagAsync(system, "时间设定", 104, 60, TagDataType.Int16);
        var service = new RecipeService(system.Store);

        Assert.Contains("Int16", RecipeService.GetEngineeringValueError(integer, 45.5)!);
        Assert.Null(RecipeService.GetEngineeringValueError(integer, 45));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(null, null, system.Device.Id, "只读测试", "", [new(readOnly.Id, 100)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(null, null, system.Device.Id, "重复测试", "", [new(writable.Id, 100), new(writable.Id, 200)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(null, null, system.Device.Id, "越界测试", "", [new(writable.Id, 501)]));
        var fractionalInteger = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(null, null, system.Device.Id, "整数小数测试", "", [new(integer.Id, 45.5)]));
        Assert.Contains("Int16", fractionalInteger.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(null, null, system.Device.Id, "空配方", "", []));

        Assert.Empty(await service.GetAllAsync());
    }

    [Fact]
    public async Task RecipeParametersAreRestrictedToTheSelectedDevice()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var secondDevice = new Device { Name = "Second PLC" };
        await system.Devices.SaveAsync(secondDevice);
        var firstTag = await SaveWritableTagAsync(system, "第一设备设定", 100, 500);
        var secondTag = new Tag
        {
            Name = "第二设备设定",
            DeviceId = secondDevice.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.Float32,
            AccessMode = AccessMode.ReadWrite,
            MinValue = 0,
            MaxValue = 500
        };
        await system.Tags.SaveAsync(secondTag);
        var service = new RecipeService(system.Store);

        Assert.Contains(await service.GetEligibleTagsAsync(system.Device.Id), x => x.Id == firstTag.Id);
        Assert.DoesNotContain(await service.GetEligibleTagsAsync(system.Device.Id), x => x.Id == secondTag.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(null, null, system.Device.Id, "跨设备配方", "", [
                new(firstTag.Id, 100),
                new(secondTag.Id, 200)
            ]));
        Assert.Empty(await service.GetAllAsync());
    }

    [Fact]
    public async Task BoolCoilTagsCanBeSavedAndEditedAsTypedSwitchValues()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "扫码启用",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 20,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(tag);
        var service = new RecipeService(system.Store);

        Assert.Contains(await service.GetEligibleTagsAsync(system.Device.Id), x => x.Id == tag.Id);

        var recipeId = await service.SaveAsync(
            null, null, system.Device.Id, "扫码工况", "", [new RecipeService.ParameterInput(tag.Id, BooleanValue: true)]);

        var recipe = Assert.Single(await service.GetAllAsync());
        var parameter = Assert.Single(recipe.Parameters);
        Assert.Null(parameter.EngineeringValue);
        Assert.True(parameter.BooleanValue);

        await service.SaveAsync(
            recipeId, recipe.Revision, system.Device.Id, "扫码工况", "", [new RecipeService.ParameterInput(tag.Id, BooleanValue: false)]);

        parameter = Assert.Single(Assert.Single(await service.GetAllAsync()).Parameters);
        Assert.Null(parameter.EngineeringValue);
        Assert.False(parameter.BooleanValue);
    }

    [Fact]
    public async Task BoolRecipeTagsRequireBooleanValuesAndWritableCoils()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var writableBool = new Tag
        {
            Name = "相机灯开关",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 21,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        var readOnlyBool = new Tag
        {
            Name = "扫码输入监控",
            DeviceId = system.Device.Id,
            AddressType = AddressType.DiscreteInput,
            Address = 22,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadOnly
        };
        await system.Tags.SaveAsync(writableBool);
        await system.Tags.SaveAsync(readOnlyBool);
        var service = new RecipeService(system.Store);

        Assert.DoesNotContain(await service.GetEligibleTagsAsync(system.Device.Id), x => x.Id == readOnlyBool.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(null, null, system.Device.Id, "错误值类型", "", [new RecipeService.ParameterInput(writableBool.Id, 1)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(null, null, system.Device.Id, "只读Bool", "", [new RecipeService.ParameterInput(readOnlyBool.Id, BooleanValue: true)]));
        Assert.Empty(await service.GetAllAsync());
    }

    [Fact]
    public async Task BoolCoilAlreadyBoundToManualOutputCannotBeAddedToRecipe()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "通电控制",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 30,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(tag);

        var manualOutputs = new ManualOutputService(system.Store, system.Communication);
        var output = (await manualOutputs.GetOutputBindingsAsync(system.Device.Id)).First();
        await manualOutputs.SaveOutputPointConfigurationAsync(
            system.Device.Id, output.Point.Id, output.Point.Name, tag.Id);

        var recipes = new RecipeService(system.Store);
        Assert.DoesNotContain(await recipes.GetEligibleTagsAsync(system.Device.Id), x => x.Id == tag.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recipes.SaveAsync(null, null, system.Device.Id, "不允许绑定", "", [
                new RecipeService.ParameterInput(tag.Id, BooleanValue: true)
            ]));
    }

    [Fact]
    public async Task RecipeReferencePreventsTagDeletionUntilRemovedFromRecipe()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveWritableTagAsync(system, "功率上限设定", 100, 1000);
        var service = new RecipeService(system.Store);
        var recipeId = await service.SaveAsync(null, null, system.Device.Id, "高功率工况", "", [new(tag.Id, 800)]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Tags.DeleteAsync(tag.Id));
        Assert.Contains("配方引用", error.Message);

        await service.DeleteAsync(recipeId);
        await system.Tags.DeleteAsync(tag.Id);
        Assert.DoesNotContain(await system.Store.GetTagsAsync(), x => x.Id == tag.Id);
    }

    private static async Task<Tag> SaveWritableTagAsync(
        TestSystem system,
        string name,
        int address,
        double? maximum,
        TagDataType dataType = TagDataType.Float32)
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
            MaxValue = maximum
        };
        await system.Tags.SaveAsync(tag);
        return tag;
    }

    private static Task<Tag> SaveWritableTagAsync(
        TestSystem system,
        string name,
        int address,
        double? maximum,
        AccessMode accessMode)
        => SaveTagAsync(system, name, address, maximum, TagDataType.Float32, accessMode);

    private static async Task<Tag> SaveTagAsync(
        TestSystem system,
        string name,
        int address,
        double? maximum,
        TagDataType dataType,
        AccessMode accessMode)
    {
        var tag = new Tag
        {
            Name = name,
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = address,
            DataType = dataType,
            AccessMode = accessMode,
            MinValue = 0,
            MaxValue = maximum
        };
        await system.Tags.SaveAsync(tag);
        return tag;
    }
}
