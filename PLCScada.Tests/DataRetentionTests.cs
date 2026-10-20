using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Core;
using PLCScada.Infrastructure.Persistence;

namespace PLCScada.Tests;

public sealed class DataRetentionTests
{
    [Fact]
    public async Task CleanupDeletesOnlyExpiredRecordsAndKeepsActiveAlarms()
    {
        await using var system = await TestSystem.CreateAsync();
        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.OperationLogs.Add(new OperationLog { Timestamp = now.AddDays(-40), Source = "Test", Message = "旧日志" });
            db.OperationLogs.Add(new OperationLog { Timestamp = now.AddDays(-5), Source = "Test", Message = "新日志" });
            db.AlarmRecords.Add(new AlarmRecord { OccurredAt = now.AddDays(-100), Status = AlarmStatus.Cleared, Message = "旧已解除" });
            db.AlarmRecords.Add(new AlarmRecord { OccurredAt = now.AddDays(-5), Status = AlarmStatus.Cleared, Message = "新已解除" });
            db.AlarmRecords.Add(new AlarmRecord { OccurredAt = now.AddDays(-100), Status = AlarmStatus.Active, Message = "旧未解除" });
            db.RecipeApplyRecords.Add(new RecipeApplyRecord
            {
                RecipeId = Guid.NewGuid(), RecipeName = "旧下发", Summary = "", OccurredAt = now.AddDays(-200)
            });
            db.RecipeApplyRecords.Add(new RecipeApplyRecord
            {
                RecipeId = Guid.NewGuid(), RecipeName = "新下发", Summary = "", OccurredAt = now.AddDays(-5)
            });
            await db.SaveChangesAsync();
        }

        var result = await system.Store.CleanupHistoryAsync(new DataRetentionOptions
        {
            OperationLogRetentionDays = 30,
            ClearedAlarmRetentionDays = 90,
            RecipeApplyRecordRetentionDays = 180
        });

        Assert.Equal(1, result.OperationLogsDeleted);
        Assert.Equal(1, result.ClearedAlarmsDeleted);
        Assert.Equal(1, result.RecipeApplyRecordsDeleted);
        Assert.Contains(await system.Store.GetLogsAsync(), x => x.Message.Contains("数据保留清理"));
        var alarms = await system.Store.GetAlarmsAsync();
        Assert.Contains(alarms, x => x.Message == "新已解除");
        Assert.Contains(alarms, x => x.Message == "旧未解除");
        Assert.DoesNotContain(alarms, x => x.Message == "旧已解除");
        await using (var verify = await factory.CreateDbContextAsync())
        {
            Assert.False(await verify.OperationLogs.AnyAsync(x => x.Message == "旧日志"));
            Assert.True(await verify.OperationLogs.AnyAsync(x => x.Message == "新日志"));
            Assert.False(await verify.RecipeApplyRecords.AnyAsync(x => x.RecipeName == "旧下发"));
            Assert.True(await verify.RecipeApplyRecords.AnyAsync(x => x.RecipeName == "新下发"));
        }
    }

    [Fact]
    public async Task CleanupWithZeroRetentionKeepsEverything()
    {
        await using var system = await TestSystem.CreateAsync();
        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.OperationLogs.Add(new OperationLog { Timestamp = now.AddDays(-3000), Source = "Test", Message = "远古日志" });
            db.AlarmRecords.Add(new AlarmRecord { OccurredAt = now.AddDays(-3000), Status = AlarmStatus.Cleared, Message = "远古报警" });
            await db.SaveChangesAsync();
        }

        var result = await system.Store.CleanupHistoryAsync(new DataRetentionOptions
        {
            OperationLogRetentionDays = 0,
            ClearedAlarmRetentionDays = 0,
            RecipeApplyRecordRetentionDays = 0
        });

        Assert.Equal(new RetentionCleanupResult(0, 0, 0), result);
        await using (var verify = await factory.CreateDbContextAsync())
        {
            Assert.True(await verify.OperationLogs.AnyAsync(x => x.Message == "远古日志"));
            Assert.True(await verify.AlarmRecords.AnyAsync(x => x.Message == "远古报警"));
        }
        Assert.DoesNotContain(await system.Store.GetLogsAsync(), x => x.Message.Contains("数据保留清理"));
    }
}
