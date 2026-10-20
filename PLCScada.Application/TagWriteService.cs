using System.Buffers.Binary;
using System.Globalization;
using PLCScada.Core;

namespace PLCScada.Application;

// 受控 Tag 写入：权限/量程/编码校验 → 设备在线校验 → 跨实例设备锁 → 写入 → 读回验证 → 审计。
// 与配方下发的差异：单项写入失败不回滚（不存在部分写入状态），由读回结果明确呈现。
public sealed class TagWriteService(
    IScadaStore store,
    IDeviceCommunication communication,
    IRecipeApplyLock applyLock)
{
    public sealed record TagWriteResult(
        bool Succeeded,
        string Message,
        object? PreviousValue,
        object? ReadbackValue);

    public async Task<TagWriteResult> WriteAsync(
        Guid tagId,
        double? engineeringValue = null,
        bool? booleanValue = null,
        CancellationToken ct = default)
    {
        var tag = (await store.GetTagsAsync(ct)).SingleOrDefault(x => x.Id == tagId);
        if (tag is null)
            return Failed("Tag 不存在或已被删除。");
        if (!tag.Enabled)
            return Failed($"Tag“{tag.Name}”已禁用，不能写入。");
        if (tag.AccessMode != AccessMode.ReadWrite)
            return Failed($"Tag“{tag.Name}”未授权读写（当前为只读），不能写入。");
        if (tag.AddressType is not (AddressType.Coil or AddressType.HoldingRegister))
            return Failed($"Tag“{tag.Name}”的地址类型不支持写入，仅支持 Coil 与 Holding Register。");

        var isBoolean = tag.DataType == TagDataType.Bool;
        if (isBoolean && tag.AddressType != AddressType.Coil)
            return Failed($"Bool Tag“{tag.Name}”只能绑定 Coil 写入。");

        object target;
        if (isBoolean)
        {
            if (booleanValue is not bool boolTarget || engineeringValue is not null)
                return Failed($"Bool Tag“{tag.Name}”需要开或关值。");
            target = boolTarget;
        }
        else
        {
            if (engineeringValue is not double numeric || booleanValue is not null || !double.IsFinite(numeric))
                return Failed($"Tag“{tag.Name}”需要有效的数值工程值。");
            if (GetRangeError(tag, numeric) is { } rangeError)
                return Failed(rangeError);
            try
            {
                _ = TagValueCodec.Encode(tag, numeric);
            }
            catch (InvalidDataException ex)
            {
                return Failed($"Tag“{tag.Name}”的工程值无法编码为 {tag.DataType}：{ex.Message}");
            }
            target = numeric;
        }

        var bindings = await store.GetManualOutputBindingsAsync(tag.DeviceId, ct);
        if (bindings.Any(binding => binding.TagId == tag.Id || binding.MonitorTagId == tag.Id))
            return Failed($"Tag“{tag.Name}”已绑定手动输出，请改用手动输出页面操作。");

        var device = await store.GetDeviceAsync(tag.DeviceId, ct);
        if (device is null || !device.Enabled)
            return Failed($"Tag“{tag.Name}”所属设备不存在或已禁用。");
        if (communication.GetSnapshot(device.Id).Status != ConnectionStatus.Online)
            return Failed($"设备“{device.Name}”当前未在线，不能写入。");

        var resourceKey = DeviceLockKey.For(device);
        await using var lease = await applyLock.TryAcquireAsync(resourceKey, TimeSpan.FromSeconds(1), ct);
        if (lease is null)
            return Failed("该 PLC 正由另一个应用实例执行写入，请稍后重试。");

        var latestDevice = await store.GetDeviceAsync(device.Id, ct);
        if (latestDevice is null ||
            !string.Equals(DeviceLockKey.For(latestDevice), resourceKey, StringComparison.Ordinal))
            return Failed("设备连接目标在获取互斥锁后发生变化，未执行写入，请重试。");

        object? previous = null;
        try
        {
            previous = await ReadValueAsync(device, tag, ct);
            if (ValuesMatch(tag, target, previous))
            {
                var unchanged = $"Tag“{tag.Name}”当前值已与目标一致（{Format(target)}），未重复写入。";
                await TryAuditAsync(tag, device, true, unchanged);
                return new(true, unchanged, previous, previous);
            }

            await WriteValueAsync(device, tag, target, ct);
            var readback = await ReadValueAsync(device, tag, ct);
            if (!ValuesMatch(tag, target, readback))
            {
                var mismatch = $"Tag“{tag.Name}”读回值 {Format(readback)} 与目标值 {Format(target)} 不一致，请核对设备。";
                await TryAuditAsync(tag, device, false, mismatch);
                return new(false, mismatch, previous, readback);
            }

            var success = $"Tag“{tag.Name}”已写入并验证：{Format(previous)} → {Format(readback)}。";
            await TryAuditAsync(tag, device, true, success);
            return new(true, success, previous, readback);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = $"Tag“{tag.Name}”写入失败：{ex.Message}";
            await TryAuditAsync(tag, device, false, failure);
            return new(false, failure, previous, null);
        }
    }

    private static TagWriteResult Failed(string message) => new(false, message, null, null);

    private static string? GetRangeError(Tag tag, double value)
    {
        if (tag.MinValue is double min && value < min ||
            tag.MaxValue is double max && value > max)
            return $"Tag“{tag.Name}”的写入值超出工程量程 {tag.MinValue?.ToString() ?? "—"} 至 {tag.MaxValue?.ToString() ?? "—"}。";
        return null;
    }

    private async Task<object> ReadValueAsync(Device device, Tag tag, CancellationToken ct)
    {
        var count = tag.DataType == TagDataType.Bool ? (ushort)1 : TagValueCodec.RegisterCount(tag);
        var bytes = await communication.ReadAsync(device, tag.AddressType, checked((ushort)tag.Address), count, ct);
        return TagValueCodec.Decode(tag, bytes);
    }

    private async Task WriteValueAsync(Device device, Tag tag, object value, CancellationToken ct)
    {
        if (tag.DataType == TagDataType.Bool)
        {
            await communication.WriteCoilAsync(device, checked((ushort)tag.Address), (bool)value, ct);
            return;
        }

        var networkBytes = TagValueCodec.Encode(tag, (double)value);
        if (networkBytes.Length == 2)
        {
            await communication.WriteSingleRegisterAsync(
                device, checked((ushort)tag.Address),
                BinaryPrimitives.ReadUInt16BigEndian(networkBytes), ct);
        }
        else
        {
            await communication.WriteRegistersAsync(device, checked((ushort)tag.Address), networkBytes, ct);
        }
    }

    private async Task TryAuditAsync(Tag tag, Device device, bool succeeded, string summary)
    {
        try
        {
            await store.LogAsync(
                "Tag",
                $"{summary}（设备 {device.Name}）",
                succeeded ? "Information" : "Warning");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Tag 写入审计失败：{0}", ex.Message);
        }
    }

    private static bool ValuesMatch(Tag tag, object expected, object actual)
    {
        if (tag.DataType == TagDataType.Bool)
            return expected is bool expectedBool && actual is bool actualBool && expectedBool == actualBool;
        if (expected is not double expectedValue || actual is not double actualValue)
            return false;
        var tolerance = tag.DataType == TagDataType.Float32
            ? Math.Max(1e-6, Math.Abs(expectedValue) * 1e-6)
            : Math.Max(1e-9, Math.Abs(expectedValue) * 1e-10);
        return Math.Abs(expectedValue - actualValue) <= tolerance;
    }

    private static string Format(object? value)
        => value switch
        {
            bool boolean => boolean ? "开" : "关",
            double number => number.ToString("0.######", CultureInfo.InvariantCulture),
            null => "—",
            _ => value.ToString() ?? "—"
        };
}
