using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Application;
using PLCScada.Core;
using PLCScada.Infrastructure.Communication;

namespace PLCScada.Tests;

public sealed class CommunicationTests
{
    [Fact]
    public async Task ProbeRequiresRealModbusResponseAndPreservesNetworkByteOrder()
    {
        await using var system = await TestSystem.CreateAsync();
        var result = await system.Communication.TestAsync(system.Device);
        Assert.True(result.Success, result.Message); Assert.Equal((ushort)1234, result.RegisterValue);
        system.Device.UnitId = 2;
        var bad = await system.Communication.TestAsync(system.Device);
        Assert.False(bad.Success); Assert.Contains("异常", bad.Message);
    }
    [Fact]
    public async Task SimulatorSupportsAllEightFunctionCodesAndReadback()
    {
        await using var system = await TestSystem.CreateAsync();
        await using var client = new IoTClientConnection(system.Device); await client.OpenAsync(default);
        await client.WriteCoilAsync(1, 20, true, default);
        Assert.Equal(1, (await client.ReadAsync(1, AddressType.Coil, 20, 1, default))[0]);
        Assert.Equal(1, (await client.ReadAsync(1, AddressType.DiscreteInput, 20, 1, default))[0]);
        await client.WriteSingleRegisterAsync(1, 30, 0x1234, default);
        Assert.Equal(new byte[] { 0x12, 0x34 }, await client.ReadAsync(1, AddressType.HoldingRegister, 30, 1, default));
        await client.WriteRegistersAsync(1, 31, [0xab, 0xcd, 0x56, 0x78], default);
        Assert.Equal(new byte[] { 0xab, 0xcd, 0x56, 0x78 }, await client.ReadAsync(1, AddressType.InputRegister, 31, 2, default));
        await client.WriteCoilsAsync(1, 40, [true, false, true, true, false, false, true, false, true], default);
        Assert.Equal(new byte[] { 0x4d, 1 }, await client.ReadAsync(1, AddressType.Coil, 40, 9, default));
    }
    [Fact]
    public async Task DisconnectionRaisesSingleAlarmAndRecoveryClearsIt()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Communication.ConnectAsync(system.Device);
        await system.Simulator.StopAsync();
        await system.Communication.PollAsync(system.Device, default);
        await system.Communication.PollAsync(system.Device, default);
        Assert.Equal(ConnectionStatus.Faulted, system.Communication.GetSnapshot(system.Device.Id).Status);
        var alarm = Assert.Single(await system.Store.GetAlarmsAsync());
        await system.Store.AcknowledgeAlarmAsync(alarm.AlarmId);
        Assert.Equal(AlarmStatus.Acknowledged, (await system.Store.GetAlarmsAsync()).Single().Status);
        Assert.NotNull((await system.Store.GetAlarmsAsync()).Single().AcknowledgedAt);
        await system.Simulator.StartAsync(); await system.Communication.PollAsync(system.Device, default);
        Assert.Equal(ConnectionStatus.Online, system.Communication.GetSnapshot(system.Device.Id).Status);
        Assert.Equal(AlarmStatus.Cleared, (await system.Store.GetAlarmsAsync()).Single().Status);
    }
    [Fact]
    public async Task NewFailureAfterRecoveryRearmsAlarmImmediatelyWithMergeCounter()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Communication.ConnectAsync(system.Device);
        await system.Simulator.StopAsync(); await system.Communication.PollAsync(system.Device, default);
        await system.Simulator.StartAsync(); await system.Communication.PollAsync(system.Device, default);
        await system.Simulator.StopAsync(); await system.Communication.PollAsync(system.Device, default);
        // 60 秒窗口内重复故障合并为同一条记录：重新置为活动并累计重复次数
        var alarm = Assert.Single(await system.Store.GetAlarmsAsync());
        Assert.Equal(AlarmStatus.Active, alarm.Status);
        Assert.Equal(2, alarm.RepeatCount);
    }
    [Fact]
    public async Task ManualDisconnectSuppressesReconnectUntilExplicitConnect()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Communication.ConnectAsync(system.Device); await system.Communication.DisconnectAsync(system.Device.Id);
        await system.Communication.PollAsync(system.Device, default);
        Assert.Equal(ConnectionStatus.Disconnected, system.Communication.GetSnapshot(system.Device.Id).Status);
        Assert.True((await system.Communication.TestAsync(system.Device)).Success);
        Assert.Equal(ConnectionStatus.Disconnected, system.Communication.GetSnapshot(system.Device.Id).Status);
        await system.Communication.ConnectAsync(system.Device);
        Assert.Equal(ConnectionStatus.Online, system.Communication.GetSnapshot(system.Device.Id).Status);
    }
    [Fact]
    public async Task ConcurrentTagReadsAndSubscriptionsAreSafeAndNeverPersistRuntimeValue()
    {
        await using var system = await TestSystem.CreateAsync(); await system.Devices.SaveAsync(system.Device);
        var tag = new Tag { Name = "Concurrent", DeviceId = system.Device.Id, Address = 10 };
        await system.Tags.SaveAsync(tag);
        var notifications = 0;
        var subscriptions = Enumerable.Range(0, 8).Select(_ => system.Runtime.SubscribeTag(tag.Id, _ => Interlocked.Increment(ref notifications))).ToArray();
        var reads = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => system.Runtime.GetTagValueAsync(tag.Id)));
        Assert.All(reads, x => { Assert.Equal(TagQuality.Good, x.Quality); Assert.Equal(1234d, x.CurrentValue); });
        Assert.Equal(24 * 8, notifications);
        foreach (var subscription in subscriptions) subscription.Dispose();
        var lastRead = await system.Runtime.GetTagValueAsync(tag.Id); Assert.Equal(24 * 8, notifications);
        await system.Communication.DisconnectAsync(system.Device.Id);
        var stale = await system.Runtime.GetTagValueAsync(tag.Id);
        Assert.Equal(TagQuality.Bad, stale.Quality); Assert.Equal(1234d, stale.CurrentValue); Assert.Equal(lastRead.Timestamp, stale.Timestamp);
        Assert.NotEmpty(await system.Store.GetTagsAsync());
    }

    [Fact]
    public async Task EngineeringRangeMarksOutOfRangeReadBadButKeepsValue()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "Range",
            DeviceId = system.Device.Id,
            Address = 10,
            MinValue = 0,
            MaxValue = 100
        };
        await system.Tags.SaveAsync(tag);

        var outOfRange = await system.Runtime.GetTagValueAsync(tag.Id);
        Assert.Equal(TagQuality.Bad, outOfRange.Quality);
        Assert.Equal(1234d, outOfRange.CurrentValue);
        Assert.Contains("高于最大值", outOfRange.LastError);
        Assert.Empty(await system.Store.GetAlarmsAsync());

        tag.MaxValue = 1234;
        await system.Tags.SaveAsync(tag);
        var atLimit = await system.Runtime.GetTagValueAsync(tag.Id);
        Assert.Equal(TagQuality.Good, atLimit.Quality);
        Assert.Equal(1234d, atLimit.CurrentValue);
        Assert.Null(atLimit.LastError);
    }

    [Fact]
    public async Task BackgroundWorkersIsolateFaultyDeviceAndRecoverWithoutBlockingHealthyDevice()
    {
        await using var system = await TestSystem.CreateAsync(); await system.Devices.SaveAsync(system.Device);
        var bad = system.Device.Copy(); bad.Id = Guid.NewGuid(); bad.Name = "Faulted"; bad.UnitId = 2;
        await system.Devices.SaveAsync(bad);
        var tag = new Tag { Name = "Live", DeviceId = system.Device.Id, Address = 10, ScanInterval = 250 };
        await system.Tags.SaveAsync(tag);
        using var polling = new ModbusPollingService(system.Store, system.Communication,
            system.Services.GetRequiredService<TagRuntime>(), NullLogger<ModbusPollingService>.Instance);
        await polling.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => system.Runtime.GetTagSnapshot(tag.Id).Quality == TagQuality.Good && system.Communication.GetSnapshot(bad.Id).Status == ConnectionStatus.Faulted);
            Assert.Equal(ConnectionStatus.Online, system.Communication.GetSnapshot(system.Device.Id).Status);
            await system.Simulator.StopAsync();
            await WaitUntilAsync(() => system.Communication.GetSnapshot(system.Device.Id).Status == ConnectionStatus.Faulted);
            await system.Simulator.StartAsync();
            await WaitUntilAsync(() => system.Communication.GetSnapshot(system.Device.Id).Status == ConnectionStatus.Online);
        }
        finally { await polling.StopAsync(default); }
        Assert.Equal(ConnectionStatus.Disconnected, system.Communication.GetSnapshot(system.Device.Id).Status);
    }
    [Fact]
    public async Task CancelledRequestDoesNotExecuteOrReconnect()
    {
        await using var system = await TestSystem.CreateAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => system.Communication.ReadAsync(system.Device, AddressType.HoldingRegister, 10, 1, cancellation.Token));
        Assert.Equal(ConnectionStatus.Disconnected, system.Communication.GetSnapshot(system.Device.Id).Status);
    }
    [Fact]
    public async Task AuditWriteFailureDoesNotFaultDeviceCommunicationOrLoseReadResult()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var communication = new DeviceCommunication(
            new IoTClientConnectionFactory(), new AuditWriteFailingStore(system.Store),
            NullLogger<DeviceCommunication>.Instance);

        // 首次读取会触发"通信恢复"记账写入（连接日志、报警解除）；注入的写入失败不应影响读取结果与设备状态。
        var bytes = await communication.ReadAsync(
            system.Device, AddressType.HoldingRegister, (ushort)system.Device.HealthCheckAddress, 1,
            CancellationToken.None);
        Assert.Equal((ushort)1234, BinaryPrimitives.ReadUInt16BigEndian(bytes));
        Assert.Equal(ConnectionStatus.Online, communication.GetSnapshot(system.Device.Id).Status);
        Assert.Null(communication.GetSnapshot(system.Device.Id).LastError);

        // 通信真实失败时，报警与故障日志写入同样失败，也不能掩盖原始通信错误。
        var bad = system.Device.Copy();
        bad.UnitId = 2;
        var failure = await Assert.ThrowsAsync<IOException>(() => communication.ReadAsync(
            bad, AddressType.HoldingRegister, (ushort)bad.HealthCheckAddress, 1, CancellationToken.None));
        Assert.DoesNotContain("测试注入", failure.Message);
        Assert.Equal(ConnectionStatus.Faulted, communication.GetSnapshot(bad.Id).Status);
    }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        // 全量并行测试时轮询恢复受 CPU/TCP 负载影响，放宽等待上限消除负载敏感；断言仍要求最终达到预期状态。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition()) await Task.Delay(40, timeout.Token);
    }

    // 仅用于验证审计写入失败时的通信行为：日志与报警写入注入失败，其余全部转发真实存储。
    private sealed class AuditWriteFailingStore(IScadaStore inner) : IScadaStore
    {
        public Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken ct = default) => inner.GetDevicesAsync(ct);
        public Task<Device?> GetDeviceAsync(Guid id, CancellationToken ct = default) => inner.GetDeviceAsync(id, ct);
        public Task SaveDeviceAsync(Device device, CancellationToken ct = default) => inner.SaveDeviceAsync(device, ct);
        public Task DeleteDeviceAsync(Guid id, CancellationToken ct = default) => inner.DeleteDeviceAsync(id, ct);
        public Task<IReadOnlyList<Tag>> GetTagsAsync(CancellationToken ct = default) => inner.GetTagsAsync(ct);
        public Task SaveTagAsync(Tag tag, CancellationToken ct = default) => inner.SaveTagAsync(tag, ct);
        public Task DeleteTagAsync(Guid id, CancellationToken ct = default) => inner.DeleteTagAsync(id, ct);
        public Task<HmiPage> EnsureHmiPageAsync(HmiPage page, IReadOnlyList<HmiControl> initialControls, CancellationToken ct = default)
            => inner.EnsureHmiPageAsync(page, initialControls, ct);
        public Task<HmiPage?> GetHmiPageAsync(string name, CancellationToken ct = default) => inner.GetHmiPageAsync(name, ct);
        public Task<IReadOnlyList<HmiControl>> GetHmiControlsAsync(Guid pageId, CancellationToken ct = default)
            => inner.GetHmiControlsAsync(pageId, ct);
        public Task<Guid> SaveHmiControlAsync(HmiControl control, CancellationToken ct = default) => inner.SaveHmiControlAsync(control, ct);
        public Task DeleteHmiControlAsync(Guid pageId, Guid controlId, CancellationToken ct = default)
            => inner.DeleteHmiControlAsync(pageId, controlId, ct);
        public Task<IReadOnlyList<Recipe>> GetRecipesAsync(CancellationToken ct = default) => inner.GetRecipesAsync(ct);
        public Task<Recipe?> GetRecipeAsync(Guid id, CancellationToken ct = default) => inner.GetRecipeAsync(id, ct);
        public Task<IReadOnlyList<string>> GetRecipeNamesAsync(CancellationToken ct = default) => inner.GetRecipeNamesAsync(ct);
        public Task<bool> RecipeNameExistsAsync(string name, Guid? exceptId = null, CancellationToken ct = default)
            => inner.RecipeNameExistsAsync(name, exceptId, ct);
        public Task<RecipeVersionPage> GetRecipeVersionsPageAsync(Guid recipeId, int pageIndex, int pageSize, CancellationToken ct = default)
            => inner.GetRecipeVersionsPageAsync(recipeId, pageIndex, pageSize, ct);
        public Task<RecipeVersion?> GetRecipeVersionAsync(Guid recipeId, int revision, CancellationToken ct = default)
            => inner.GetRecipeVersionAsync(recipeId, revision, ct);
        public Task<RecipeApplyRecordPage> GetRecipeApplyRecordsAsync(Guid recipeId, int pageIndex, int pageSize, CancellationToken ct = default)
            => inner.GetRecipeApplyRecordsAsync(recipeId, pageIndex, pageSize, ct);
        public Task<Guid> SaveRecipeAsync(Recipe recipe, IReadOnlyList<RecipeParameter> parameters, CancellationToken ct = default)
            => inner.SaveRecipeAsync(recipe, parameters, ct);
        public Task DeleteRecipeAsync(Guid recipeId, CancellationToken ct = default) => inner.DeleteRecipeAsync(recipeId, ct);
        public Task RecordRecipeApplyAsync(Guid recipeId, int appliedRevision, bool succeeded, string summary, CancellationToken ct = default)
            => inner.RecordRecipeApplyAsync(recipeId, appliedRevision, succeeded, summary, ct);
        public Task<IReadOnlyList<ManualOutputPoint>> GetManualOutputPointsAsync(Guid deviceId, CancellationToken ct = default)
            => inner.GetManualOutputPointsAsync(deviceId, ct);
        public Task CreateManualOutputPointAsync(ManualOutputPoint point, CancellationToken ct = default)
            => inner.CreateManualOutputPointAsync(point, ct);
        public Task UpdateManualOutputPointAsync(Guid deviceId, Guid pointId, string name, CancellationToken ct = default)
            => inner.UpdateManualOutputPointAsync(deviceId, pointId, name, ct);
        public Task UpdateManualOutputPointOrderAsync(Guid deviceId, IReadOnlyList<Guid> orderedPointIds, CancellationToken ct = default)
            => inner.UpdateManualOutputPointOrderAsync(deviceId, orderedPointIds, ct);
        public Task<Guid> SaveManualOutputPointConfigurationAsync(
            Guid deviceId, Guid? pointId, string name, Guid? tagId, CancellationToken ct = default,
            Guid? monitorTagId = null, ManualOutputMode outputMode = ManualOutputMode.Hold, int pulseDurationMs = 300)
            => inner.SaveManualOutputPointConfigurationAsync(deviceId, pointId, name, tagId, ct, monitorTagId, outputMode, pulseDurationMs);
        public Task DeleteManualOutputPointAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
            => inner.DeleteManualOutputPointAsync(deviceId, pointId, ct);
        public Task<IReadOnlyList<ManualOutputBinding>> GetManualOutputBindingsAsync(Guid deviceId, CancellationToken ct = default)
            => inner.GetManualOutputBindingsAsync(deviceId, ct);
        public Task SaveManualOutputBindingsAsync(Guid deviceId, IReadOnlyList<ManualOutputBinding> bindings, CancellationToken ct = default)
            => inner.SaveManualOutputBindingsAsync(deviceId, bindings, ct);
        public Task SaveManualOutputBindingAsync(Guid deviceId, ManualOutputBinding binding, CancellationToken ct = default)
            => inner.SaveManualOutputBindingAsync(deviceId, binding, ct);
        public Task<IReadOnlyList<OperationLog>> GetLogsAsync(CancellationToken ct = default) => inner.GetLogsAsync(ct);
        public Task<LogQueryResult> QueryLogsAsync(LogQuery query, CancellationToken ct = default)
            => inner.QueryLogsAsync(query, ct);
        public Task<IReadOnlyList<OperationLog>> QueryLogsForExportAsync(LogQuery query, int cap = 5000, CancellationToken ct = default)
            => inner.QueryLogsForExportAsync(query, cap, ct);
        public Task<IReadOnlyList<string>> GetLogSourcesAsync(CancellationToken ct = default)
            => inner.GetLogSourcesAsync(ct);
        public Task<RetentionCleanupResult> CleanupHistoryAsync(DataRetentionOptions options, CancellationToken ct = default)
            => inner.CleanupHistoryAsync(options, ct);
        public Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken ct = default)
            => inner.GetSettingsAsync(ct);
        public Task SaveSettingsAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
            => inner.SaveSettingsAsync(values, ct);
        public Task<DataRetentionOptions> GetRetentionOptionsAsync(DataRetentionOptions fallback, CancellationToken ct = default)
            => inner.GetRetentionOptionsAsync(fallback, ct);
        public Task SaveRetentionOptionsAsync(DataRetentionOptions options, CancellationToken ct = default)
            => inner.SaveRetentionOptionsAsync(options, ct);
        public Task LogAsync(string source, string message, string level = "Information", CancellationToken ct = default)
            => throw new InvalidOperationException("测试注入：审计日志写入失败。");
        public Task<IReadOnlyList<AlarmRecord>> GetAlarmsAsync(CancellationToken ct = default) => inner.GetAlarmsAsync(ct);
        public Task SetCommunicationAlarmAsync(Guid deviceId, string name, string? error, CancellationToken ct = default)
            => throw new InvalidOperationException("测试注入：报警写入失败。");
        public Task SetTagAlarmAsync(Guid tagId, string tagName, string? error, string? condition = null, CancellationToken ct = default)
            => throw new InvalidOperationException("测试注入：Tag 报警写入失败。");
        public Task SetTagQualityAlarmAsync(Guid tagId, string tagName, string? error, CancellationToken ct = default)
            => throw new InvalidOperationException("测试注入：Tag 质量报警写入失败。");
        public Task<IReadOnlyList<AlarmDefinition>> GetAlarmDefinitionsAsync(CancellationToken ct = default)
            => inner.GetAlarmDefinitionsAsync(ct);
        public Task SaveAlarmDefinitionAsync(AlarmDefinition definition, CancellationToken ct = default)
            => inner.SaveAlarmDefinitionAsync(definition, ct);
        public Task DeleteAlarmDefinitionAsync(Guid id, CancellationToken ct = default)
            => inner.DeleteAlarmDefinitionAsync(id, ct);
        public Task SetAlarmDefinitionAlarmAsync(Guid definitionId, string? message, string severity = "Warning", CancellationToken ct = default)
            => throw new InvalidOperationException("测试注入：报警定义写入失败。");
        public Task AcknowledgeAlarmAsync(Guid id, string? note = null, CancellationToken ct = default)
            => inner.AcknowledgeAlarmAsync(id, note, ct);
        public Task ShelveAlarmAsync(Guid id, int minutes, string reason, CancellationToken ct = default)
            => inner.ShelveAlarmAsync(id, minutes, reason, ct);
        public Task UnShelveAlarmAsync(Guid id, CancellationToken ct = default)
            => inner.UnShelveAlarmAsync(id, ct);
        public Task<AlarmStatistics> GetAlarmStatisticsAsync(DateTimeOffset from, CancellationToken ct = default)
            => inner.GetAlarmStatisticsAsync(from, ct);
        public Task<IReadOnlyList<AlarmSuppression>> GetSuppressionsAsync(CancellationToken ct = default)
            => inner.GetSuppressionsAsync(ct);
        public Task AddSuppressionAsync(Guid deviceId, int minutes, string reason, CancellationToken ct = default)
            => inner.AddSuppressionAsync(deviceId, minutes, reason, ct);
        public Task RemoveSuppressionAsync(Guid id, CancellationToken ct = default)
            => inner.RemoveSuppressionAsync(id, ct);
        public Task<AlarmBadgeSummary> GetAlarmBadgeSummaryAsync(CancellationToken ct = default)
            => inner.GetAlarmBadgeSummaryAsync(ct);
        public Task<int> GetTagScanIntervalAsync(int fallbackMilliseconds, CancellationToken ct = default)
            => inner.GetTagScanIntervalAsync(fallbackMilliseconds, ct);
        public Task SaveTagScanIntervalAsync(int milliseconds, CancellationToken ct = default)
            => inner.SaveTagScanIntervalAsync(milliseconds, ct);
        public Task<int> GetQualityAlarmFailuresAsync(int fallback, CancellationToken ct = default)
            => inner.GetQualityAlarmFailuresAsync(fallback, ct);
        public Task SaveQualityAlarmFailuresAsync(int failures, CancellationToken ct = default)
            => inner.SaveQualityAlarmFailuresAsync(failures, ct);
        public Task<Guid?> GetRunningStateTagIdAsync(CancellationToken ct = default)
            => inner.GetRunningStateTagIdAsync(ct);
        public Task<Guid?> GetSnTagIdAsync(CancellationToken ct = default)
            => inner.GetSnTagIdAsync(ct);
        public Task SetSnTagIdAsync(Guid? tagId, CancellationToken ct = default)
            => inner.SetSnTagIdAsync(tagId, ct);
        public Task<SnRecord> AddSnRecordAsync(string sn, string source = "PLC", string? deviceName = null,
            string? recipeName = null, int? recipeVersion = null, string? note = null,
            CancellationToken ct = default)
            => inner.AddSnRecordAsync(sn, source, deviceName, recipeName, recipeVersion, note, ct);
        public Task<SnRecord?> GetLatestSnRecordBySnAsync(string sn, CancellationToken ct = default)
            => inner.GetLatestSnRecordBySnAsync(sn, ct);
        public Task<IReadOnlyList<SnapshotRecord>> GetSnapshotsBySnRecordAsync(Guid snRecordId, CancellationToken ct = default)
            => inner.GetSnapshotsBySnRecordAsync(snRecordId, ct);
        public Task<IReadOnlyDictionary<Guid, int>> GetSnapshotCountsAsync(
            IReadOnlyCollection<Guid> snRecordIds, CancellationToken ct = default)
            => inner.GetSnapshotCountsAsync(snRecordIds, ct);
        public Task<(string? Name, int? Version)> GetActiveRecipeAsync(CancellationToken ct = default)
            => inner.GetActiveRecipeAsync(ct);
        public Task<SnRecordPage> QuerySnRecordsAsync(string? search, int page, int pageSize, CancellationToken ct = default)
            => inner.QuerySnRecordsAsync(search, page, pageSize, ct);
        public Task<IReadOnlyList<SnRecord>> QuerySnRecordsForExportAsync(string? search, int cap = 10000, CancellationToken ct = default)
            => inner.QuerySnRecordsForExportAsync(search, cap, ct);
        public Task<IReadOnlyList<SnapshotRule>> GetSnapshotRulesAsync(CancellationToken ct = default)
            => inner.GetSnapshotRulesAsync(ct);
        public Task SaveSnapshotRuleAsync(SnapshotRule rule, IReadOnlyList<SnapshotPointInput> points, CancellationToken ct = default)
            => inner.SaveSnapshotRuleAsync(rule, points, ct);
        public Task DeleteSnapshotRuleAsync(Guid id, CancellationToken ct = default)
            => inner.DeleteSnapshotRuleAsync(id, ct);
        public Task SaveSingleSnapshotRuleAsync(SnapshotRule rule, IReadOnlyList<SnapshotPointInput> points, CancellationToken ct = default)
            => inner.SaveSingleSnapshotRuleAsync(rule, points, ct);
        public Task AddSnapshotRecordAsync(SnapshotRecord record, CancellationToken ct = default)
            => inner.AddSnapshotRecordAsync(record, ct);
        public Task<SnapshotRecordPage> QuerySnapshotRecordsAsync(
            int page, int pageSize, string? search = null, DateTimeOffset? from = null,
            CancellationToken ct = default)
            => inner.QuerySnapshotRecordsAsync(page, pageSize, search, from, ct);
        public Task<IReadOnlyList<SnapshotRecord>> QuerySnapshotRecordsForExportAsync(
            string? search = null, DateTimeOffset? from = null, int cap = 20000,
            CancellationToken ct = default)
            => inner.QuerySnapshotRecordsForExportAsync(search, from, cap, ct);
        public Task<(int Total, DateTimeOffset? First, DateTimeOffset? Last)> GetSnStatsAsync(
            string sn, CancellationToken ct = default)
            => inner.GetSnStatsAsync(sn, ct);
        public Task<IReadOnlyDictionary<string, int>> GetSnCountsAsync(
            IReadOnlyCollection<string> sns, CancellationToken ct = default)
            => inner.GetSnCountsAsync(sns, ct);
        public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetSnapshotLastTimesAsync(
            IReadOnlyCollection<Guid> ruleIds, CancellationToken ct = default)
            => inner.GetSnapshotLastTimesAsync(ruleIds, ct);
        public Task SetRunningStateTagIdAsync(Guid? tagId, CancellationToken ct = default)
            => inner.SetRunningStateTagIdAsync(tagId, ct);
        public Task<AlarmQueryResult> QueryAlarmsAsync(AlarmQuery query, CancellationToken ct = default)
            => inner.QueryAlarmsAsync(query, ct);
        public Task<IReadOnlyList<AlarmRecord>> QueryAlarmsForExportAsync(AlarmQuery query, int cap = 5000, CancellationToken ct = default)
            => inner.QueryAlarmsForExportAsync(query, cap, ct);
    }
}
