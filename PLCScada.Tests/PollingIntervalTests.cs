using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Application;
using PLCScada.Core;
using PLCScada.Infrastructure.Persistence;

namespace PLCScada.Tests;

public sealed class PollingIntervalTests
{
    [Fact]
    public async Task GlobalScanIntervalRoundTripsWithFallbackAndValidation()
    {
        await using var system = await TestSystem.CreateAsync();

        // 未设置 → 回退默认
        Assert.Equal(1000, await system.Store.GetTagScanIntervalAsync(1000));

        await system.Store.SaveTagScanIntervalAsync(250);
        Assert.Equal(250, await system.Store.GetTagScanIntervalAsync(1000));
        Assert.Contains(await system.Store.GetLogsAsync(), x => x.Message.Contains("Polling.TagScanIntervalMs=250"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SaveTagScanIntervalAsync(249));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SaveTagScanIntervalAsync(60001));

        // 脏数据回退默认
        await system.Store.SaveSettingsAsync(new Dictionary<string, string>
        {
            ["Polling.TagScanIntervalMs"] = "abc"
        });
        Assert.Equal(1000, await system.Store.GetTagScanIntervalAsync(1000));
    }

    [Fact]
    public async Task QualityAlarmThresholdRoundTripsWithFallbackAndValidation()
    {
        await using var system = await TestSystem.CreateAsync();

        Assert.Equal(0, await system.Store.GetQualityAlarmFailuresAsync(0));
        await system.Store.SaveQualityAlarmFailuresAsync(5);
        Assert.Equal(5, await system.Store.GetQualityAlarmFailuresAsync(0));
        Assert.Contains(await system.Store.GetLogsAsync(),
            x => x.Message.Contains("Alarms.QualityAlarmFailures=5"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SaveQualityAlarmFailuresAsync(-1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SaveQualityAlarmFailuresAsync(1001));

        await system.Store.SaveSettingsAsync(new Dictionary<string, string>
        {
            ["Alarms.QualityAlarmFailures"] = "oops"
        });
        Assert.Equal(0, await system.Store.GetQualityAlarmFailuresAsync(0));
    }

    [Fact]
    public void EffectiveIntervalPrefersTagThenGlobalThenDeviceFloor()
    {
        Assert.Equal(250, PollingMath.EffectiveTagIntervalMs(250, 250));
        Assert.Equal(500, PollingMath.EffectiveTagIntervalMs(500, 250));
        Assert.Equal(1000, PollingMath.EffectiveTagIntervalMs(250, 1000));
    }
}
