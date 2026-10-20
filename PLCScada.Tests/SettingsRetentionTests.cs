using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Core;
using PLCScada.Infrastructure.Persistence;

namespace PLCScada.Tests;

public sealed class SettingsRetentionTests
{
    [Fact]
    public async Task RetentionOptionsRoundTripWithDefaultsFallbackAndValidation()
    {
        await using var system = await TestSystem.CreateAsync();
        var defaults = new DataRetentionOptions
        {
            OperationLogRetentionDays = 30,
            ClearedAlarmRetentionDays = 90,
            RecipeApplyRecordRetentionDays = 0
        };

        var initial = await system.Store.GetRetentionOptionsAsync(defaults);
        Assert.Equal(30, initial.OperationLogRetentionDays);
        Assert.Equal(90, initial.ClearedAlarmRetentionDays);
        Assert.Equal(0, initial.RecipeApplyRecordRetentionDays);

        await system.Store.SaveRetentionOptionsAsync(new DataRetentionOptions
        {
            OperationLogRetentionDays = 7,
            ClearedAlarmRetentionDays = 14,
            RecipeApplyRecordRetentionDays = 365
        });
        var saved = await system.Store.GetRetentionOptionsAsync(defaults);
        Assert.Equal(7, saved.OperationLogRetentionDays);
        Assert.Equal(14, saved.ClearedAlarmRetentionDays);
        Assert.Equal(365, saved.RecipeApplyRecordRetentionDays);
        Assert.Contains(await system.Store.GetLogsAsync(), x => x.Message.Contains("更新系统设置"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SaveRetentionOptionsAsync(new DataRetentionOptions { OperationLogRetentionDays = -1 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SaveRetentionOptionsAsync(new DataRetentionOptions { ClearedAlarmRetentionDays = 5000 }));

        // 脏数据（非数字）回退默认配置
        await system.Store.SaveSettingsAsync(new Dictionary<string, string>
        {
            ["Retention.OperationLogDays"] = "abc"
        });
        var fallback = await system.Store.GetRetentionOptionsAsync(defaults);
        Assert.Equal(30, fallback.OperationLogRetentionDays);
        Assert.Equal(14, fallback.ClearedAlarmRetentionDays);
    }

    [Fact]
    public async Task CleanupUsesPersistedRetentionPolicy()
    {
        await using var system = await TestSystem.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.OperationLogs.Add(new OperationLog { Timestamp = now.AddDays(-10), Source = "Test", Message = "十天前" });
            db.OperationLogs.Add(new OperationLog { Timestamp = now.AddDays(-2), Source = "Test", Message = "两天前" });
            await db.SaveChangesAsync();
        }

        await system.Store.SaveRetentionOptionsAsync(new DataRetentionOptions
        {
            OperationLogRetentionDays = 7,
            ClearedAlarmRetentionDays = 90,
            RecipeApplyRecordRetentionDays = 0
        });
        var effective = await system.Store.GetRetentionOptionsAsync(new DataRetentionOptions());
        var result = await system.Store.CleanupHistoryAsync(effective);

        Assert.Equal(1, result.OperationLogsDeleted);
        await using (var verify = await factory.CreateDbContextAsync())
        {
            Assert.False(await verify.OperationLogs.AnyAsync(x => x.Message == "十天前"));
            Assert.True(await verify.OperationLogs.AnyAsync(x => x.Message == "两天前"));
        }
    }
}
