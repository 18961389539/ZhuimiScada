using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PLCScada.Core;

namespace PLCScada.Infrastructure.Persistence;

// SN 追溯自动记录：每秒读取 SN 来源 Tag（PLC 字符串），值非空且变化时落一条 SN 记录。
// 来源配置（Trace.SnTagId）每 10 秒刷新一次，切换 Tag 无需重启。
public sealed class SnWatcherService(
    IScadaStore store, ITagRuntime runtime, ILogger<SnWatcherService> logger) : BackgroundService
{
    private string? _lastSn;
    private Guid? _tagId;
    private DateTimeOffset _tagLoadedAt = DateTimeOffset.MinValue;
    private string? _deviceName;
    private string? _recipeName;
    private int? _recipeVersion;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - _tagLoadedAt > TimeSpan.FromSeconds(10))
                {
                    _tagLoadedAt = DateTimeOffset.UtcNow;
                    var tagId = await store.GetSnTagIdAsync(stoppingToken);
                    if (tagId != _tagId)
                    {
                        _tagId = tagId;
                        _lastSn = null;
                    }
                    if (tagId is Guid snTagId)
                    {
                        var tag = (await store.GetTagsAsync(stoppingToken))
                            .FirstOrDefault(x => x.Id == snTagId);
                        _deviceName = tag?.Device?.Name;
                    }
                    var (recipeName, recipeVersion) = await store.GetActiveRecipeAsync(stoppingToken);
                    _recipeName = recipeName;
                    _recipeVersion = recipeVersion;
                }

                if (_tagId is Guid id &&
                    runtime.GetTagSnapshot(id).CurrentValue is string text &&
                    !string.IsNullOrWhiteSpace(text))
                {
                    var sn = text.Trim();
                    if (sn.Length <= 200 && sn != _lastSn)
                    {
                        await store.AddSnRecordAsync(
                            sn, "PLC", _deviceName, _recipeName, _recipeVersion, null, stoppingToken);
                        _lastSn = sn;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SN 自动记录失败");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
