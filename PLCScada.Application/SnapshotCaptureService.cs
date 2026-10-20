using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using PLCScada.Core;

namespace PLCScada.Application;

// 触发快照：触发源为 Coil 上的 Bool；运行中该位为 True 时抓取规则点位并保存，
// 随后把触发位复位为 False（一次握手，避免重复触发）。
public sealed class SnapshotCaptureService(
    IScadaStore store, ITagRuntime runtime, IDeviceCommunication communication)
{
    // 中文点位名保持可读（不转义为 Unicode 编码），便于数据库与导出直接查看。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<bool> TryCaptureAsync(SnapshotRule rule, string? sn, CancellationToken ct = default)
    {
        var trigger = rule.TriggerTag;
        if (trigger is null || trigger.DataType != TagDataType.Bool || trigger.AddressType != AddressType.Coil)
            return false;
        if (runtime.GetTagSnapshot(rule.TriggerTagId).CurrentValue is not true)
            return false;

        var preview = await PreviewAsync(rule, sn, ct);
        await CommitAsync(preview, ct);

        var device = await store.GetDeviceAsync(trigger.DeviceId, ct);
        if (device is not null)
            await communication.WriteCoilAsync(device, (ushort)trigger.Address, false, ct);
        return true;
    }

    // 试抓预览：只读取当前值并套用采集口径，不写库、不复位触发位，供配置阶段核对。
    public async Task<SnapshotPreview> PreviewAsync(SnapshotRule rule, string? sn, CancellationToken ct = default)
    {
        var trimmedSn = string.IsNullOrWhiteSpace(sn) ? null : sn.Trim();
        var device = rule.TriggerTag is null ? null : await store.GetDeviceAsync(rule.TriggerTag.DeviceId, ct);
        return new SnapshotPreview(
            rule.Id, rule.Name, trimmedSn, device?.Name, DateTimeOffset.UtcNow, BuildValues(rule));
    }

    // 把预览内容落库（含 SN 记录绑定）。不触碰 PLC，供“确认写入”使用。
    public async Task<Guid> CommitAsync(SnapshotPreview preview, CancellationToken ct = default)
    {
        // 绑定 SN 记录：同一次触发读取的 SN 与点位值应属于同一条追溯记录；无对应记录时创建。
        var snRecord = await ResolveSnRecordAsync(preview.Sn, preview.DeviceName, ct);
        var record = new SnapshotRecord
        {
            RuleId = preview.RuleId,
            RuleName = preview.RuleName,
            OccurredAt = preview.CapturedAt,
            Sn = preview.Sn,
            SnRecordId = snRecord?.Id,
            ValuesJson = JsonSerializer.Serialize(preview.Values, JsonOptions)
        };
        await store.AddSnapshotRecordAsync(record, ct);
        return record.Id;
    }

    private async Task<SnRecord?> ResolveSnRecordAsync(
        string? trimmedSn, string? deviceName, CancellationToken ct)
    {
        if (trimmedSn is null) return null;
        var existing = await store.GetLatestSnRecordBySnAsync(trimmedSn, ct);
        if (existing is not null) return existing;
        var (recipeName, recipeVersion) = await store.GetActiveRecipeAsync(ct);
        return await store.AddSnRecordAsync(
            trimmedSn, "PLC", deviceName, recipeName, recipeVersion, null, ct);
    }

    private SnapshotValueItem[] BuildValues(SnapshotRule rule)
        => rule.Points
            .OrderBy(point => point.SortOrder)
            .Select(point =>
            {
                var snapshot = runtime.GetTagSnapshot(point.TagId);
                var unit = string.IsNullOrWhiteSpace(point.Unit) ? null : point.Unit.Trim();
                return new SnapshotValueItem(
                    point.TagId,
                    ResolveName(point),
                    Describe(snapshot.CurrentValue, point.Decimals, unit),
                    snapshot.Quality.ToString(),
                    point.Required,
                    unit);
            }).ToArray();

    // 别名优先作为报告列名；未填别名时退回点位名称。
    private static string ResolveName(SnapshotRulePoint point)
    {
        var alias = (point.Alias ?? "").Trim();
        if (alias.Length > 0) return alias;
        return point.Tag?.Name ?? "已删除点位";
    }

    // 数值按采集项配置的小数位格式化，并附加单位；布尔与字符串保持原样。
    private static string Describe(object? value, int decimals, string? unit) => value switch
    {
        null => "",
        bool flag => flag ? "TRUE" : "FALSE",
        double number => Format(number, decimals, unit),
        float number => Format(number, decimals, unit),
        decimal number => Format((double)number, decimals, unit),
        string text => text,
        _ => value.ToString() ?? ""
    };

    private static string Format(double number, int decimals, string? unit)
    {
        var text = number.ToString("F" + Math.Clamp(decimals, 0, 6), CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(unit) ? text : $"{text} {unit}";
    }
}

// 快照中的单个采集项。Required/Unit 为后加的可选字段：历史 JSON 缺少这两个属性时按默认值还原。
public sealed record SnapshotValueItem(
    Guid TagId, string Name, string Value, string Quality, bool Required = false, string? Unit = null);

// 试抓预览：与将要写入的快照同构，但尚未落库。
public sealed record SnapshotPreview(
    Guid RuleId,
    string RuleName,
    string? Sn,
    string? DeviceName,
    DateTimeOffset CapturedAt,
    IReadOnlyList<SnapshotValueItem> Values)
{
    /// <summary>必填项中质量不是 Good 的数量（0 表示本次追溯记录完整）。</summary>
    public int MissingRequiredCount => Values.Count(x => x.Required && x.Quality != "Good");
}
