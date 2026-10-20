using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Core;
using PLCScada.Infrastructure.Persistence;

namespace PLCScada.Tests;

public sealed class AlarmQueryTests
{
    [Fact]
    public async Task QueryFiltersSortsAndPages()
    {
        await using var system = await TestSystem.CreateAsync();
        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.AlarmRecords.AddRange(
                new AlarmRecord
                {
                    Source = "Device:AAAAAAAA-1111-1111-1111-111111111111", Message = "P1活动",
                    Priority = AlarmPriority.P1, OccurredAt = now.AddHours(-30), Status = AlarmStatus.Active
                },
                new AlarmRecord
                {
                    Source = "Tag:BBBBBBBB-1111-1111-1111-111111111111:High", Message = "P2活动",
                    Priority = AlarmPriority.P2, OccurredAt = now.AddMinutes(-10), Status = AlarmStatus.Active,
                    ShelvedUntil = now.AddMinutes(30)
                },
                new AlarmRecord
                {
                    Source = "Tag:BBBBBBBB-1111-1111-1111-111111111111:Low", Message = "P2已解除",
                    Priority = AlarmPriority.P2, OccurredAt = now.AddMinutes(-5), ClearedAt = now,
                    Status = AlarmStatus.Cleared
                },
                new AlarmRecord
                {
                    Source = "AlarmDef:CCCCCCCC-1111-1111-1111-111111111111", Message = "RTN",
                    Priority = AlarmPriority.P3, OccurredAt = now.AddHours(-2),
                    Status = AlarmStatus.ClearedUnacknowledged
                });
            await db.SaveChangesAsync();
        }

        var active = await system.Store.QueryAlarmsAsync(new AlarmQuery(ActiveOnly: true));
        Assert.Equal(2, active.TotalCount);

        var p1 = await system.Store.QueryAlarmsAsync(
            new AlarmQuery(ActiveOnly: true, Priorities: [AlarmPriority.P1]));
        Assert.Equal(1, p1.TotalCount);
        Assert.Equal("P1活动", p1.Items[0].Message);

        var bySource = await system.Store.QueryAlarmsAsync(new AlarmQuery(
            Sources: ["Tag:BBBBBBBB-1111-1111-1111-111111111111:Low"]));
        Assert.Equal(1, bySource.TotalCount);

        var shelved = await system.Store.QueryAlarmsAsync(new AlarmQuery(ShelvedOnly: true));
        Assert.Equal(1, shelved.TotalCount);
        Assert.Equal("P2活动", shelved.Items[0].Message);

        var standing = await system.Store.QueryAlarmsAsync(new AlarmQuery(StandingOnly: true));
        Assert.Equal(1, standing.TotalCount);
        Assert.Equal("P1活动", standing.Items[0].Message);

        var byPrioritySort = await system.Store.QueryAlarmsAsync(new AlarmQuery(PageSize: 10));
        Assert.Equal("P1活动", byPrioritySort.Items[0].Message);

        var byTimeSort = await system.Store.QueryAlarmsAsync(new AlarmQuery(SortByTime: true, PageSize: 10));
        Assert.Equal("P2已解除", byTimeSort.Items[0].Message);

        var page2 = await system.Store.QueryAlarmsAsync(
            new AlarmQuery(SortByTime: true, Page: 2, PageSize: 10));
        Assert.Empty(page2.Items);
        Assert.Equal(4, page2.TotalCount);
    }

    [Fact]
    public async Task ExportRespectsFilterAndCap()
    {
        await using var system = await TestSystem.CreateAsync();
        for (var index = 0; index < 6; index++)
            await system.Store.SetTagAlarmAsync(
                Guid.NewGuid(), $"点位{index}", "超限", "High");

        var exported = await system.Store.QueryAlarmsForExportAsync(
            new AlarmQuery(SourcePrefix: "Tag:"), cap: 4);
        Assert.Equal(4, exported.Count);
        Assert.All(exported, x => Assert.StartsWith("Tag:", x.Source));
    }
}
