using System.ComponentModel.DataAnnotations;

namespace PLCScada.Core;

// 系统级监控约定的固定值（Tag 名称 / 默认地址等）。
public static class MonitoringDefaults
{
    // 未配置监控 Tag 时的候选名称（历史自动创建的 Tag，可作为默认选择）。
    public const string RunningStateTagName = "运行状态监控";
    // SN 追溯默认候选 Tag 名称（未显式配置来源时使用）。
    public const string SnTagName = "扫码内容";
}

public enum ConnectionStatus { Disconnected, Connecting, Online, Faulted, Disabled }
public enum AddressType { Coil, DiscreteInput, HoldingRegister, InputRegister }
public enum TagDataType { Bool, Int16, UInt16, Int32, UInt32, Float32, Float64, String }
public enum AccessMode { ReadOnly, ReadWrite }
public enum TagQuality { Good, Bad, Uncertain, Stale }
public enum ByteOrder { BigEndian, LittleEndian }
public enum StringEncoding { Ascii, Utf16LE }
public enum WordOrder { HighWordFirst, LowWordFirst }
public enum AlarmStatus { Active, Acknowledged, Cleared, ClearedUnacknowledged }
public enum AlarmPriority { P1 = 1, P2 = 2, P3 = 3, P4 = 4 }
public enum HmiDisplayType { Status, TotalResult, Result, Measurement }
public enum ManualOutputMode { Hold, Pulse }
public enum AlarmDefinitionKind { Bit, Enum }

// 报警定义：对采集值做条件判定的规则（位报警用于 M 点，枚举报警用于故障码/状态字）。
public sealed class AlarmDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TagId { get; set; }
    public Tag? Tag { get; set; }
    [Required, StringLength(80)] public string Name { get; set; } = "";
    public AlarmDefinitionKind Kind { get; set; } = AlarmDefinitionKind.Bit;
    // Kind=Bit：Bool 等于该值时触发。
    public bool BitTarget { get; set; } = true;
    // Kind=Enum：命中的数值集合（逗号分隔，如 "5,6,7"）。
    [StringLength(200)] public string EnumValues { get; set; } = "";
    [StringLength(20)] public string Severity { get; set; } = "Warning";
    // 消息模板，支持 {tagName} 与 {value} 占位符。
    [Required, StringLength(200)] public string Message { get; set; } = "{tagName}：{value}";
    // 条件持续达到该秒数才触发（去抖）；0 = 立即。
    [Range(0, 3600)] public int DelaySeconds { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class NonEmptyGuidAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value is Guid id && id != Guid.Empty;
}

// Configuration only. Connection state lives in DeviceSnapshot, never in this table.
public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [StringLength(500)] public string Description { get; set; } = "";
    public string Protocol { get; set; } = "Modbus TCP";
    [Required, StringLength(45)] public string IpAddress { get; set; } = "127.0.0.1";
    [Range(1, 65535)] public int Port { get; set; } = 502;
    [Range(1, 247)] public int UnitId { get; set; } = 1;
    [Range(250, 60000)] public int PollInterval { get; set; } = 1000;
    [Range(200, 10000)] public int ConnectionTimeout { get; set; } = 1500;
    [Range(0, 65535)] public int HealthCheckAddress { get; set; }
    public bool Enabled { get; set; } = true;
    public Device Copy() => (Device)MemberwiseClone();
}

public sealed class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [StringLength(500)] public string Description { get; set; } = "";
    [NonEmptyGuid(ErrorMessage = "请选择关联设备。")]
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }
    public AddressType AddressType { get; set; } = AddressType.HoldingRegister;
    [Range(0, 65535)] public int Address { get; set; }
    public TagDataType DataType { get; set; } = TagDataType.UInt16;
    // String 类型：占用寄存器数与编码（ASCII / UTF-16LE）。
    [Range(1, 125)] public int StringRegisters { get; set; } = 20;
    public StringEncoding StringEncoding { get; set; } = StringEncoding.Ascii;
    public AccessMode AccessMode { get; set; }
    [Range(0, 60000)] public int ScanInterval { get; set; } = 1000;
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    [StringLength(30)] public string Unit { get; set; } = "";
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public double? AlarmHigh { get; set; }
    public double? AlarmLow { get; set; }
    public double AlarmDeadband { get; set; }
    // 连续读取失败达到该次数时生成质量报警（读取恢复自动解除）；0 = 关闭（默认）。
    [Range(0, 1000)] public int QualityAlarmAfterFailures { get; set; }
    public string? DefaultValue { get; set; }
    public bool Enabled { get; set; } = true;
    public ByteOrder ByteOrder { get; set; }
    public WordOrder WordOrder { get; set; }
    public Tag Copy() { var copy = (Tag)MemberwiseClone(); copy.Device = null; return copy; }
}

public sealed class ManualOutputPoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [Range(0, int.MaxValue)] public int SortOrder { get; set; }
    [StringLength(16)] public string? DefaultPlcAddress { get; set; }
    [Range(0, 65535)] public int? DefaultModbusAddress { get; set; }
    public ManualOutputMode OutputMode { get; set; } = ManualOutputMode.Hold;
    [Range(50, 10000)] public int PulseDurationMs { get; set; } = 300;
}

public sealed class ManualOutputBinding
{
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }
    public Guid OutputPointId { get; set; }
    public ManualOutputPoint? OutputPoint { get; set; }
    public Guid? TagId { get; set; }
    public Tag? Tag { get; set; }
    public Guid? MonitorTagId { get; set; }
    public Tag? MonitorTag { get; set; }
}

public sealed record DeviceSnapshot(Guid DeviceId, ConnectionStatus Status, DateTimeOffset? LastConnectedTime = null,
    DateTimeOffset? LastCommunicationTime = null, string? LastError = null, double? LatencyMs = null, long ErrorCount = 0);
public sealed record TagSnapshot(Guid TagId, object? CurrentValue, TagQuality Quality, DateTimeOffset? Timestamp, string? LastError);
public sealed record CommunicationTestResult(bool Success, string Message, ushort? RegisterValue, double ElapsedMs);

public sealed class HmiPage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
}
public sealed class HmiControl
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HmiPageId { get; set; }
    public HmiPage? HmiPage { get; set; }
    public Guid TagId { get; set; }
    public Tag? Tag { get; set; }
    public string Name { get; set; } = "";
    public string ControlType { get; set; } = "TextDisplay";
    public int Position { get; set; }
    public string ConfigurationJson { get; set; } = "{}";
}

public sealed class Recipe
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? DeviceId { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [StringLength(500)] public string Description { get; set; } = "";
    [StringLength(200)] public string ChangeNote { get; set; } = "";
    public int Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAppliedAt { get; set; }
    public List<RecipeParameter> Parameters { get; set; } = [];
}

public sealed class RecipeParameter
{
    public Guid RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    public Guid TagId { get; set; }
    public Tag? Tag { get; set; }
    [Range(0, int.MaxValue)] public int SortOrder { get; set; }
    public double? EngineeringValue { get; set; }
    public bool? BooleanValue { get; set; }
}

public sealed class RecipeVersion
{
    public Guid RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    [StringLength(200)] public string ChangeNote { get; set; } = "";
    public Guid? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? DeviceProtocol { get; set; }
    public string? DeviceIpAddress { get; set; }
    public int? DevicePort { get; set; }
    public int? DeviceUnitId { get; set; }
    public int SnapshotSchemaVersion { get; set; } = 1;
    public string ParametersJson { get; set; } = "[]";
}

public sealed record RecipeVersionPage(
    int CurrentRevision,
    int PageIndex,
    int TotalCount,
    IReadOnlyList<RecipeVersion> Versions);

// Append-only audit of recipe apply attempts. Intentionally has no foreign key:
// deleting a recipe must not destroy its download history.
public sealed class RecipeApplyRecord
{
    public long Id { get; set; }
    public Guid RecipeId { get; set; }
    [StringLength(100)] public string RecipeName { get; set; } = "";
    public Guid? DeviceId { get; set; }
    public int Revision { get; set; }
    public bool Succeeded { get; set; }
    [StringLength(2000)] public string Summary { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record RecipeApplyRecordPage(
    int PageIndex,
    int PageSize,
    int TotalCount,
    IReadOnlyList<RecipeApplyRecord> Records)
{
    public int PageCount => TotalCount == 0 ? 0 : (TotalCount + PageSize - 1) / PageSize;
}

public sealed record RecipeVersionParameterSnapshot(
    Guid TagId,
    string TagName,
    Guid DeviceId,
    AddressType AddressType,
    int Address,
    TagDataType DataType,
    AccessMode AccessMode,
    bool Enabled,
    string Unit,
    double Scale,
    double Offset,
    ByteOrder ByteOrder,
    WordOrder WordOrder,
    double? MinValue,
    double? MaxValue,
    double? EngineeringValue,
    bool? BooleanValue,
    int SortOrder = 0);

public sealed class AlarmRecord
{
    public Guid AlarmId { get; set; } = Guid.NewGuid();
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public string Severity { get; set; } = "Warning";
    // 优先级（按严重度映射：Error→P1、Warning→P2、Info→P3）。
    public AlarmPriority Priority { get; set; } = AlarmPriority.P2;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
    [StringLength(200)] public string? AcknowledgeNote { get; set; }
    // 搁置（Shelving）：到期前从活动统计中抑制；随报警实例结束而失效。
    public DateTimeOffset? ShelvedUntil { get; set; }
    [StringLength(200)] public string? ShelveReason { get; set; }
    // 60 秒窗口内同源重复触发合并时的累计次数。
    public int RepeatCount { get; set; } = 1;
    public AlarmStatus Status { get; set; }
}
public sealed class OperationLog
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Level { get; set; } = "Information";
    public string Source { get; set; } = "System";
    public string Message { get; set; } = "";
}

// 日志查询：关键词命中来源或内容；级别/来源/时间范围可选；按时间倒序分页。
public sealed record LogQuery(
    string? Search = null,
    IReadOnlyList<string>? Levels = null,
    string? Source = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50);

public sealed record LogQueryResult(
    IReadOnlyList<OperationLog> Items, int TotalCount, int Page, int PageSize);

// 键值设置表：保存可运维调整的运行参数（如数据保留天数），DB 为准、代码默认值兜底。
// SN 追溯记录：每次扫码内容变化（或手动打点）落一条，按 SN/时间检索。
public sealed class SnRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, StringLength(200)] public string Sn { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    [StringLength(40)] public string Source { get; set; } = "PLC";
    // 追溯上下文：设备、当时生效的配方与版本、手动备注。
    [StringLength(80)] public string? DeviceName { get; set; }
    [StringLength(80)] public string? RecipeName { get; set; }
    public int? RecipeVersion { get; set; }
    [StringLength(200)] public string? Note { get; set; }
}

public sealed record SnRecordPage(IReadOnlyList<SnRecord> Items, int TotalCount, int Page, int PageSize);

// 事件触发快照：触发源为 Coil Bool，为 True 时抓取点位 → 保存 → 复位为 False。
public sealed class SnapshotRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, StringLength(80)] public string Name { get; set; } = "";
    public Guid TriggerTagId { get; set; }
    public Tag? TriggerTag { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SnapshotRulePoint> Points { get; set; } = [];
}

public sealed class SnapshotRulePoint
{
    public Guid RuleId { get; set; }
    public SnapshotRule? Rule { get; set; }
    public Guid TagId { get; set; }
    public Tag? Tag { get; set; }
    // 采集口径：别名用于追溯报告列名，单位与小数位决定值如何呈现，必填表示报告必含项。
    [StringLength(40)] public string? Alias { get; set; }
    [StringLength(20)] public string? Unit { get; set; }
    public int Decimals { get; set; } = 2;
    public bool Required { get; set; } = true;
    // 采集项在记录/报告中的展示顺序。
    public int SortOrder { get; set; }
}

// 保存采集项时传入的口径配置（与 SnapshotRulePoint 解耦，供 UI/服务层构造）。
public sealed record SnapshotPointInput(
    Guid TagId, string? Alias = null, string? Unit = null, int Decimals = 2, bool Required = true);

public sealed class SnapshotRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? RuleId { get; set; }
    [StringLength(80)] public string RuleName { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    [StringLength(200)] public string? Sn { get; set; }
    // 绑定的 SN 记录（同一次触发读取的 SN 与点位值）。
    public Guid? SnRecordId { get; set; }
    public string ValuesJson { get; set; } = "[]";
}

public sealed record SnapshotRecordPage(IReadOnlyList<SnapshotRecord> Items, int TotalCount, int Page, int PageSize);

public sealed class ScadaSetting
{
    [Required, StringLength(80)] public string Key { get; set; } = "";
    [StringLength(200)] public string Value { get; set; } = "";
}

// 数据保留策略：0 天 = 永久保留；默认保守（配方下发记录默认永久，属审计台账）。
public sealed class DataRetentionOptions
{
    public int OperationLogRetentionDays { get; set; } = 30;
    // SN 追溯记录保留天数（0 = 永久）。
    public int SnRecordRetentionDays { get; set; } = 90;
    public int ClearedAlarmRetentionDays { get; set; } = 90;
    public int RecipeApplyRecordRetentionDays { get; set; }
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(6);
}

public sealed record RetentionCleanupResult(
    int OperationLogsDeleted, int ClearedAlarmsDeleted, int RecipeApplyRecordsDeleted, int SnRecordsDeleted = 0);

// 报警查询：多条件 + 服务端分页（时间/优先级排序）。
public sealed record AlarmQuery(
    string? Search = null,
    bool ActiveOnly = false,
    IReadOnlyList<AlarmStatus>? Statuses = null,
    IReadOnlyList<AlarmPriority>? Priorities = null,
    string? SourcePrefix = null,
    IReadOnlyList<string>? Sources = null,
    bool ShelvedOnly = false,
    bool StandingOnly = false,
    bool SortByTime = false,
    int Page = 1,
    int PageSize = 50);

public sealed record AlarmQueryResult(
    IReadOnlyList<AlarmRecord> Items, int TotalCount, int Page, int PageSize);

// 全局报警提示（顶部徽标）：未确认（不含搁置）+ P1 未确认 + 已恢复未确认。
public sealed record AlarmBadgeSummary(
    int UnacknowledgedCount, int P1UnacknowledgedCount, int ClearedUnacknowledgedCount);

// 报警抑制（Suppression）：按设备临时拦截"新产生"的报警（与搁置不同：搁置针对已发生的实例）。
public sealed class AlarmSuppression
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }
    [Required, StringLength(200)] public string Reason { get; set; } = "";
    public DateTimeOffset Until { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// 报警 KPI：区间流量（Total/Ack 均值/Top 来源）与当前快照（活动/未确认/搁置/常驻）分离统计。
public sealed record AlarmSourceStat(string Source, int Count);

public sealed record AlarmStatistics(
    DateTimeOffset From,
    int RangeTotalCount,
    int RangeAcknowledgedCount,
    double? AverageAcknowledgeSeconds,
    int ActiveCount,
    int UnacknowledgedCount,
    int ClearedUnacknowledgedCount,
    int ShelvedActiveCount,
    int StandingAlarmCount,
    IReadOnlyList<AlarmSourceStat> TopSources,
    IReadOnlyList<AlarmRecord> StandingAlarms);
