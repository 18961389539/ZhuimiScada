using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PLCScada.Core;

namespace PLCScada.Infrastructure.Persistence;

// 定期执行数据保留清理：宿主启动时立即执行一次，之后按 CheckInterval 周期执行。
public sealed class DataRetentionService(
    IScadaStore store, DataRetentionOptions options, ILogger<DataRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var effective = await store.GetRetentionOptionsAsync(options, stoppingToken);
                var result = await store.CleanupHistoryAsync(effective, stoppingToken);
                if (result.OperationLogsDeleted + result.ClearedAlarmsDeleted + result.RecipeApplyRecordsDeleted > 0)
                    logger.LogInformation(
                        "数据保留清理完成：日志 {Logs}、已解除报警 {Alarms}、下发记录 {Applies}",
                        result.OperationLogsDeleted, result.ClearedAlarmsDeleted, result.RecipeApplyRecordsDeleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "数据保留清理失败，将在下一个周期重试");
            }

            try
            {
                await Task.Delay(options.CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
