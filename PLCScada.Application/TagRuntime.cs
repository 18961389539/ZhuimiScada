using System.Collections.Concurrent;
using System.Globalization;
using PLCScada.Core;

namespace PLCScada.Application;

public sealed class TagRuntime(IScadaStore store, IDeviceCommunication communication) : ITagRuntime
{
    private const int LimitStateNormal = 0;
    private const int LimitStateHigh = 1;
    private const int LimitStateLow = 2;

    private readonly ConcurrentDictionary<Guid, TagSnapshot> _values = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _readLocks = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Action<TagSnapshot>>> _listeners = new();
    private readonly ConcurrentDictionary<Guid, int> _limitStates = new();
    private readonly ConcurrentDictionary<Guid, int> _consecutiveFailures = new();
    private readonly ConcurrentDictionary<Guid, bool> _qualityAlarmsRaised = new();
    private IReadOnlyList<AlarmDefinition> _alarmDefinitions = [];
    private DateTimeOffset _alarmDefinitionsLoadedAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _alarmDefinitionReloadGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _alarmDefFirstHit = new();
    private readonly ConcurrentDictionary<Guid, bool> _alarmDefActive = new();
    private readonly ConcurrentDictionary<Guid, string> _alarmDefMessage = new();
    public TagSnapshot GetTagSnapshot(Guid id) => _values.GetValueOrDefault(id) ?? new(id, null, TagQuality.Uncertain, null, "等待首次读取");
    public IReadOnlyList<TagSnapshot> GetSnapshots() => _values.Keys.Select(GetTagSnapshot).ToArray();
    public async Task<TagSnapshot> GetTagValueAsync(Guid id, CancellationToken ct = default)
    {
        var tag = (await store.GetTagsAsync(ct)).SingleOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Tag 不存在。");
        return await ReadAsync(tag, ct);
    }
    public async Task<TagSnapshot> ReadAsync(Tag tag, CancellationToken ct)
    {
        var gate = _readLocks.GetOrAdd(tag.Id, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            TagSnapshot state;
            try
            {
                if (!tag.Enabled || tag.Device is not { Enabled: true } device) throw new InvalidOperationException("Tag 或设备已禁用。");
                var bytes = await communication.ReadAsync(device, tag.AddressType, (ushort)tag.Address, TagValueCodec.RegisterCount(tag), ct);
                var value = TagValueCodec.Decode(tag, bytes);
                var rangeError = GetRangeError(tag, value);
                state = new(tag.Id, value, rangeError is null ? TagQuality.Good : TagQuality.Bad,
                    DateTimeOffset.UtcNow, rangeError);
                await TrackReadSuccessAsync(tag, ct);
                await EvaluateLimitAlarmAsync(tag, value, ct);
                await EvaluateAlarmDefinitionsAsync(tag, value, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (InvalidOperationException ex)
            {
                // 禁用/手动断开等非通信失败：不计入连续读取失败次数
                state = GetTagSnapshot(tag.Id) with { Quality = TagQuality.Bad, LastError = ex.Message };
            }
            catch (Exception ex)
            {
                await TrackReadFailureAsync(tag, ex.Message, ct);
                state = GetTagSnapshot(tag.Id) with { Quality = TagQuality.Bad, LastError = ex.Message };
            }
            Publish(state); return state;
        }
        finally { gate.Release(); }
    }

    // 合并采集：对一组连续/重叠的数值寄存器只发起一次读取，逐 Tag 按偏移切片解码，
    // 与 ReadAsync 使用同一套质量/量程/限值报警/发布路径；读取失败时组内 Tag 全部标记 Bad 并保留上次值。
    public async Task ReadGroupAsync(
        Device device, AddressType addressType, ushort startAddress, ushort registerCount,
        IReadOnlyList<Tag> tags, CancellationToken ct)
    {
        var gates = tags.Select(tag => _readLocks.GetOrAdd(tag.Id, _ => new(1, 1))).ToArray();
        foreach (var gate in gates)
            await gate.WaitAsync(ct);
        try
        {
            byte[] bytes;
            try
            {
                bytes = await communication.ReadAsync(device, addressType, startAddress, registerCount, ct);
                if (bytes.Length != registerCount * 2)
                    throw new InvalidDataException("Modbus 返回数据长度不符。");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                foreach (var tag in tags)
                {
                    await TrackReadFailureAsync(tag, ex.Message, ct);
                    Publish(GetTagSnapshot(tag.Id) with { Quality = TagQuality.Bad, LastError = ex.Message });
                }
                return;
            }

            foreach (var tag in tags)
            {
                try
                {
                    var offset = (tag.Address - startAddress) * 2;
                    var length = TagValueCodec.RegisterCount(tag) * 2;
                    var value = TagValueCodec.Decode(tag, bytes.AsSpan(offset, length).ToArray());
                    var rangeError = GetRangeError(tag, value);
                    var state = new TagSnapshot(tag.Id, value, rangeError is null ? TagQuality.Good : TagQuality.Bad,
                        DateTimeOffset.UtcNow, rangeError);
                    await TrackReadSuccessAsync(tag, ct);
                    await EvaluateLimitAlarmAsync(tag, value, ct);
                    await EvaluateAlarmDefinitionsAsync(tag, value, ct);
                    Publish(state);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await TrackReadFailureAsync(tag, ex.Message, ct);
                    Publish(GetTagSnapshot(tag.Id) with { Quality = TagQuality.Bad, LastError = ex.Message });
                }
            }
        }
        finally
        {
            foreach (var gate in gates)
                gate.Release();
        }
    }

    // 位批量采集：一次请求读取一段连续位（FC01/FC02），按位解码回各 Tag。
    public async Task ReadBitGroupAsync(
        Device device, AddressType addressType, ushort startAddress, ushort bitCount,
        IReadOnlyList<Tag> tags, CancellationToken ct)
    {
        var gates = tags.Select(tag => _readLocks.GetOrAdd(tag.Id, _ => new(1, 1))).ToArray();
        foreach (var gate in gates)
            await gate.WaitAsync(ct);
        try
        {
            byte[] bytes;
            try
            {
                bytes = await communication.ReadAsync(device, addressType, startAddress, bitCount, ct);
                if (bytes.Length < (bitCount + 7) / 8)
                    throw new InvalidDataException("Modbus 返回位数据长度不符。");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                foreach (var tag in tags)
                {
                    await TrackReadFailureAsync(tag, ex.Message, ct);
                    Publish(GetTagSnapshot(tag.Id) with { Quality = TagQuality.Bad, LastError = ex.Message });
                }
                return;
            }

            foreach (var tag in tags)
            {
                try
                {
                    var bitIndex = tag.Address - startAddress;
                    var value = (object)(((bytes[bitIndex / 8] >> (bitIndex % 8)) & 1) == 1);
                    var state = new TagSnapshot(tag.Id, value, TagQuality.Good, DateTimeOffset.UtcNow, null);
                    await TrackReadSuccessAsync(tag, ct);
                    await EvaluateAlarmDefinitionsAsync(tag, value, ct);
                    Publish(state);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await TrackReadFailureAsync(tag, ex.Message, ct);
                    Publish(GetTagSnapshot(tag.Id) with { Quality = TagQuality.Bad, LastError = ex.Message });
                }
            }
        }
        finally
        {
            foreach (var gate in gates)
                gate.Release();
        }
    }

    // 限值报警按读数评估（越工程量程标 Bad 也照常评估）；采用滞回：达到限值触发，
    // 回到死区另一侧才解除，避免边界抖动反复记录。报警写入失败不影响读取结果。
    private async Task EvaluateLimitAlarmAsync(Tag tag, object value, CancellationToken ct)
    {
        if (tag.DataType == TagDataType.Bool || value is not double number)
        {
            _limitStates.TryRemove(tag.Id, out _);
            return;
        }

        var previous = _limitStates.TryGetValue(tag.Id, out var current) ? current : LimitStateNormal;
        if (previous == LimitStateNormal && tag.AlarmHigh is null && tag.AlarmLow is null)
            return;
        if (previous != LimitStateNormal && tag.AlarmHigh is null && tag.AlarmLow is null)
        {
            _limitStates.TryRemove(tag.Id, out _);
            await TryWriteTagAlarmAsync(tag, null, previous == LimitStateHigh ? "High" : "Low", ct);
            return;
        }

        var deadband = tag.AlarmDeadband > 0 ? tag.AlarmDeadband : 0;
        int desired;
        if (previous == LimitStateHigh)
        {
            desired = tag.AlarmHigh is double high ? number < high - deadband ? LimitStateNormal : LimitStateHigh : LimitStateNormal;
        }
        else if (previous == LimitStateLow)
        {
            desired = tag.AlarmLow is double low ? number > low + deadband ? LimitStateNormal : LimitStateLow : LimitStateNormal;
        }
        else
        {
            desired = tag.AlarmHigh is double highLimit && number >= highLimit ? LimitStateHigh
                : tag.AlarmLow is double lowLimit && number <= lowLimit ? LimitStateLow
                : LimitStateNormal;
        }

        if (desired == previous)
            return;
        _limitStates[tag.Id] = desired;
        var unit = string.IsNullOrWhiteSpace(tag.Unit) ? "" : $" {tag.Unit}";
        var message = desired switch
        {
            LimitStateHigh => $"读数 {Format(number)}{unit} 达到或高于报警上限 {Format(tag.AlarmHigh!.Value)}{unit}。",
            LimitStateLow => $"读数 {Format(number)}{unit} 达到或低于报警下限 {Format(tag.AlarmLow!.Value)}{unit}。",
            _ => null
        };
        var condition = (message is null ? previous : desired) switch
        {
            LimitStateHigh => "High",
            LimitStateLow => "Low",
            _ => null
        };
        await TryWriteTagAlarmAsync(tag, message, condition, ct);
    }

    private async Task TryWriteTagAlarmAsync(Tag tag, string? message, string? condition, CancellationToken ct)
    {
        try
        {
            await store.SetTagAlarmAsync(tag.Id, tag.Name, message, condition, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Tag 报警写入失败：{0}", ex.Message);
        }
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string? GetRangeError(Tag tag, object value)
    {
        if (value is not double number) return null;
        if (tag.MinValue is double min && number < min)
            return $"工程值 {number.ToString("0.###", CultureInfo.InvariantCulture)} 低于最小值 {min.ToString("0.###", CultureInfo.InvariantCulture)}。";
        if (tag.MaxValue is double max && number > max)
            return $"工程值 {number.ToString("0.###", CultureInfo.InvariantCulture)} 高于最大值 {max.ToString("0.###", CultureInfo.InvariantCulture)}。";
        return null;
    }
    public void MarkUnavailable(Tag tag, string error)
        => Publish(GetTagSnapshot(tag.Id) with { Quality = TagQuality.Stale, LastError = error });
    public void RemoveMissing(IReadOnlyList<Tag> tags)
    {
        var ids = tags.Select(x => x.Id).ToHashSet();
        foreach (var id in _values.Keys.Where(x => !ids.Contains(x)))
        { Publish(new(id, null, TagQuality.Bad, null, "Tag 已删除")); _values.TryRemove(id, out _); }
        foreach (var id in _limitStates.Keys.Where(x => !ids.Contains(x)))
            _limitStates.TryRemove(id, out _);
        foreach (var id in _consecutiveFailures.Keys.Where(x => !ids.Contains(x)))
            _consecutiveFailures.TryRemove(id, out _);
        foreach (var id in _qualityAlarmsRaised.Keys.Where(x => !ids.Contains(x)))
            _qualityAlarmsRaised.TryRemove(id, out _);
    }

    private int _qualityThreshold;
    private DateTimeOffset _qualityThresholdLoadedAt = DateTimeOffset.MinValue;

    // 全局质量报警阈值（系统设置维护，10 秒缓存；0 = 关闭）。
    private async Task<int> GetQualityThresholdAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _qualityThresholdLoadedAt < TimeSpan.FromSeconds(10))
            return _qualityThreshold;
        _qualityThreshold = await store.GetQualityAlarmFailuresAsync(0, ct);
        _qualityThresholdLoadedAt = DateTimeOffset.UtcNow;
        return _qualityThreshold;
    }

    // 连续读取失败跟踪（质量报警）：仅通信/解码类失败计数；达到全局阈值记录一次报警，读取恢复自动解除。
    // 写入失败不影响读取结果（best-effort）。
    private async Task TrackReadFailureAsync(Tag tag, string error, CancellationToken ct)
    {
        var threshold = await GetQualityThresholdAsync(ct);
        if (threshold <= 0)
            return;
        var failures = _consecutiveFailures.AddOrUpdate(tag.Id, 1, (_, current) => current + 1);
        if (failures != threshold)
            return;
        _qualityAlarmsRaised[tag.Id] = true;
        await TryWriteQualityAlarmAsync(tag, $"连续 {failures} 次读取失败：{error}", ct);
    }

    private async Task TrackReadSuccessAsync(Tag tag, CancellationToken ct)
    {
        _consecutiveFailures.TryRemove(tag.Id, out _);
        if (_qualityAlarmsRaised.TryRemove(tag.Id, out _))
            await TryWriteQualityAlarmAsync(tag, null, ct);
    }

    private async Task TryWriteQualityAlarmAsync(Tag tag, string? message, CancellationToken ct)
    {
        try
        {
            await store.SetTagQualityAlarmAsync(tag.Id, tag.Name, message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Tag 质量报警写入失败：{0}", ex.Message);
        }
    }

    // 报警定义求值（位/枚举）：条件持续达到 DelaySeconds 才记录一次，恢复即解除；
    // 定义停用或删除时清理残留的活动报警。写入失败不影响采集（best-effort）。
    private async Task EvaluateAlarmDefinitionsAsync(Tag tag, object? value, CancellationToken ct)
    {
        if (value is null) return;
        var definitions = await GetAlarmDefinitionsAsync(ct);
        foreach (var definition in definitions)
        {
            if (!definition.Enabled || definition.TagId != tag.Id) continue;
            if (MatchesDefinition(definition, tag, value))
            {
                var since = _alarmDefFirstHit.GetOrAdd(definition.Id, DateTimeOffset.UtcNow);
                if (DateTimeOffset.UtcNow - since < TimeSpan.FromSeconds(definition.DelaySeconds)) continue;
                var rendered = RenderAlarmMessage(definition, tag, value);
                if (_alarmDefActive.TryAdd(definition.Id, true))
                {
                    _alarmDefMessage[definition.Id] = rendered;
                    await TryWriteAlarmDefinitionAlarmAsync(definition, rendered, ct);
                }
                else if (_alarmDefMessage.TryGetValue(definition.Id, out var last) && last != rendered)
                {
                    _alarmDefMessage[definition.Id] = rendered;
                    await TryWriteAlarmDefinitionAlarmAsync(definition, rendered, ct);
                }
                continue;
            }
            _alarmDefFirstHit.TryRemove(definition.Id, out _);
            if (_alarmDefActive.TryRemove(definition.Id, out _))
            {
                _alarmDefMessage.TryRemove(definition.Id, out _);
                await TryClearAlarmDefinitionAsync(definition.Id, ct);
            }
        }
    }

    // 定义缓存 10 秒刷新一次；刷新时清理已停用/已删除定义遗留的活动报警。
    private async Task<IReadOnlyList<AlarmDefinition>> GetAlarmDefinitionsAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _alarmDefinitionsLoadedAt < TimeSpan.FromSeconds(10))
            return _alarmDefinitions;
        await _alarmDefinitionReloadGate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _alarmDefinitionsLoadedAt < TimeSpan.FromSeconds(10))
                return _alarmDefinitions;
            var loaded = await store.GetAlarmDefinitionsAsync(ct);
            _alarmDefinitions = loaded;
            _alarmDefinitionsLoadedAt = DateTimeOffset.UtcNow;
            var activeIds = loaded.Where(x => x.Enabled).Select(x => x.Id).ToHashSet();
            foreach (var staleId in _alarmDefActive.Keys.Where(x => !activeIds.Contains(x)).ToArray())
            {
                _alarmDefFirstHit.TryRemove(staleId, out _);
                _alarmDefMessage.TryRemove(staleId, out _);
                if (_alarmDefActive.TryRemove(staleId, out _))
                    await TryClearAlarmDefinitionAsync(staleId, ct);
            }
            return _alarmDefinitions;
        }
        finally
        {
            _alarmDefinitionReloadGate.Release();
        }
    }

    private static bool MatchesDefinition(AlarmDefinition definition, Tag tag, object value)
    {
        try
        {
            if (definition.Kind == AlarmDefinitionKind.Bit)
                return value is bool flag && flag == definition.BitTarget;
            // 枚举报警只对整数类型生效（浮点值按限值报警处理，避免四舍五入误命中）
            if (tag.DataType is not (TagDataType.Int16 or TagDataType.UInt16
                or TagDataType.Int32 or TagDataType.UInt32))
                return false;
            if (value is not double number)
                return false;
            return AlarmDefinitionService.ParseEnumEntries(definition.EnumValues)
                .Any(x => x.Value == (int)Math.Round(number));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string RenderAlarmMessage(AlarmDefinition definition, Tag tag, object value)
    {
        var text = value is bool flag
            ? (flag ? "1" : "0")
            : Convert.ToDouble(value).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var label = text;
        if (definition.Kind == AlarmDefinitionKind.Enum && value is double number)
        {
            try
            {
                label = AlarmDefinitionService.ParseEnumEntries(definition.EnumValues)
                    .FirstOrDefault(x => x.Value == (int)Math.Round(number))?.Label ?? text;
            }
            catch (InvalidOperationException)
            {
                label = text;
            }
        }
        return definition.Message
            .Replace("{tagName}", tag.Name)
            .Replace("{value}", text)
            .Replace("{label}", label);
    }

    private async Task TryWriteAlarmDefinitionAlarmAsync(
        AlarmDefinition definition, string renderedMessage, CancellationToken ct)
    {
        try
        {
            await store.SetAlarmDefinitionAlarmAsync(
                definition.Id, renderedMessage, definition.Severity, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("报警定义写入失败：{0}", ex.Message);
        }
    }

    private async Task TryClearAlarmDefinitionAsync(Guid definitionId, CancellationToken ct)
    {
        try
        {
            await store.SetAlarmDefinitionAlarmAsync(definitionId, null, ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("报警定义解除失败：{0}", ex.Message);
        }
    }
    private void Publish(TagSnapshot state)
    {
        _values[state.TagId] = state;
        if (_listeners.TryGetValue(state.TagId, out var listeners))
            foreach (var listener in listeners.Values)
                try { listener(state); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Tag subscriber failed: {0}", ex.Message); }
    }
    public IDisposable SubscribeTag(Guid id, Action<TagSnapshot> listener)
    {
        var key = Guid.NewGuid();
        var listeners = _listeners.GetOrAdd(id, _ => new()); listeners[key] = listener;
        return new Subscription(() => listeners.TryRemove(key, out _));
    }
    private sealed class Subscription(Action unsubscribe) : IDisposable
    { private Action? _unsubscribe = unsubscribe; public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke(); }
}
