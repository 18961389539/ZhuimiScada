using PLCScada.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PLCScada.Core;

namespace PLCScada.Infrastructure.Communication;

public sealed class ModbusPollingService(IScadaStore store, IDeviceCommunication communication, TagRuntime runtime,
    ILogger<ModbusPollingService> logger) : BackgroundService
{
    private sealed record Worker(CancellationTokenSource Cancellation, Task Task);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = new Dictionary<Guid, Worker>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var devices = await store.GetDevicesAsync(stoppingToken);
                runtime.RemoveMissing(await store.GetTagsAsync(stoppingToken));
                foreach (var id in workers.Keys.Where(id => devices.All(d => d.Id != id)).ToArray())
                { var worker = workers[id]; await worker.Cancellation.CancelAsync(); await worker.Task; worker.Cancellation.Dispose(); workers.Remove(id); await communication.DisconnectAsync(id, stoppingToken); }
                foreach (var device in devices.Where(x => !workers.ContainsKey(x.Id)))
                {
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    workers.Add(device.Id, new(cts, RunDeviceAsync(device.Id, cts.Token)));
                }
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var worker in workers.Values) await worker.Cancellation.CancelAsync();
            await Task.WhenAll(workers.Values.Select(x => x.Task));
            foreach (var worker in workers.Values) worker.Cancellation.Dispose();
            foreach (var id in workers.Keys) await communication.DisconnectAsync(id);
            logger.LogInformation("轮询服务已停止，全部设备连接已释放");
            await store.LogAsync("System", "PLCScada 采集服务已停止，连接已释放");
        }
    }
    private async Task RunDeviceAsync(Guid id, CancellationToken ct)
    {
        var nextRead = new Dictionary<Guid, DateTimeOffset>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var device = await store.GetDeviceAsync(id, ct);
                if (device is null) return;
                try
                {
                    await communication.PollAsync(device, ct);
                    var globalScanInterval = await store.GetTagScanIntervalAsync(1000, ct);
                    var tags = (await store.GetTagsAsync(ct)).Where(x => x.DeviceId == id).ToArray();
                    var online = communication.GetSnapshot(id).Status == ConnectionStatus.Online;
                    var now = DateTimeOffset.UtcNow;
                    var dueTags = new List<Tag>();
                    foreach (var tag in tags)
                    {
                        if (!tag.Enabled || !online)
                        { runtime.MarkUnavailable(tag, "设备未在线或 Tag 已禁用"); continue; }
                        if (nextRead.GetValueOrDefault(tag.Id) > now) continue;
                        dueTags.Add(tag);
                    }

                    // 数值寄存器与位（线圈）分别按连续地址合并为单次读取
                    var batchTags = dueTags
                        .Where(tag => tag.DataType != TagDataType.Bool &&
                                      tag.AddressType is AddressType.HoldingRegister or AddressType.InputRegister)
                        .ToArray();
                    var bitTags = dueTags.Where(tag => tag.DataType == TagDataType.Bool).ToArray();
                    foreach (var group in TagReadGrouping.BuildGroups(batchTags))
                    {
                        if (group.Tags.Count > 1)
                            await runtime.ReadGroupAsync(
                                device, group.AddressType, (ushort)group.StartAddress,
                                (ushort)group.RegisterCount, group.Tags, ct);
                        else
                            await runtime.ReadAsync(group.Tags[0], ct);
                        var scheduledFrom = DateTimeOffset.UtcNow;
                        foreach (var tag in group.Tags)
                            nextRead[tag.Id] = scheduledFrom.AddMilliseconds(
                                PollingMath.EffectiveTagIntervalMs(globalScanInterval, device.PollInterval));
                    }
                    foreach (var bitGroup in TagReadGrouping.BuildBitGroups(bitTags))
                    {
                        if (bitGroup.Tags.Count > 1)
                            await runtime.ReadBitGroupAsync(
                                device, bitGroup.AddressType, (ushort)bitGroup.StartAddress,
                                (ushort)bitGroup.BitCount, bitGroup.Tags, ct);
                        else
                            await runtime.ReadAsync(bitGroup.Tags[0], ct);
                        var scheduledFrom = DateTimeOffset.UtcNow;
                        foreach (var tag in bitGroup.Tags)
                            nextRead[tag.Id] = scheduledFrom.AddMilliseconds(
                                PollingMath.EffectiveTagIntervalMs(globalScanInterval, device.PollInterval));
                    }
                    foreach (var tag in dueTags.Except(batchTags).Except(bitTags))
                    {
                        await runtime.ReadAsync(tag, ct);
                        nextRead[tag.Id] = DateTimeOffset.UtcNow.AddMilliseconds(
                            PollingMath.EffectiveTagIntervalMs(globalScanInterval, device.PollInterval));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "设备 {DeviceId} 本轮采集失败", id); }
                await Task.Delay(device.PollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

