using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using PLCScada.Core;

namespace PLCScada.Application;

public sealed class TagService(IScadaStore store)
{
    public sealed record TagPackageItem(
        string DeviceName,
        string Name,
        string Description,
        AddressType AddressType,
        int Address,
        TagDataType DataType,
        AccessMode AccessMode,
        int ScanInterval,
        double Scale,
        double Offset,
        string Unit,
        double? MinValue,
        double? MaxValue,
        string? DefaultValue,
        bool Enabled,
        ByteOrder ByteOrder,
        WordOrder WordOrder,
        double? AlarmHigh,
        double? AlarmLow,
        double AlarmDeadband,
        int QualityAlarmAfterFailures = 0);

    public sealed record TagBundle(
        string Format,
        int SchemaVersion,
        DateTimeOffset ExportedAt,
        IReadOnlyList<TagPackageItem> Tags);

    public sealed record TagImportItemResult(string Name, string Status, string Message);

    public sealed record TagImportResult(int Imported, int Failed, IReadOnlyList<TagImportItemResult> Items);

    public sealed record TagBatchFailure(string Name, string Error);

    public sealed record TagBatchResult(int Succeeded, IReadOnlyList<TagBatchFailure> Failed);

    private static readonly JsonSerializerOptions BundleJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken ct = default) => store.GetTagsAsync(ct);
    public async Task SaveAsync(Tag input, CancellationToken ct = default)
    {
        var tag = input.Copy();
        tag.Name = tag.Name.Trim();
        Validator.ValidateObject(tag, new ValidationContext(tag), true);
        if (await store.GetDeviceAsync(tag.DeviceId, ct) is null) throw new ValidationException("必须选择有效设备。");
        if ((await store.GetTagsAsync(ct)).Any(x => x.Id != tag.Id &&
            string.Equals(x.Name, tag.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException("Tag 名称已存在，请使用唯一名称。");
        if (!Enum.IsDefined(tag.AddressType) || !Enum.IsDefined(tag.DataType) || !Enum.IsDefined(tag.AccessMode) ||
            !Enum.IsDefined(tag.ByteOrder) || !Enum.IsDefined(tag.WordOrder))
            throw new ValidationException("变量类型无效。");
        var bit = tag.AddressType is AddressType.Coil or AddressType.DiscreteInput;
        if (bit != (tag.DataType == TagDataType.Bool)) throw new ValidationException("位地址必须绑定 Bool；寄存器必须绑定数值或字符串类型。");
        if (tag.DataType == TagDataType.String)
        {
            if (tag.StringRegisters is < 1 or > 125)
                throw new ValidationException("String 寄存器数必须为 1 到 125。");
            if (tag.AccessMode != AccessMode.ReadOnly)
                throw new ValidationException("String 类型 Tag 仅支持只读。");
        }
        if (tag.AddressType is AddressType.DiscreteInput or AddressType.InputRegister && tag.AccessMode != AccessMode.ReadOnly)
            throw new ValidationException("离散输入和输入寄存器只能读取。");
        var words = TagValueCodec.RegisterCount(tag);
        if (tag.Address + words > 65536) throw new ValidationException("变量超出 Modbus 地址边界。");
        if (!double.IsFinite(tag.Scale) || tag.Scale == 0 || !double.IsFinite(tag.Offset) ||
            tag.MinValue is double min && !double.IsFinite(min) || tag.MaxValue is double max && !double.IsFinite(max) ||
            tag.MinValue > tag.MaxValue) throw new ValidationException("缩放与数值范围不合法。");
        ValidateAlarmLimits(tag);
        await store.SaveTagAsync(tag, ct);
    }
    private static void ValidateAlarmLimits(Tag tag)
    {
        if (tag.QualityAlarmAfterFailures is < 0 or > 1000)
            throw new ValidationException("连续读取失败报警次数必须为 0 到 1000，0 表示关闭。");
        if (tag.DataType is TagDataType.Bool or TagDataType.String &&
            (tag.AlarmHigh is not null || tag.AlarmLow is not null || tag.AlarmDeadband != 0))
            throw new ValidationException("Bool/String 类型的 Tag 不支持限值报警。");
        if (tag.AlarmHigh is double high && !double.IsFinite(high) ||
            tag.AlarmLow is double low && !double.IsFinite(low) ||
            !double.IsFinite(tag.AlarmDeadband) || tag.AlarmDeadband < 0)
            throw new ValidationException("报警限值与死区必须是有效数值，死区不能为负。");
        if (tag.AlarmHigh is double upper && tag.AlarmLow is double lower && lower >= upper)
            throw new ValidationException("报警上限必须大于报警下限。");
        if (tag.AlarmHigh is double upperLimit && tag.MaxValue is double maximum && upperLimit > maximum)
            throw new ValidationException("报警上限不能超过工程量程上限。");
        if (tag.AlarmLow is double lowerLimit && tag.MinValue is double minimum && lowerLimit < minimum)
            throw new ValidationException("报警下限不能低于工程量程下限。");
    }
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => store.DeleteTagAsync(id, ct);

    // 批量启用/禁用：逐个复用完整校验与单条审计（每个 Tag 一条修改记录）；单个失败不中断其余。
    public async Task<TagBatchResult> SetEnabledAsync(
        IReadOnlyList<Guid> tagIds, bool enabled, CancellationToken ct = default)
    {
        var ids = tagIds.Distinct().ToArray();
        if (ids.Length == 0)
            return new(0, []);
        var tags = (await store.GetTagsAsync(ct)).Where(tag => ids.Contains(tag.Id)).ToArray();
        if (tags.Length != ids.Length)
            throw new InvalidOperationException("部分 Tag 不存在或已被删除，请刷新后重试。");

        var failed = new List<TagBatchFailure>();
        var succeeded = 0;
        foreach (var tag in tags.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var updated = tag.Copy();
                updated.Enabled = enabled;
                await SaveAsync(updated, ct);
                succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(new(tag.Name, ex.Message));
            }
        }
        return new(succeeded, failed);
    }

    public string ExportBundleJson(IReadOnlyList<Tag> tags, IReadOnlyDictionary<Guid, string> deviceNames)
    {
        var items = tags.Select(tag => new TagPackageItem(
            tag.DeviceId is Guid deviceId && deviceNames.TryGetValue(deviceId, out var name)
                ? name
                : "",
            tag.Name,
            tag.Description,
            tag.AddressType,
            tag.Address,
            tag.DataType,
            tag.AccessMode,
            tag.ScanInterval,
            tag.Scale,
            tag.Offset,
            tag.Unit,
            tag.MinValue,
            tag.MaxValue,
            tag.DefaultValue,
            tag.Enabled,
            tag.ByteOrder,
            tag.WordOrder,
            tag.AlarmHigh,
            tag.AlarmLow,
            tag.AlarmDeadband,
            tag.QualityAlarmAfterFailures)).ToArray();

        return JsonSerializer.Serialize(
            new TagBundle("PLCScada.TagBundle", 1, DateTimeOffset.UtcNow, items),
            BundleJsonOptions);
    }

    public async Task<TagImportResult> ImportBundleJsonAsync(string json, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 5_000_000)
            throw new InvalidOperationException("Tag 配置文件为空或超过 5 MB。");

        TagBundle bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<TagBundle>(json, BundleJsonOptions)
                ?? throw new InvalidOperationException("Tag 配置文件内容为空。");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Tag 配置 JSON 格式无效：{ex.Message}", ex);
        }

        if (bundle.Format != "PLCScada.TagBundle" || bundle.SchemaVersion != 1)
            throw new InvalidOperationException("不支持该 Tag 配置文件格式或版本。");
        if (bundle.Tags is null || bundle.Tags.Count is < 1 or > 5000 ||
            bundle.Tags.Any(item => item is null || string.IsNullOrWhiteSpace(item.Name)))
            throw new InvalidOperationException("Tag 配置条目数量必须为 1 到 5000 项，且每项必须有名称。");

        var devices = await store.GetDevicesAsync(ct);
        var items = new List<TagImportItemResult>(bundle.Tags.Count);
        foreach (var item in bundle.Tags)
        {
            try
            {
                var matches = devices.Where(device =>
                    string.Equals(device.Name, item.DeviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1)
                    throw new InvalidOperationException(
                        $"找不到唯一匹配设备“{item.DeviceName}”，请先创建或重命名设备后再导入。");

                await SaveAsync(new Tag
                {
                    DeviceId = matches[0].Id,
                    Name = item.Name,
                    Description = item.Description ?? "",
                    AddressType = item.AddressType,
                    Address = item.Address,
                    DataType = item.DataType,
                    AccessMode = item.AccessMode,
                    ScanInterval = item.ScanInterval,
                    Scale = item.Scale,
                    Offset = item.Offset,
                    Unit = item.Unit ?? "",
                    MinValue = item.MinValue,
                    MaxValue = item.MaxValue,
                    DefaultValue = item.DefaultValue,
                    Enabled = item.Enabled,
                    ByteOrder = item.ByteOrder,
                    WordOrder = item.WordOrder,
                    AlarmHigh = item.AlarmHigh,
                    AlarmLow = item.AlarmLow,
                    AlarmDeadband = item.AlarmDeadband,
                    QualityAlarmAfterFailures = item.QualityAlarmAfterFailures
                }, ct);
                items.Add(new(item.Name, "已导入", ""));
            }
            catch (Exception ex)
            {
                items.Add(new(item.Name, "未导入", ex.Message));
            }
        }

        var imported = items.Count(x => x.Status == "已导入");
        return new(imported, items.Count - imported, items);
    }

    // 同设备 + 同地址类型下的范围重叠检测（不阻断保存：同一寄存器的别名 Tag 是合法用法，仅提示）。
    public static IReadOnlyList<Tag> FindAddressConflicts(IReadOnlyList<Tag> tags, Tag candidate)
        => tags.Where(tag => tag.Id != candidate.Id &&
                             tag.DeviceId == candidate.DeviceId &&
                             TagAddressRange.Overlaps(tag, candidate))
               .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
               .ToArray();

    public static IReadOnlyDictionary<Guid, IReadOnlyList<Tag>> FindAddressConflictsByTag(IReadOnlyList<Tag> tags)
    {
        var result = new Dictionary<Guid, IReadOnlyList<Tag>>();
        foreach (var tag in tags)
        {
            var conflicts = FindAddressConflicts(tags, tag);
            if (conflicts.Count > 0)
                result[tag.Id] = conflicts;
        }
        return result;
    }
}
