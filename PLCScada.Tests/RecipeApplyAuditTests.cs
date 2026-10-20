using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class RecipeApplyAuditTests
{
    [Fact]
    public async Task EveryApplyAttemptIsRecordedWithStructuredOutcome()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "功率上限设定",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.Float32,
            AccessMode = AccessMode.ReadWrite,
            MinValue = 0,
            MaxValue = 100
        };
        await system.Tags.SaveAsync(tag);
        await system.Devices.ConnectAsync(system.Device.Id);
        var recipes = new RecipeService(system.Store);
        var recipeId = await recipes.SaveAsync(
            null, null, system.Device.Id, "下发记录测试工况", "", [new(tag.Id, 12.5)]);

        var applier = new RecipeApplyService(system.Store, system.Communication, system.RecipeApplyLock);
        var preview = await applier.PreviewAsync(recipeId);
        Assert.True(preview.CanApply);
        Assert.Equal(0, (await recipes.GetApplyRecordsAsync(recipeId)).TotalCount);

        var applied = await applier.ApplyAsync(
            recipeId, preview.RecipeRevision, preview.ConfigurationFingerprint,
            preview.CurrentValuesFingerprint);
        Assert.True(applied.Succeeded);

        var unchangedPreview = await applier.PreviewAsync(recipeId);
        var unchanged = await applier.ApplyAsync(
            recipeId, unchangedPreview.RecipeRevision, unchangedPreview.ConfigurationFingerprint,
            unchangedPreview.CurrentValuesFingerprint);
        Assert.True(unchanged.Succeeded);
        Assert.False(unchanged.WritesStarted);

        await system.Devices.DisconnectAsync(system.Device.Id);
        var offlinePreview = await applier.PreviewAsync(recipeId);
        Assert.False(offlinePreview.CanApply);
        var offline = await applier.ApplyAsync(
            recipeId, offlinePreview.RecipeRevision, offlinePreview.ConfigurationFingerprint,
            offlinePreview.CurrentValuesFingerprint);
        Assert.False(offline.Succeeded);

        await recipes.SaveAsync(
            recipeId, 1, system.Device.Id, "下发记录测试工况", "", [new(tag.Id, 30)],
            changeNote: "调整功率上限至 30");
        var stale = await applier.ApplyAsync(
            recipeId, 1, unchangedPreview.ConfigurationFingerprint,
            unchangedPreview.CurrentValuesFingerprint);
        Assert.False(stale.Succeeded);

        var page = await recipes.GetApplyRecordsAsync(recipeId);
        Assert.Equal(4, page.TotalCount);
        var records = page.Records;
        Assert.False(records[0].Succeeded);
        Assert.Contains("已被修改", records[0].Summary);
        Assert.False(records[1].Succeeded);
        Assert.Contains("未在线", records[1].Summary);
        Assert.True(records[2].Succeeded);
        Assert.Contains("完全一致", records[2].Summary);
        Assert.True(records[3].Succeeded);
        Assert.Contains("已写入", records[3].Summary);
        Assert.All(records, record => Assert.Equal("下发记录测试工况", record.RecipeName));
        Assert.All(records, record => Assert.Equal(system.Device.Id, record.DeviceId));
        Assert.All(records, record => Assert.Equal(1, record.Revision));
        Assert.All(records, record => Assert.True(record.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5)));
    }

    [Fact]
    public async Task ApplyRecordsPaginateClampAndSurviveRecipeDeletion()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "转数设定",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.UInt16,
            AccessMode = AccessMode.ReadWrite,
            MinValue = 0,
            MaxValue = 1000
        };
        await system.Tags.SaveAsync(tag);
        var recipes = new RecipeService(system.Store);
        var recipeId = await recipes.SaveAsync(null, null, system.Device.Id, "记录分页工况", "", [new(tag.Id, 5)]);

        for (var index = 1; index <= 13; index++)
            await system.Store.RecordRecipeApplyAsync(recipeId, 1, index % 2 == 0, $"第 {index} 次尝试");

        var newest = await recipes.GetApplyRecordsAsync(recipeId, 0, 5);
        Assert.Equal(13, newest.TotalCount);
        Assert.Equal(3, newest.PageCount);
        Assert.Equal(new[] { "第 13 次尝试", "第 12 次尝试", "第 11 次尝试", "第 10 次尝试", "第 9 次尝试" },
            newest.Records.Select(x => x.Summary));
        Assert.False(newest.Records[0].Succeeded);
        Assert.True(newest.Records[1].Succeeded);

        var middle = await recipes.GetApplyRecordsAsync(recipeId, 1, 5);
        Assert.Equal(new[] { "第 8 次尝试", "第 7 次尝试", "第 6 次尝试", "第 5 次尝试", "第 4 次尝试" },
            middle.Records.Select(x => x.Summary));
        var oldest = await recipes.GetApplyRecordsAsync(recipeId, 2, 5);
        Assert.Equal(new[] { "第 3 次尝试", "第 2 次尝试", "第 1 次尝试" },
            oldest.Records.Select(x => x.Summary));
        var clamped = await recipes.GetApplyRecordsAsync(recipeId, 99, 5);
        Assert.Equal(2, clamped.PageIndex);
        Assert.Equal(oldest.Records.Select(x => x.Summary), clamped.Records.Select(x => x.Summary));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            recipes.GetApplyRecordsAsync(recipeId, pageIndex: -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            recipes.GetApplyRecordsAsync(recipeId, pageSize: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            recipes.GetApplyRecordsAsync(recipeId, pageSize: 51));

        var empty = await recipes.GetApplyRecordsAsync(Guid.NewGuid());
        Assert.Equal(0, empty.TotalCount);
        Assert.Empty(empty.Records);

        await recipes.DeleteAsync(recipeId);
        var afterDelete = await recipes.GetApplyRecordsAsync(recipeId);
        Assert.Equal(13, afterDelete.TotalCount);
        Assert.All(afterDelete.Records, record => Assert.Equal("记录分页工况", record.RecipeName));
    }
}
