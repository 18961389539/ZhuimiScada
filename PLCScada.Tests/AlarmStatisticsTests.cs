using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Core;
using PLCScada.Infrastructure.Persistence;

namespace PLCScada.Tests;

public sealed class AlarmStatisticsTests
{
    [Fact]
    public async Task StatisticsSeparateRangeFlowFromCurrentSnapshot()
    {
        await using var system = await TestSystem.CreateAsync();
        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.AlarmRecords.AddRange(
                new AlarmRecord
                {
                    Source = "Device:11111111-1111-1111-1111-111111111111", Message = "常驻活动",
                    OccurredAt = now.AddHours(-30), Status = AlarmStatus.Active, Priority = AlarmPriority.P1
                },
                new AlarmRecord
                {
                    Source = "Tag:22222222-2222-2222-2222-222222222222:High", Message = "近期活动",
                    OccurredAt = now.AddHours(-1), Status = AlarmStatus.Active
                },
                new AlarmRecord
                {
                    Source = "Tag:22222222-2222-2222-2222-222222222222:High", Message = "已确认",
                    OccurredAt = now.AddHours(-2), AcknowledgedAt = now.AddHours(-2).AddSeconds(300),
                    Status = AlarmStatus.Acknowledged
                },
                new AlarmRecord
                {
                    Source = "AlarmDef:33333333-3333-3333-3333-333333333333", Message = "已恢复未确认",
                    OccurredAt = now.AddDays(-10), ClearedAt = now.AddDays(-10),
                    Status = AlarmStatus.ClearedUnacknowledged
                },
                new AlarmRecord
                {
                    Source = "Device:44444444-4444-4444-4444-444444444444", Message = "搁置中",
                    OccurredAt = now.AddHours(-1), Status = AlarmStatus.Active, ShelvedUntil = now.AddMinutes(30)
                },
                new AlarmRecord
                {
                    Source = "Device:55555555-5555-5555-5555-555555555555", Message = "超范围旧活动",
                    OccurredAt = now.AddDays(-40), Status = AlarmStatus.Active
                });
            await db.SaveChangesAsync();
        }

        var stats = await system.Store.GetAlarmStatisticsAsync(now.AddDays(-30));

        Assert.Equal(5, stats.RangeTotalCount);
        Assert.Equal(1, stats.RangeAcknowledgedCount);
        Assert.InRange(stats.AverageAcknowledgeSeconds!.Value, 290, 310);
        Assert.Equal(5, stats.ActiveCount);
        Assert.Equal(4, stats.UnacknowledgedCount);
        Assert.Equal(1, stats.ClearedUnacknowledgedCount);
        Assert.Equal(1, stats.ShelvedActiveCount);
        Assert.Equal(2, stats.StandingAlarmCount);
        Assert.Equal(2, stats.StandingAlarms.Count);
        Assert.Equal("Tag:22222222-2222-2222-2222-222222222222:High", stats.TopSources[0].Source);
        Assert.Equal(2, stats.TopSources[0].Count);
    }

    [Fact]
    public async Task EmptyDatabaseReturnsZeroedStatistics()
    {
        await using var system = await TestSystem.CreateAsync();

        var stats = await system.Store.GetAlarmStatisticsAsync(DateTimeOffset.UtcNow.AddDays(-7));

        Assert.Equal(0, stats.RangeTotalCount);
        Assert.Equal(0, stats.RangeAcknowledgedCount);
        Assert.Null(stats.AverageAcknowledgeSeconds);
        Assert.Equal(0, stats.ActiveCount);
        Assert.Equal(0, stats.StandingAlarmCount);
        Assert.Empty(stats.TopSources);
        Assert.Empty(stats.StandingAlarms);
    }
}
