namespace PLCScada.Core;

public interface IScadaStore
{
    Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken ct = default);
    Task<Device?> GetDeviceAsync(Guid id, CancellationToken ct = default);
    Task SaveDeviceAsync(Device device, CancellationToken ct = default);
    Task DeleteDeviceAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Tag>> GetTagsAsync(CancellationToken ct = default);
    Task SaveTagAsync(Tag tag, CancellationToken ct = default);
    Task DeleteTagAsync(Guid id, CancellationToken ct = default);
    Task<HmiPage> EnsureHmiPageAsync(HmiPage page, IReadOnlyList<HmiControl> initialControls, CancellationToken ct = default);
    Task<HmiPage?> GetHmiPageAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<HmiControl>> GetHmiControlsAsync(Guid pageId, CancellationToken ct = default);
    Task<Guid> SaveHmiControlAsync(HmiControl control, CancellationToken ct = default);
    Task DeleteHmiControlAsync(Guid pageId, Guid controlId, CancellationToken ct = default);
    Task<IReadOnlyList<Recipe>> GetRecipesAsync(CancellationToken ct = default);
    Task<Recipe?> GetRecipeAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetRecipeNamesAsync(CancellationToken ct = default);
    Task<bool> RecipeNameExistsAsync(string name, Guid? exceptId = null, CancellationToken ct = default);
    Task<RecipeVersionPage> GetRecipeVersionsPageAsync(
        Guid recipeId, int pageIndex, int pageSize, CancellationToken ct = default);
    Task<RecipeVersion?> GetRecipeVersionAsync(Guid recipeId, int revision, CancellationToken ct = default);
    Task<RecipeApplyRecordPage> GetRecipeApplyRecordsAsync(
        Guid recipeId, int pageIndex, int pageSize, CancellationToken ct = default);
    Task<Guid> SaveRecipeAsync(Recipe recipe, IReadOnlyList<RecipeParameter> parameters, CancellationToken ct = default);
    Task DeleteRecipeAsync(Guid recipeId, CancellationToken ct = default);
    Task RecordRecipeApplyAsync(
        Guid recipeId, int appliedRevision, bool succeeded, string summary, CancellationToken ct = default);
    Task<IReadOnlyList<ManualOutputPoint>> GetManualOutputPointsAsync(Guid deviceId, CancellationToken ct = default);
    Task CreateManualOutputPointAsync(ManualOutputPoint point, CancellationToken ct = default);
    Task UpdateManualOutputPointAsync(Guid deviceId, Guid pointId, string name, CancellationToken ct = default);
    Task UpdateManualOutputPointOrderAsync(
        Guid deviceId, IReadOnlyList<Guid> orderedPointIds, CancellationToken ct = default);
    Task<Guid> SaveManualOutputPointConfigurationAsync(
        Guid deviceId, Guid? pointId, string name, Guid? tagId, CancellationToken ct = default,
        Guid? monitorTagId = null, ManualOutputMode outputMode = ManualOutputMode.Hold, int pulseDurationMs = 300);
    Task DeleteManualOutputPointAsync(Guid deviceId, Guid pointId, CancellationToken ct = default);
    Task<IReadOnlyList<ManualOutputBinding>> GetManualOutputBindingsAsync(Guid deviceId, CancellationToken ct = default);
    Task SaveManualOutputBindingsAsync(Guid deviceId, IReadOnlyList<ManualOutputBinding> bindings, CancellationToken ct = default);
    Task SaveManualOutputBindingAsync(Guid deviceId, ManualOutputBinding binding, CancellationToken ct = default);
    Task<IReadOnlyList<OperationLog>> GetLogsAsync(CancellationToken ct = default);
    Task<LogQueryResult> QueryLogsAsync(LogQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<OperationLog>> QueryLogsForExportAsync(LogQuery query, int cap = 5000, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetLogSourcesAsync(CancellationToken ct = default);
    Task LogAsync(string source, string message, string level = "Information", CancellationToken ct = default);
    Task<RetentionCleanupResult> CleanupHistoryAsync(DataRetentionOptions options, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default);
    Task<DataRetentionOptions> GetRetentionOptionsAsync(DataRetentionOptions fallback, CancellationToken ct = default);
    Task SaveRetentionOptionsAsync(DataRetentionOptions options, CancellationToken ct = default);
    Task<IReadOnlyList<AlarmRecord>> GetAlarmsAsync(CancellationToken ct = default);
    Task SetCommunicationAlarmAsync(Guid deviceId, string name, string? error, CancellationToken ct = default);
    Task SetTagAlarmAsync(Guid tagId, string tagName, string? error, string? condition = null, CancellationToken ct = default);
    Task SetTagQualityAlarmAsync(Guid tagId, string tagName, string? error, CancellationToken ct = default);
    Task<IReadOnlyList<AlarmDefinition>> GetAlarmDefinitionsAsync(CancellationToken ct = default);
    Task SaveAlarmDefinitionAsync(AlarmDefinition definition, CancellationToken ct = default);
    Task DeleteAlarmDefinitionAsync(Guid id, CancellationToken ct = default);
    Task SetAlarmDefinitionAlarmAsync(
        Guid definitionId, string? message, string severity = "Warning", CancellationToken ct = default);
    Task AcknowledgeAlarmAsync(Guid id, string? note = null, CancellationToken ct = default);
    Task ShelveAlarmAsync(Guid id, int minutes, string reason, CancellationToken ct = default);
    Task UnShelveAlarmAsync(Guid id, CancellationToken ct = default);
    Task<AlarmStatistics> GetAlarmStatisticsAsync(DateTimeOffset from, CancellationToken ct = default);
    Task<int> GetTagScanIntervalAsync(int fallbackMilliseconds, CancellationToken ct = default);
    Task SaveTagScanIntervalAsync(int milliseconds, CancellationToken ct = default);
    Task<int> GetQualityAlarmFailuresAsync(int fallback, CancellationToken ct = default);
    Task SaveQualityAlarmFailuresAsync(int failures, CancellationToken ct = default);
    Task<Guid?> GetRunningStateTagIdAsync(CancellationToken ct = default);
    Task SetRunningStateTagIdAsync(Guid? tagId, CancellationToken ct = default);
    Task<Guid?> GetSnTagIdAsync(CancellationToken ct = default);
    Task SetSnTagIdAsync(Guid? tagId, CancellationToken ct = default);
    Task<SnRecord> AddSnRecordAsync(
        string sn, string source = "PLC", string? deviceName = null,
        string? recipeName = null, int? recipeVersion = null, string? note = null,
        CancellationToken ct = default);
    Task<SnRecord?> GetLatestSnRecordBySnAsync(string sn, CancellationToken ct = default);
    Task<IReadOnlyList<SnapshotRecord>> GetSnapshotsBySnRecordAsync(Guid snRecordId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, int>> GetSnapshotCountsAsync(
        IReadOnlyCollection<Guid> snRecordIds, CancellationToken ct = default);
    Task<(string? Name, int? Version)> GetActiveRecipeAsync(CancellationToken ct = default);
    Task<SnRecordPage> QuerySnRecordsAsync(string? search, int page, int pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<SnRecord>> QuerySnRecordsForExportAsync(
        string? search, int cap = 10000, CancellationToken ct = default);
    Task<IReadOnlyList<SnapshotRule>> GetSnapshotRulesAsync(CancellationToken ct = default);
    Task SaveSnapshotRuleAsync(SnapshotRule rule, IReadOnlyList<SnapshotPointInput> points, CancellationToken ct = default);
    Task DeleteSnapshotRuleAsync(Guid id, CancellationToken ct = default);
    Task SaveSingleSnapshotRuleAsync(SnapshotRule rule, IReadOnlyList<SnapshotPointInput> points, CancellationToken ct = default);
    Task AddSnapshotRecordAsync(SnapshotRecord record, CancellationToken ct = default);
    Task<SnapshotRecordPage> QuerySnapshotRecordsAsync(
        int page, int pageSize, string? search = null, DateTimeOffset? from = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<SnapshotRecord>> QuerySnapshotRecordsForExportAsync(
        string? search = null, DateTimeOffset? from = null, int cap = 20000,
        CancellationToken ct = default);
    Task<(int Total, DateTimeOffset? First, DateTimeOffset? Last)> GetSnStatsAsync(
        string sn, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> GetSnCountsAsync(
        IReadOnlyCollection<string> sns, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetSnapshotLastTimesAsync(
        IReadOnlyCollection<Guid> ruleIds, CancellationToken ct = default);
    Task<IReadOnlyList<AlarmSuppression>> GetSuppressionsAsync(CancellationToken ct = default);
    Task AddSuppressionAsync(Guid deviceId, int minutes, string reason, CancellationToken ct = default);
    Task RemoveSuppressionAsync(Guid id, CancellationToken ct = default);
    Task<AlarmBadgeSummary> GetAlarmBadgeSummaryAsync(CancellationToken ct = default);
    Task<AlarmQueryResult> QueryAlarmsAsync(AlarmQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<AlarmRecord>> QueryAlarmsForExportAsync(
        AlarmQuery query, int cap = 5000, CancellationToken ct = default);
}

public interface IRecipeApplyLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(
        string resourceKey, TimeSpan waitTimeout, CancellationToken ct = default);
}

// Owns the IoTClient connection; UI must use DeviceService or ITagRuntime instead.
public interface IModbusConnection : IAsyncDisposable
{
    Task OpenAsync(CancellationToken ct);
    Task<byte[]> ReadAsync(byte unitId, AddressType type, ushort address, ushort count, CancellationToken ct);
    Task WriteCoilAsync(byte unitId, ushort address, bool value, CancellationToken ct);
    Task WriteCoilsAsync(byte unitId, ushort address, bool[] values, CancellationToken ct);
    Task WriteSingleRegisterAsync(byte unitId, ushort address, ushort value, CancellationToken ct);
    Task WriteRegistersAsync(byte unitId, ushort address, byte[] networkBytes, CancellationToken ct);
}
public interface IModbusConnectionFactory { IModbusConnection Create(Device device); }
public interface IDeviceCommunication
{
    DeviceSnapshot GetSnapshot(Guid id);
    IReadOnlyList<DeviceSnapshot> GetSnapshots();
    Task<CommunicationTestResult> TestAsync(Device device, CancellationToken ct = default);
    Task ConnectAsync(Device device, CancellationToken ct = default);
    Task DisconnectAsync(Guid id, CancellationToken ct = default);
    Task InvalidateAsync(Guid id, CancellationToken ct = default);
    Task PollAsync(Device device, CancellationToken ct);
    Task<byte[]> ReadAsync(Device device, AddressType type, ushort address, ushort count, CancellationToken ct);
    Task WriteCoilAsync(Device device, ushort address, bool value, CancellationToken ct = default);
    Task WriteSingleRegisterAsync(Device device, ushort address, ushort value, CancellationToken ct = default);
    Task WriteRegistersAsync(Device device, ushort address, byte[] networkBytes, CancellationToken ct = default);
}
public interface ITagRuntime
{
    TagSnapshot GetTagSnapshot(Guid id);
    IReadOnlyList<TagSnapshot> GetSnapshots();
    Task<TagSnapshot> GetTagValueAsync(Guid id, CancellationToken ct = default);
    IDisposable SubscribeTag(Guid id, Action<TagSnapshot> listener);
}
public interface ISimulatorControl
{
    bool Available { get; }
    bool Running { get; }
    int Port { get; }
    string? LastError { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
