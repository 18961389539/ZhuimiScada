using PLCScada.Core;

namespace PLCScada.Application;

public sealed class ManualOutputService(IScadaStore store, IDeviceCommunication communication)
{
    public sealed record OutputPointTemplate(string Name, string PlcAddress, ushort ModbusAddress);
    public sealed record OutputBinding(
        ManualOutputPoint Point,
        Tag? Tag,
        Tag? MonitorTag,
        bool IsUsable,
        string? Error,
        bool IsMonitorUsable,
        string? MonitorError);

    public sealed record OutputResetItemResult(
        Guid PointId, string PointName, string Address, string Status, string? Error);

    public sealed record OutputResetResult(
        int ResetCount, int FailedCount, int SkippedCount, IReadOnlyList<OutputResetItemResult> Items);

    public static IReadOnlyList<OutputPointTemplate> DefaultOutputPoints { get; } = Array.AsReadOnly(new[]
    {
        new OutputPointTemplate("通电", "Y0", 24576),
        new OutputPointTemplate("压头气缸", "Y1", 24577),
        new OutputPointTemplate("下压气缸", "Y2", 24578),
        new OutputPointTemplate("通电平移", "Y3", 24579),
        new OutputPointTemplate("开机气缸", "Y4", 24580),
        new OutputPointTemplate("模式切换气缸", "Y5", 24581),
        new OutputPointTemplate("灰尘模拟气缸", "Y6", 24582),
        new OutputPointTemplate("地刷通电气缸", "Y7", 24583),
        new OutputPointTemplate("编码器", "Y10", 24584),
        new OutputPointTemplate("扫码启动", "Y11", 24585),
        new OutputPointTemplate("打点气缸", "Y12", 24586),
        new OutputPointTemplate("相机拍照", "Y13", 24587),
        new OutputPointTemplate("相机方案切换", "Y14", 24588),
        new OutputPointTemplate("复位", "M0", 0)
    });

    public async Task<IReadOnlyList<OutputBinding>> GetOutputBindingsAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = await GetEnabledDeviceAsync(deviceId, ct);
        return await LoadOutputBindingsAsync(device, ct);
    }

    public async Task<Guid> CreateOutputPointAsync(Guid deviceId, string name, CancellationToken ct = default)
    {
        var device = await GetEnabledDeviceAsync(deviceId, ct);
        name = NormalizeOutputName(name);
        var points = await store.GetManualOutputPointsAsync(device.Id, ct);
        if (points.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("该设备已存在同名输出按钮。");
        var point = new ManualOutputPoint
        {
            DeviceId = deviceId,
            Name = name,
            SortOrder = points.Count == 0 ? 0 : points.Max(x => x.SortOrder) + 1
        };
        await store.CreateManualOutputPointAsync(point, ct);
        return point.Id;
    }

    public async Task<Guid> SaveOutputPointConfigurationAsync(
        Guid deviceId, Guid? outputPointId, string name, Guid? tagId, CancellationToken ct = default,
        Guid? monitorTagId = null, ManualOutputMode outputMode = ManualOutputMode.Hold, int pulseDurationMs = 300)
    {
        var device = await GetEnabledDeviceAsync(deviceId, ct);
        name = NormalizeOutputName(name);
        if (!Enum.IsDefined(outputMode))
            throw new InvalidOperationException("输出模式无效。");
        if (pulseDurationMs is < 50 or > 10000)
            throw new InvalidOperationException("脉冲时长必须为 50 到 10000 毫秒。");
        var points = await store.GetManualOutputPointsAsync(deviceId, ct);
        if (outputPointId is Guid selectedPointId)
        {
            if (points.All(point => point.Id != selectedPointId))
                throw new InvalidOperationException("输出按钮不存在或已被删除。");
        }
        if (points.Any(point => point.Id != outputPointId && string.Equals(
                point.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("该设备已存在同名输出按钮。");

        if (tagId is Guid selectedTagId)
        {
            var tag = (await store.GetTagsAsync(ct)).SingleOrDefault(x => x.Id == selectedTagId);
            if (tag is null || !IsEligibleTag(device, tag))
                throw new InvalidOperationException(
                    $"Tag 必须属于设备 {device.Name}，且为启用的 Bool/Coil/ReadWrite。");

        }

        var allTags = await store.GetTagsAsync(ct);
        var monitorTag = monitorTagId is Guid selectedMonitorId
            ? allTags.SingleOrDefault(x => x.Id == selectedMonitorId)
            : null;
        if (monitorTagId.HasValue && (monitorTag is null || !IsEligibleMonitorTag(device, monitorTag)))
            throw new InvalidOperationException(
                $"状态监控 Tag 必须属于设备 {device.Name}，且为启用的 Bool/Coil 或 Bool/DiscreteInput。");

        var candidateTags = new[] {
            tagId is Guid controlId ? allTags.Single(x => x.Id == controlId) : null,
            monitorTag
        }.Where(x => x is not null).Cast<Tag>().ToArray();
        if (candidateTags.GroupBy(x => x.Id).Any(x => x.Count() > 1) ||
            candidateTags.GroupBy(x => (x.AddressType, x.Address)).Any(x => x.Count() > 1))
            throw new InvalidOperationException("控制 Tag 与状态监控 Tag 必须使用不同的 Tag 和地址。");

        var conflictingBinding = (await LoadOutputBindingsAsync(device, ct))
            .FirstOrDefault(binding =>
                (!outputPointId.HasValue || binding.Point.Id != outputPointId.Value) &&
                candidateTags.Any(candidate => new[] { binding.Tag, binding.MonitorTag }
                    .Any(other => other is not null && SameTagOrAddress(candidate, other))));
        if (conflictingBinding is not null)
            throw new InvalidOperationException(
                $"控制或监控 Tag/地址已绑定到“{conflictingBinding.Point.Name}”。");

        return await store.SaveManualOutputPointConfigurationAsync(
            deviceId, outputPointId, name, tagId, ct, monitorTagId, outputMode, pulseDurationMs);
    }

    public async Task UpdateOutputPointAsync(Guid deviceId, Guid pointId, string name, CancellationToken ct = default)
    {
        var device = await GetEnabledDeviceAsync(deviceId, ct);
        name = NormalizeOutputName(name);
        var points = await store.GetManualOutputPointsAsync(deviceId, ct);
        _ = points.SingleOrDefault(x => x.Id == pointId)
            ?? throw new InvalidOperationException("输出按钮不存在或已被删除。");
        if (points.Any(x => x.Id != pointId && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("该设备已存在同名输出按钮。");
        var binding = (await LoadOutputBindingsAsync(device, ct)).Single(x => x.Point.Id == pointId);
        await SaveOutputPointConfigurationAsync(
            deviceId, pointId, name, binding.Tag?.Id, ct, binding.MonitorTag?.Id,
            binding.Point.OutputMode, binding.Point.PulseDurationMs);
    }

    public async Task DeleteOutputPointAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
    {
        await GetEnabledDeviceAsync(deviceId, ct);
        await store.DeleteManualOutputPointAsync(deviceId, pointId, ct);
    }

    // 上移/下移一个位置并持久化顺序；首/尾边界无操作。
    public async Task MoveOutputPointAsync(
        Guid deviceId, Guid pointId, int direction, CancellationToken ct = default)
    {
        await GetEnabledDeviceAsync(deviceId, ct);
        if (direction is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(direction), "方向只能是 -1（上移）或 1（下移）。");
        var points = (await store.GetManualOutputPointsAsync(deviceId, ct)).ToList();
        var currentIndex = points.FindIndex(x => x.Id == pointId);
        if (currentIndex < 0)
            throw new InvalidOperationException("输出按钮不存在或已被删除。");
        var targetIndex = currentIndex + direction;
        if (targetIndex < 0 || targetIndex >= points.Count)
            return;
        (points[currentIndex], points[targetIndex]) = (points[targetIndex], points[currentIndex]);
        await store.UpdateManualOutputPointOrderAsync(
            deviceId, points.Select(x => x.Id).ToArray(), ct);
    }

    public async Task<IReadOnlyList<Tag>> GetEligibleTagsAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = await GetEnabledDeviceAsync(deviceId, ct);
        return (await store.GetTagsAsync(ct))
            .Where(tag => IsEligibleTag(device, tag))
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<Tag>> GetEligibleMonitorTagsAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = await GetEnabledDeviceAsync(deviceId, ct);
        return (await store.GetTagsAsync(ct))
            .Where(tag => IsEligibleMonitorTag(device, tag))
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task SaveOutputBindingsAsync(
        Guid deviceId,
        IReadOnlyDictionary<Guid, Guid?> selections,
        CancellationToken ct = default)
    {
        var device = await GetEnabledDeviceAsync(deviceId, ct);
        var points = await store.GetManualOutputPointsAsync(deviceId, ct);
        if (selections.Count != points.Count || points.Any(point => !selections.ContainsKey(point.Id)) ||
            selections.Keys.Any(id => points.All(point => point.Id != id)))
        {
            throw new InvalidOperationException("必须为每个手动输出按钮提交一个绑定选择（可选择未绑定）。");
        }

        var currentBindings = await LoadOutputBindingsAsync(device, ct);
        var monitorTagIds = currentBindings.ToDictionary(x => x.Point.Id, x => x.MonitorTag?.Id);
        var newBindings = points.Select(point => new ManualOutputBinding
        {
            DeviceId = deviceId,
            OutputPointId = point.Id,
            TagId = selections[point.Id],
            MonitorTagId = monitorTagIds.GetValueOrDefault(point.Id)
        }).ToArray();

        await store.SaveManualOutputBindingsAsync(deviceId, newBindings, ct);
    }

    public async Task SaveOutputBindingAsync(
        Guid deviceId,
        Guid outputPointId,
        Guid? tagId,
        CancellationToken ct = default,
        Guid? monitorTagId = null)
    {
        var device = await GetEnabledDeviceAsync(deviceId, ct);
        var output = (await LoadOutputBindingsAsync(device, ct)).SingleOrDefault(x => x.Point.Id == outputPointId)
            ?? throw new ArgumentOutOfRangeException(nameof(outputPointId), "未知的手动输出按钮。");
        await SaveOutputPointConfigurationAsync(
            deviceId, outputPointId, output.Point.Name, tagId, ct,
            monitorTagId ?? output.MonitorTag?.Id, output.Point.OutputMode, output.Point.PulseDurationMs);
    }

    public async Task<IReadOnlyDictionary<Guid, bool?>> ReadOutputsAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = await GetEnabledDeviceAsync(deviceId, ct);
        var bindings = await LoadOutputBindingsAsync(device, ct);
        var states = new Dictionary<Guid, bool?>();
        foreach (var binding in bindings)
        {
            if (!binding.IsUsable || binding.Tag is not { } controlTag)
            {
                states[binding.Point.Id] = null;
                continue;
            }

            if (binding.MonitorTag is not null && !binding.IsMonitorUsable)
            {
                states[binding.Point.Id] = null;
                continue;
            }

            var monitorTag = binding.MonitorTag ?? controlTag;
            var bytes = await communication.ReadAsync(
                device, monitorTag.AddressType, checked((ushort)monitorTag.Address), 1, ct);
            states[binding.Point.Id] = (bytes[0] & 1) != 0;
        }
        return states;
    }

    public async Task SetOutputAsync(
        Guid deviceId,
        Guid outputPointId,
        Guid tagId,
        ushort address,
        bool value,
        CancellationToken ct = default)
    {
        var device = await GetEnabledDeviceAsync(deviceId, ct);
        var point = (await store.GetManualOutputPointsAsync(deviceId, ct))
            .SingleOrDefault(x => x.Id == outputPointId);
        if (value && point is null)
            throw new ArgumentOutOfRangeException(nameof(outputPointId), "未知的手动输出按钮。");
        var tag = (await store.GetTagsAsync(ct)).SingleOrDefault(x => x.Id == tagId);
        if (value)
        {
            var outputName = point!.Name;
            if (tag is null)
                throw new InvalidOperationException($"输出按钮“{outputName}”绑定的 Tag 不存在。");
            if (!IsEligibleTag(device, tag) || tag.Address != address)
                throw new InvalidOperationException($"绑定 Tag“{tag.Name}”已失效；请重新检查按钮绑定。");
            var binding = (await LoadOutputBindingsAsync(device, ct)).Single(x => x.Point.Id == point!.Id);
            if (!binding.IsUsable || binding.Tag?.Id != tagId || binding.Tag.Address != address)
                throw new InvalidOperationException($"输出按钮“{outputName}”的 Tag 绑定已变化，请刷新后重试。");
        }
        try
        {
            await communication.WriteCoilAsync(device, address, value, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await TryAuditAsync(device, "Warning",
                $"手动输出“{point?.Name ?? "已删除按钮"}” / Coil[{address}] 写入 {(value ? 1 : 0)} 失败：{ex.Message}");
            throw;
        }
        await TryAuditAsync(device, "Information",
            $"手动输出“{point?.Name ?? "已删除按钮"}”绑定 TAG“{tag?.Name ?? tagId.ToString()}” / Coil[{address}] 写入 {(value ? 1 : 0)}");
    }

    // 脉冲输出：写 1 → 等待 N ms → 无条件尝试复位写 0（即使等待被取消，如浏览器断开）。
    // 复位失败会明确报告“可能保持为 1”，阻止静默的不安全状态。
    public async Task PulseOutputAsync(
        Guid deviceId, Guid outputPointId, Guid tagId, ushort address, CancellationToken ct = default)
    {
        var device = await GetEnabledDeviceAsync(deviceId, ct);
        var point = (await store.GetManualOutputPointsAsync(deviceId, ct))
            .SingleOrDefault(x => x.Id == outputPointId)
            ?? throw new ArgumentOutOfRangeException(nameof(outputPointId), "未知的手动输出按钮。");
        if (point.OutputMode != ManualOutputMode.Pulse)
            throw new InvalidOperationException($"输出按钮“{point.Name}”不是脉冲模式。");
        var durationMs = Math.Clamp(point.PulseDurationMs, 50, 10000);

        var tag = (await store.GetTagsAsync(ct)).SingleOrDefault(x => x.Id == tagId);
        if (tag is null)
            throw new InvalidOperationException($"输出按钮“{point.Name}”绑定的 Tag 不存在。");
        if (!IsEligibleTag(device, tag) || tag.Address != address)
            throw new InvalidOperationException($"绑定 Tag“{tag.Name}”已失效；请重新检查按钮绑定。");
        var binding = (await LoadOutputBindingsAsync(device, ct)).Single(x => x.Point.Id == point.Id);
        if (!binding.IsUsable || binding.Tag?.Id != tagId || binding.Tag.Address != address)
            throw new InvalidOperationException($"输出按钮“{point.Name}”的 Tag 绑定已变化，请刷新后重试。");

        try
        {
            await communication.WriteCoilAsync(device, address, true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await TryAuditAsync(device, "Warning",
                $"手动输出“{point.Name}”脉冲写入 1 失败：{ex.Message}");
            throw;
        }

        try
        {
            await Task.Delay(durationMs, ct);
        }
        catch (OperationCanceledException)
        {
            // 等待被取消也必须继续复位输出。
        }

        Exception? resetError = null;
        try
        {
            await communication.WriteCoilAsync(device, address, false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            resetError = ex;
        }

        if (resetError is not null)
        {
            var message = $"手动输出“{point.Name}”脉冲（{durationMs} ms）复位写入失败，输出可能保持为 1，请立即确认设备：{resetError.Message}";
            await TryAuditAsync(device, "Warning", message);
            throw new InvalidOperationException(message, resetError);
        }

        await TryAuditAsync(device, "Information",
            $"手动输出“{point.Name}”绑定 TAG“{tag.Name}” / Coil[{address}] 完成 {durationMs} ms 脉冲");
    }

    // 全部归零：对全部可用输出写入 0（含当前为 0 的按钮，写 0 幂等）。
    // 单个失败不中断其余按钮，结果逐项汇总；未绑定/绑定失效的按钮跳过。
    public async Task<OutputResetResult> ResetAllOutputsAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = await GetEnabledDeviceAsync(deviceId, ct);
        var bindings = await LoadOutputBindingsAsync(device, ct);
        var items = new List<OutputResetItemResult>(bindings.Count);
        foreach (var binding in bindings)
        {
            var point = binding.Point;
            if (!binding.IsUsable || binding.Tag is not { } tag)
            {
                items.Add(new(point.Id, point.Name, "—", "未执行", binding.Error ?? "未绑定可用 Tag。"));
                continue;
            }

            var address = checked((ushort)tag.Address);
            try
            {
                await communication.WriteCoilAsync(device, address, false, ct);
                await TryAuditAsync(device, "Information",
                    $"手动输出“{point.Name}”绑定 TAG“{tag.Name}” / Coil[{address}] 全部归零写入 0");
                items.Add(new(point.Id, point.Name, $"Coil[{address}]", "已归零", null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await TryAuditAsync(device, "Warning",
                    $"手动输出“{point.Name}” / Coil[{address}] 全部归零写入失败：{ex.Message}");
                items.Add(new(point.Id, point.Name, $"Coil[{address}]", "失败", ex.Message));
            }
        }

        return new(
            items.Count(x => x.Status == "已归零"),
            items.Count(x => x.Status == "失败"),
            items.Count(x => x.Status == "未执行"),
            items);
    }

    private async Task TryAuditAsync(Device device, string level, string message)
    {
        try
        {
            await store.LogAsync(device.Name, message, level, ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("设备 {0} 手动输出审计保存失败：{1}", device.Name, ex);
        }
    }

    private async Task<IReadOnlyList<OutputBinding>> LoadOutputBindingsAsync(Device device, CancellationToken ct)
    {
        var tags = await store.GetTagsAsync(ct);
        var points = await store.GetManualOutputPointsAsync(device.Id, ct);
        var savedBindings = await store.GetManualOutputBindingsAsync(device.Id, ct);
        var outputBindings = new List<OutputBinding>(points.Count);
        foreach (var point in points)
        {
            var saved = savedBindings.SingleOrDefault(x => x.OutputPointId == point.Id);
            var tag = saved is not null
                ? saved.TagId is Guid tagId ? tags.SingleOrDefault(x => x.Id == tagId) : null
                : point.DefaultModbusAddress is int defaultAddress
                    ? tags.SingleOrDefault(x => x.DeviceId == device.Id && x.Name == point.Name &&
                                                x.Address == defaultAddress)
                    : null;
            var monitorTag = saved?.MonitorTagId is Guid monitorTagId
                ? tags.SingleOrDefault(x => x.Id == monitorTagId)
                : null;
            var isUsable = tag is not null && IsEligibleTag(device, tag);
            var error = tag is null
                ? "未绑定可用 Tag。"
                : isUsable ? null : $"Tag“{tag.Name}”必须是当前设备上启用的 Bool/Coil/ReadWrite。";
            var isMonitorUsable = monitorTag is null || IsEligibleMonitorTag(device, monitorTag);
            var monitorError = monitorTag is not null && !isMonitorUsable
                ? $"状态监控 Tag“{monitorTag.Name}”必须是启用的 Bool/Coil 或 Bool/DiscreteInput。"
                : null;
            outputBindings.Add(new(point, tag, monitorTag, isUsable, error, isMonitorUsable, monitorError));
        }
        var assignedTags = outputBindings
            .SelectMany(binding => new[] { binding.Tag, binding.MonitorTag }
                .Where(tag => tag is not null).Select(tag => (binding.Point.Id, Tag: tag!)))
            .ToArray();
        var conflictedPoints = new HashSet<Guid>();
        for (var i = 0; i < assignedTags.Length; i++)
        for (var j = i + 1; j < assignedTags.Length; j++)
        {
            if (SameTagOrAddress(assignedTags[i].Tag, assignedTags[j].Tag))
            {
                conflictedPoints.Add(assignedTags[i].Id);
                conflictedPoints.Add(assignedTags[j].Id);
            }
        }
        return outputBindings.Select(binding =>
        {
            if (!conflictedPoints.Contains(binding.Point.Id)) return binding;
            const string conflict = "控制或监控 Tag/地址与其他按钮冲突。";
            return binding with
            {
                IsUsable = false,
                Error = conflict,
                IsMonitorUsable = false,
                MonitorError = binding.MonitorTag is null ? binding.MonitorError : conflict
            };
        }).ToArray();
    }

    private static string NormalizeOutputName(string name)
    {
        var normalized = name.Trim();
        if (normalized.Length is < 1 or > 80)
            throw new InvalidOperationException("按钮名称长度必须为 1 到 80 个字符。");
        return normalized;
    }

    private static bool IsEligibleTag(Device device, Tag tag)
        => tag.DeviceId == device.Id && tag.Enabled && tag.AddressType == AddressType.Coil &&
           tag.DataType == TagDataType.Bool && tag.AccessMode == AccessMode.ReadWrite &&
           tag.Address is >= 0 and <= ushort.MaxValue;
    private static bool IsEligibleMonitorTag(Device device, Tag tag)
        => tag.DeviceId == device.Id && tag.Enabled && tag.DataType == TagDataType.Bool &&
           tag.AddressType is AddressType.Coil or AddressType.DiscreteInput &&
           tag.Address is >= 0 and <= ushort.MaxValue;
    private static bool SameTagOrAddress(Tag left, Tag right)
        => left.Id == right.Id ||
           left.AddressType == right.AddressType && left.Address == right.Address;

    private async Task<Device> GetEnabledDeviceAsync(Guid id, CancellationToken ct)
    {
        var device = await store.GetDeviceAsync(id, ct) ?? throw new InvalidOperationException("目标设备不存在。");
        if (!device.Enabled) throw new InvalidOperationException("目标设备已禁用。");
        return device;
    }
}
