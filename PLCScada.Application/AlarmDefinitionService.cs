using System.Globalization;
using PLCScada.Core;

namespace PLCScada.Application;

// 报警定义管理：位报警（M/Coil 点位）与枚举报警（状态字/故障码）。
public sealed class AlarmDefinitionService(IScadaStore store)
{
    public Task<IReadOnlyList<AlarmDefinition>> GetAllAsync(CancellationToken ct = default)
        => store.GetAlarmDefinitionsAsync(ct);

    public Task<IReadOnlyList<Tag>> GetTagsAsync(CancellationToken ct = default)
        => store.GetTagsAsync(ct);

    public async Task SaveAsync(AlarmDefinition definition, CancellationToken ct = default)
    {
        var tags = await store.GetTagsAsync(ct);
        var tag = tags.SingleOrDefault(x => x.Id == definition.TagId)
            ?? throw new InvalidOperationException("报警定义引用的 Tag 不存在。");
        definition.Name = NormalizeName(definition.Name);
        definition.Message = (definition.Message ?? "").Trim();
        if (definition.Message.Length is < 1 or > 200)
            throw new InvalidOperationException("报警消息长度必须为 1 到 200 个字符（可用 {tagName}、{value} 占位符）。");
        if (definition.DelaySeconds is < 0 or > 3600)
            throw new InvalidOperationException("报警延时秒数必须为 0 到 3600。");
        if (definition.Kind == AlarmDefinitionKind.Bit)
        {
            if (tag.DataType != TagDataType.Bool)
                throw new InvalidOperationException("位报警只能绑定 Bool 类型 Tag（如 M/Coil 点位）。");
            definition.EnumValues = "";
        }
        else
        {
            if (tag.DataType is not (TagDataType.Int16 or TagDataType.UInt16
                or TagDataType.Int32 or TagDataType.UInt32))
                throw new InvalidOperationException(
                    "枚举报警只能绑定整数类型 Tag（Int16/UInt16/Int32/UInt32），浮点值请改用限值报警。");
            var entries = ParseEnumEntries(definition.EnumValues);
            if (entries.Count == 0)
                throw new InvalidOperationException(
                    "枚举报警至少需要一个数值，如 5,6 或带文案 5=急停, 6=堵转。");
            definition.EnumValues = string.Join(",", entries
                .OrderBy(x => x.Value)
                .Select(x => x.Label is null ? x.Value.ToString() : $"{x.Value}={x.Label}"));
        }
        definition.Severity = NormalizeSeverity(definition.Severity);
        await store.SaveAlarmDefinitionAsync(definition, ct);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default) => store.DeleteAlarmDefinitionAsync(id, ct);

    public sealed record EnumEntry(int Value, string? Label);

    // 解析枚举值集合：支持 "5,6" 或带文案 "5=急停, 6=堵转"（中英文逗号/分号分隔）；
    // 文案中不允许再出现分隔符或等号，长度 1–50。
    public static IReadOnlyList<EnumEntry> ParseEnumEntries(string? text)
    {
        var result = new List<EnumEntry>();
        foreach (var part in (text ?? "").Split(
                     [',', '，', ';', '；'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            var valueText = separator < 0 ? part : part[..separator];
            var label = separator < 0 ? null : part[(separator + 1)..].Trim();
            if (!int.TryParse(valueText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var value) || value is < -32768 or > 65535)
                throw new InvalidOperationException($"枚举值“{part}”不是合法数值（-32768 到 65535）。");
            if (label is not null)
            {
                if (label.Length is < 1 or > 50)
                    throw new InvalidOperationException($"枚举文案“{part}”长度必须为 1 到 50 个字符。");
                if (label.IndexOfAny([',', '，', ';', '；', '=']) >= 0)
                    throw new InvalidOperationException($"枚举文案“{label}”不能包含逗号、分号或等号。");
            }
            result.Add(new(value, label));
        }
        return result;
    }

    public static IReadOnlyList<int> ParseEnumValues(string? text)
        => ParseEnumEntries(text).Select(x => x.Value).ToArray();

    private static string NormalizeName(string? name)
    {
        var normalized = (name ?? "").Trim();
        if (normalized.Length is < 1 or > 80)
            throw new InvalidOperationException("报警名称长度必须为 1 到 80 个字符。");
        return normalized;
    }

    private static string NormalizeSeverity(string? severity) => severity switch
    {
        "Error" => "Error",
        "Info" => "Info",
        _ => "Warning"
    };
}
