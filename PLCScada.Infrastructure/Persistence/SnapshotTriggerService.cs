using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Infrastructure.Persistence;

// 触发快照后台服务：每秒检查启用的规则；触发位为 True 时抓取点位、保存记录并复位为 False。
// 规则列表每 3 秒刷新，规则增删改无需重启。同一规则 3 秒内只抓一次（防复位失败造成重复）。
public sealed class SnapshotTriggerService(
    IScadaStore store,
    ITagRuntime runtime,
    SnapshotCaptureService capture,
    ILogger<SnapshotTriggerService> logger) : BackgroundService
{
    private IReadOnlyList<SnapshotRule> _rules = [];
    private DateTimeOffset _rulesLoadedAt = DateTimeOffset.MinValue;
    private readonly Dictionary<Guid, DateTimeOffset> _lastCapture = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - _rulesLoadedAt > TimeSpan.FromSeconds(3))
                {
                    _rules = await store.GetSnapshotRulesAsync(stoppingToken);
                    _rulesLoadedAt = DateTimeOffset.UtcNow;
                }

                if (_rules.Any(x => x.Enabled))
                {
                    var sn = await GetCurrentSnAsync(stoppingToken);
                    foreach (var rule in _rules.Where(x => x.Enabled))
                    {
                        if (_lastCapture.TryGetValue(rule.Id, out var last) &&
                            DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(3))
                            continue;
                        if (await capture.TryCaptureAsync(rule, sn, stoppingToken))
                        {
                            _lastCapture[rule.Id] = DateTimeOffset.UtcNow;
                            logger.LogInformation("触发快照已记录：{Rule}（SN={Sn}）", rule.Name, sn ?? "—");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "触发快照处理失败");
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

    private async Task<string?> GetCurrentSnAsync(CancellationToken ct)
    {
        var tagId = await store.GetSnTagIdAsync(ct);
        if (tagId is not Guid id) return null;
        return runtime.GetTagSnapshot(id).CurrentValue is string text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
    }
}
