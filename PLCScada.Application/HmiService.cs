using System.Text.Json;
using PLCScada.Core;

namespace PLCScada.Application;

public sealed class HmiService(IScadaStore store)
{
    public sealed record DisplayItem(HmiControl Control, Tag Tag, HmiDisplayType Type, string Group);

    private sealed record ControlSettings(string Group);

    private static readonly (string Name, HmiDisplayType Type, string Group)[] DefaultControls =
    [
        ("运行状态", HmiDisplayType.Status, ""),
        ("总判定", HmiDisplayType.TotalResult, ""),
        ("转速方向判定", HmiDisplayType.Result, ""),
        ("正常电流判定", HmiDisplayType.Result, ""),
        ("正常功率判定", HmiDisplayType.Result, ""),
        ("正常转速判定", HmiDisplayType.Result, ""),
        ("灰尘感应电流判定", HmiDisplayType.Result, ""),
        ("灰尘感应功率判定", HmiDisplayType.Result, ""),
        ("灰尘感应转速判定", HmiDisplayType.Result, ""),
        ("相机灯亮判定", HmiDisplayType.Result, ""),
        ("相机灯灭判定", HmiDisplayType.Result, ""),
        ("正常电压", HmiDisplayType.Measurement, "正常工况"),
        ("正常电流", HmiDisplayType.Measurement, "正常工况"),
        ("正常功率", HmiDisplayType.Measurement, "正常工况"),
        ("灰尘感应电压", HmiDisplayType.Measurement, "灰尘感应工况"),
        ("灰尘感应电流", HmiDisplayType.Measurement, "灰尘感应工况"),
        ("灰尘感应功率", HmiDisplayType.Measurement, "灰尘感应工况"),
        ("正常功率差值", HmiDisplayType.Measurement, "差值"),
        ("正常转速差值", HmiDisplayType.Measurement, "差值")
    ];

    public async Task<IReadOnlyList<DisplayItem>> GetDisplayItemsAsync(CancellationToken ct = default)
    {
        var page = await EnsureDefaultPageAsync(ct);
        var controls = await store.GetHmiControlsAsync(page.Id, ct);
        return controls
            .Where(control => control.Tag is not null)
            .Select(control => new DisplayItem(
                control,
                control.Tag!,
                ParseDisplayType(control.ControlType),
                GetDisplayGroup(control)))
            .ToArray();
    }

    public async Task<IReadOnlyList<Tag>> GetEligibleTagsAsync(CancellationToken ct = default)
        => (await store.GetTagsAsync(ct))
            .Where(IsEligibleTag)
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<Guid> SaveControlAsync(
        Guid? controlId,
        Guid tagId,
        string name,
        HmiDisplayType type,
        string? group,
        CancellationToken ct = default)
    {
        var page = await EnsureDefaultPageAsync(ct);
        name = name.Trim();
        if (name.Length is < 1 or > 100)
            throw new InvalidOperationException("HMI 显示名称长度必须为 1 到 100 个字符。");
        if (!Enum.IsDefined(type))
            throw new InvalidOperationException("HMI 展示类型无效。");

        var tags = await store.GetTagsAsync(ct);
        var tag = tags.SingleOrDefault(candidate => candidate.Id == tagId);
        if (tag is null || !IsEligibleTag(tag))
            throw new InvalidOperationException("请选择启用的只读数值寄存器 Tag。");
        if (type is HmiDisplayType.Status or HmiDisplayType.TotalResult or HmiDisplayType.Result &&
            tag.DataType is TagDataType.Float32 or TagDataType.Float64)
            throw new InvalidOperationException("状态与判定展示项必须绑定整数寄存器 Tag。");

        var controls = await store.GetHmiControlsAsync(page.Id, ct);
        var current = controlId is Guid id ? controls.SingleOrDefault(item => item.Id == id) : null;
        if (controlId.HasValue && current is null)
            throw new InvalidOperationException("HMI 展示项不存在或已被删除。");
        if (controls.Any(item => item.Id != controlId && item.TagId == tagId))
            throw new InvalidOperationException("同一个 Tag 不能在同一 HMI 画面重复添加。");
        if (type is HmiDisplayType.Status or HmiDisplayType.TotalResult &&
            controls.Any(item => item.Id != controlId && ParseDisplayType(item.ControlType) == type))
            throw new InvalidOperationException("运行状态和总判定在画面中各只能有一个。");
        if (controls.Any(item => item.Id != controlId &&
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("HMI 展示名称不能重复。");

        var normalizedGroup = type == HmiDisplayType.Measurement
            ? NormalizeGroup(group)
            : "";
        var control = new HmiControl
        {
            Id = controlId ?? Guid.Empty,
            HmiPageId = page.Id,
            TagId = tagId,
            Name = name,
            ControlType = type.ToString(),
            ConfigurationJson = JsonSerializer.Serialize(new ControlSettings(normalizedGroup)),
            Position = current?.Position ?? 0
        };
        return await store.SaveHmiControlAsync(control, ct);
    }

    public async Task DeleteControlAsync(Guid controlId, CancellationToken ct = default)
    {
        var page = await EnsureDefaultPageAsync(ct);
        await store.DeleteHmiControlAsync(page.Id, controlId, ct);
    }

    public static HmiDisplayType ParseDisplayType(string? value)
        => Enum.TryParse<HmiDisplayType>(value, out var type) && Enum.IsDefined(type)
            ? type
            : HmiDisplayType.Measurement;

    public static string GetDisplayGroup(HmiControl control)
    {
        try
        {
            return JsonSerializer.Deserialize<ControlSettings>(control.ConfigurationJson)?.Group ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    public static string DisplayTypeLabel(HmiDisplayType type) => type switch
    {
        HmiDisplayType.Status => "运行状态",
        HmiDisplayType.TotalResult => "总判定",
        HmiDisplayType.Result => "单项判定",
        HmiDisplayType.Measurement => "测量值",
        _ => "测量值"
    };

    private async Task<HmiPage> EnsureDefaultPageAsync(CancellationToken ct)
    {
        var existing = await store.GetHmiPageAsync("测试数据监控", ct);
        if (existing is not null) return existing;

        var tags = await store.GetTagsAsync(ct);
        var tagsByName = tags.ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);
        var controls = new List<HmiControl>();
        foreach (var (name, type, group) in DefaultControls)
        {
            if (!tagsByName.TryGetValue(name, out var tag) || !IsEligibleTag(tag))
                continue;
            controls.Add(new HmiControl
            {
                TagId = tag.Id,
                Name = tag.Name,
                ControlType = type.ToString(),
                Position = controls.Count,
                ConfigurationJson = JsonSerializer.Serialize(new ControlSettings(group))
            });
        }

        return await store.EnsureHmiPageAsync(
            new HmiPage { Name = "测试数据监控" }, controls, ct);
    }

    private static bool IsEligibleTag(Tag tag)
        => tag.Enabled &&
           tag.AccessMode == AccessMode.ReadOnly &&
           tag.AddressType is AddressType.HoldingRegister or AddressType.InputRegister &&
           tag.DataType != TagDataType.Bool;

    private static string NormalizeGroup(string? group)
    {
        var normalized = group?.Trim() ?? "";
        if (normalized.Length is < 1 or > 40)
            throw new InvalidOperationException("测量值分组名称长度必须为 1 到 40 个字符。");
        return normalized;
    }
}
