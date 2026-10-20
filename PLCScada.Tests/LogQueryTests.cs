using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Application;
using PLCScada.Core;
using PLCScada.Infrastructure.Persistence;

namespace PLCScada.Tests;

public sealed class LogQueryTests
{
    [Fact]
    public async Task QueryFiltersByKeywordLevelSourceAndDate()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Store.LogAsync("Devices", "设备连接成功", "Information");
        await system.Store.LogAsync("Recipes", "配方下发失败", "Error");
        await system.Store.LogAsync("Tags", "连续读取失败", "Warning");
        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.OperationLogs.Add(new OperationLog
            {
                Timestamp = DateTimeOffset.UtcNow.AddDays(-10), Source = "Old", Message = "远古日志", Level = "Information"
            });
            await db.SaveChangesAsync();
        }

        var keyword = await system.Store.QueryLogsAsync(new LogQuery(Search: "配方", PageSize: 50));
        Assert.Equal(1, keyword.TotalCount);
        Assert.Equal("配方下发失败", keyword.Items[0].Message);

        var levels = await system.Store.QueryLogsAsync(
            new LogQuery(Levels: ["Error", "Warning"], PageSize: 50));
        Assert.Equal(2, levels.TotalCount);

        var source = await system.Store.QueryLogsAsync(new LogQuery(Source: "Devices", PageSize: 50));
        Assert.Equal(1, source.TotalCount);

        var recent = await system.Store.QueryLogsAsync(
            new LogQuery(From: DateTimeOffset.UtcNow.AddDays(-1), PageSize: 50));
        Assert.DoesNotContain(recent.Items, x => x.Message == "远古日志");
        var all = await system.Store.QueryLogsAsync(
            new LogQuery(From: DateTimeOffset.UtcNow.AddDays(-30), PageSize: 50));
        Assert.Contains(all.Items, x => x.Message == "远古日志");
    }

    [Fact]
    public async Task QueryPaginatesNewestFirst()
    {
        await using var system = await TestSystem.CreateAsync();
        for (var index = 0; index < 25; index++)
            await system.Store.LogAsync("BatchTest", $"批量日志 {index}");

        var page1 = await system.Store.QueryLogsAsync(
            new LogQuery(Search: "批量日志", Page: 1, PageSize: 10));
        Assert.Equal(25, page1.TotalCount);
        Assert.Equal(10, page1.Items.Count);
        Assert.Equal("批量日志 24", page1.Items[0].Message);
        Assert.Equal("批量日志 15", page1.Items[^1].Message);

        var page3 = await system.Store.QueryLogsAsync(
            new LogQuery(Search: "批量日志", Page: 3, PageSize: 10));
        Assert.Equal(5, page3.Items.Count);
        Assert.Equal("批量日志 0", page3.Items[^1].Message);

        var capped = await system.Store.QueryLogsForExportAsync(
            new LogQuery(Search: "批量日志"), cap: 7);
        Assert.Equal(7, capped.Count);
        Assert.Equal("批量日志 24", capped[0].Message);
    }

    [Fact]
    public async Task CsvExportEscapesSpecialCharacters()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Store.LogAsync("BatchTest", "内容,含\"引号\"\n第二行");

        var items = await system.Store.QueryLogsForExportAsync(new LogQuery(Search: "引号"));
        var csv = LogCsv.Export(items);

        Assert.StartsWith("时间,级别,来源,内容", csv);
        Assert.Contains("\"内容,含\"\"引号\"\"\n第二行\"", csv);
    }

    [Fact]
    public async Task LogSourcesAreDistinctAndSorted()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Store.LogAsync("Beta", "x");
        await system.Store.LogAsync("Alpha", "x");
        await system.Store.LogAsync("Beta", "y");

        var sources = await system.Store.GetLogSourcesAsync();

        Assert.Equal(sources.OrderBy(x => x, StringComparer.Ordinal), sources);
        Assert.Equal(sources.Distinct().Count(), sources.Count);
        Assert.Contains("Alpha", sources);
        Assert.Contains("Beta", sources);
    }
}
