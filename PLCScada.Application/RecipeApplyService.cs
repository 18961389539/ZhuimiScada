using PLCScada.Core;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PLCScada.Application;

public sealed class RecipeApplyService(
    IScadaStore store,
    IDeviceCommunication communication,
    IRecipeApplyLock applyLock)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> DeviceApplyGates = new();

    public sealed record PreviewItem(
        Guid TagId,
        string TagName,
        string Address,
        string Type,
        string Unit,
        object? CurrentValue,
        object? TargetValue,
        string? Error);

    public sealed record ApplyPreview(
        Guid RecipeId,
        int RecipeRevision,
        string RecipeName,
        Guid? DeviceId,
        string DeviceName,
        string DeviceEndpoint,
        DateTimeOffset CapturedAt,
        string ConfigurationFingerprint,
        string CurrentValuesFingerprint,
        string? BlockReason,
        IReadOnlyList<PreviewItem> Items)
    {
        public bool CanApply => BlockReason is null && Items.Count > 0 && Items.All(x => x.Error is null);
    }

    public sealed record ApplyItemResult(
        Guid TagId,
        string TagName,
        string Address,
        object TargetValue,
        string Status,
        string? Error);

    public sealed record ApplyResult(
        bool Succeeded,
        bool WritesStarted,
        bool? RollbackSucceeded,
        string Summary,
        IReadOnlyList<ApplyItemResult> Items);

    // 配方识别：读出设备当前值，判定每个配方与设备实际配置的匹配度（“当前 PLC 在用哪个配方”）。
    public sealed record DeviceRecipeMatch(
        Guid RecipeId,
        int Revision,
        string RecipeName,
        int TotalItems,
        int MatchingItems,
        bool FullyMatches,
        string? Error);

    public sealed record RecipeMatchResult(
        Guid DeviceId,
        string DeviceName,
        string DeviceEndpoint,
        DateTimeOffset CapturedAt,
        string? BlockReason,
        IReadOnlyList<DeviceRecipeMatch> Items);

    private sealed record PreparedItem(Tag Tag, object Target, object Previous, bool Changed);

    public async Task<ApplyPreview> PreviewAsync(Guid recipeId, CancellationToken ct = default)
    {
        var recipe = await GetRecipeAsync(recipeId, ct);
        var items = recipe.Parameters.Select(parameter => new PreviewItem(
            parameter.TagId,
            parameter.Tag?.Name ?? "Tag 不存在",
            parameter.Tag is null ? "—" : GetAddress(parameter.Tag),
            parameter.Tag?.DataType.ToString() ?? "未知",
            parameter.Tag?.Unit ?? "",
            null,
            GetTargetValue(parameter),
            null)).ToArray();

        var blockReason = await ValidateRecipeAsync(recipe, ct);
        if (blockReason is not null)
            return new(recipe.Id, recipe.Revision, recipe.Name, recipe.DeviceId,
                await GetDeviceNameAsync(recipe.DeviceId, ct),
                await GetDeviceEndpointAsync(recipe.DeviceId, ct),
                DateTimeOffset.UtcNow, "", "",
                blockReason,
                AddError(items, blockReason));

        var device = await store.GetDeviceAsync(recipe.DeviceId!.Value, ct);
        if (device is null || !device.Enabled)
        {
            const string unavailable = "配方设备不存在或已禁用。";
            return new(recipe.Id, recipe.Revision, recipe.Name, recipe.DeviceId,
                device?.Name ?? "设备不存在", GetDeviceEndpoint(device), DateTimeOffset.UtcNow, "", "",
                unavailable, AddError(items, unavailable));
        }
        var configurationFingerprint = CreateConfigurationFingerprint(recipe, device);
        if (communication.GetSnapshot(device.Id).Status != ConnectionStatus.Online)
        {
            const string offline = "设备当前未在线，不能读取当前值或下发配方。";
            return new(recipe.Id, recipe.Revision, recipe.Name, device.Id,
                device.Name, GetDeviceEndpoint(device), DateTimeOffset.UtcNow, configurationFingerprint, "",
                offline, AddError(items, offline));
        }

        var currentItems = new List<PreviewItem>(recipe.Parameters.Count);
        foreach (var parameter in recipe.Parameters)
        {
            var tag = parameter.Tag!;
            try
            {
                var current = await ReadValueAsync(device, tag, ct);
                currentItems.Add(new(
                    tag.Id, tag.Name, GetAddress(tag), tag.DataType.ToString(), tag.Unit,
                    current, GetTargetValue(parameter), null));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException)
            {
                var error = $"读取当前值失败：{ex.Message}";
                currentItems.Add(new(
                    tag.Id, tag.Name, GetAddress(tag), tag.DataType.ToString(), tag.Unit,
                    null, GetTargetValue(parameter), error));
                var remaining = recipe.Parameters.Skip(currentItems.Count).Select(remainingParameter =>
                {
                    var remainingTag = remainingParameter.Tag!;
                    return new PreviewItem(
                        remainingTag.Id, remainingTag.Name, GetAddress(remainingTag),
                        remainingTag.DataType.ToString(), remainingTag.Unit,
                        null, GetTargetValue(remainingParameter), "因前项读取失败，未读取。");
                });
                return new(recipe.Id, recipe.Revision, recipe.Name, device.Id, device.Name,
                    GetDeviceEndpoint(device),
                    DateTimeOffset.UtcNow, configurationFingerprint,
                    CreateCurrentValuesFingerprint(currentItems),
                    "无法读取设备当前值。", currentItems.Concat(remaining).ToArray());
            }
        }

        return new(recipe.Id, recipe.Revision, recipe.Name, device.Id, device.Name,
            GetDeviceEndpoint(device),
            DateTimeOffset.UtcNow, configurationFingerprint,
            CreateCurrentValuesFingerprint(currentItems), null, currentItems);
    }

    public async Task<ApplyResult> ApplyAsync(
        Guid recipeId, int expectedRevision, string expectedConfigurationFingerprint,
        string expectedCurrentValuesFingerprint,
        CancellationToken ct = default)
    {
        var selectedRecipe = await GetRecipeAsync(recipeId, ct);
        if (selectedRecipe.DeviceId is not Guid deviceId)
            return new(false, false, null, "配方尚未绑定设备，不能下发。", []);
        var gate = DeviceApplyGates.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        IAsyncDisposable? processLease = null;
        try
        {
            var device = await store.GetDeviceAsync(deviceId, ct);
            if (device is null)
            {
                const string missing = "配方设备不存在，未执行写入。";
                await RecordOutcomeAsync(recipeId, expectedRevision, false, missing);
                return new(false, false, null, missing, []);
            }

            var lockedResourceKey = DeviceLockKey.For(device);
            processLease = await applyLock.TryAcquireAsync(
                lockedResourceKey, TimeSpan.FromSeconds(1), ct);
            if (processLease is null)
            {
                const string busy = "该 PLC 正由另一个应用实例执行配方下发，请稍后重试。";
                var auditWarning = await RecordOutcomeAsync(recipeId, expectedRevision, false, busy);
                return new(false, false, null,
                    auditWarning is null ? busy : $"{busy} 审计记录失败：{auditWarning}", []);
            }

            return await ApplyUnderDeviceGateAsync(
                recipeId, expectedRevision, expectedConfigurationFingerprint,
                expectedCurrentValuesFingerprint, lockedResourceKey, ct);
        }
        finally
        {
            try
            {
                if (processLease is not null)
                    await processLease.DisposeAsync();
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task<ApplyResult> ApplyUnderDeviceGateAsync(
        Guid recipeId, int expectedRevision, string expectedConfigurationFingerprint,
        string expectedCurrentValuesFingerprint, string lockedResourceKey, CancellationToken ct)
    {
        var recipe = await GetRecipeAsync(recipeId, ct);
        if (recipe.Revision != expectedRevision)
        {
            const string stale = "配方在预览后已被修改，请重新预览。";
            await RecordOutcomeAsync(recipeId, expectedRevision, false, stale);
            return new(false, false, null, stale, []);
        }

        var preview = await PreviewAsync(recipeId, ct);
        if (!preview.CanApply)
        {
            var summary = preview.BlockReason ?? "有设定项无法读取，未执行任何写入。";
            await RecordOutcomeAsync(recipeId, expectedRevision, false, summary);
            return new(false, false, null, summary, preview.Items.Select(item =>
                new ApplyItemResult(item.TagId, item.TagName, item.Address,
                    item.TargetValue ?? "—", "未执行", item.Error ?? summary)).ToArray());
        }
        if (preview.RecipeRevision != expectedRevision)
        {
            const string stale = "配方在预览期间已被修改，请重新预览。";
            await RecordOutcomeAsync(recipeId, expectedRevision, false, stale);
            return new(false, false, null, stale, []);
        }
        if (!string.Equals(preview.ConfigurationFingerprint, expectedConfigurationFingerprint, StringComparison.Ordinal))
        {
            const string changed = "Tag 或设备配置在预览后发生变化，未执行写入，请重新预览。";
            await RecordOutcomeAsync(recipeId, expectedRevision, false, changed);
            return new(false, false, null, changed, []);
        }
        if (!string.Equals(preview.CurrentValuesFingerprint, expectedCurrentValuesFingerprint, StringComparison.Ordinal))
        {
            const string changed = "PLC 当前值在预览后发生变化，请重新预览确认差异。";
            await RecordOutcomeAsync(recipeId, expectedRevision, false, changed);
            return new(false, false, null, changed, []);
        }

        var device = await store.GetDeviceAsync(preview.DeviceId!.Value, ct)
            ?? throw new InvalidOperationException("配方设备不存在或已删除。");
        var latestRecipe = await GetRecipeAsync(recipeId, ct);
        if (latestRecipe.Revision != expectedRevision ||
            !string.Equals(CreateConfigurationFingerprint(latestRecipe, device), expectedConfigurationFingerprint, StringComparison.Ordinal))
        {
            const string changed = "配方、Tag 或设备配置已变化，未执行写入，请重新预览。";
            await RecordOutcomeAsync(recipeId, expectedRevision, false, changed);
            return new(false, false, null, changed, []);
        }
        var previewByTag = preview.Items.ToDictionary(x => x.TagId);
        var prepared = latestRecipe.Parameters.Select(parameter =>
        {
            var tag = parameter.Tag!;
            var target = GetTargetValue(parameter)!;
            var previous = previewByTag[tag.Id].CurrentValue!;
            return new PreparedItem(tag, target, previous, !ValuesMatch(tag, target, previous));
        }).ToArray();

        var statuses = prepared.Select(item => new ApplyItemResult(
            item.Tag.Id, item.Tag.Name, GetAddress(item.Tag), item.Target,
            item.Changed ? "未执行" : "与设备一致，未写入", null)).ToArray();
        if (prepared.All(item => !item.Changed))
        {
            const string unchanged = "配方与设备当前值完全一致，无需写入。";
            var auditWarning = await RecordOutcomeAsync(recipeId, expectedRevision, true, unchanged);
            return new(true, false, null,
                auditWarning is null ? unchanged : $"{unchanged} 但审计记录失败：{auditWarning}",
                statuses);
        }

        var writeTarget = await store.GetDeviceAsync(device.Id, ct);
        if (writeTarget is null ||
            !string.Equals(DeviceLockKey.For(writeTarget), lockedResourceKey, StringComparison.Ordinal))
        {
            const string lockTargetChanged = "设备连接目标在获取互斥锁后发生变化，未执行写入，请重新预览。";
            await RecordOutcomeAsync(recipeId, expectedRevision, false, lockTargetChanged);
            return new(false, false, null, lockTargetChanged, statuses.Select(item =>
                item with { Status = "未执行写入", Error = lockTargetChanged }).ToArray());
        }

        var attempted = new List<int>();
        Exception? failure = null;

        for (var index = 0; index < prepared.Length; index++)
        {
            var item = prepared[index];
            if (!item.Changed)
                continue;
            try
            {
                var beforeWrite = await ReadValueAsync(device, item.Tag, ct);
                if (!ValuesMatch(item.Tag, item.Previous, beforeWrite))
                    throw new IOException(
                        $"写入前设备值已变化（预览 {Format(item.Previous)}，当前 {Format(beforeWrite)}）。");
                attempted.Add(index);
                await WriteValueAsync(device, item, ct);
                var actual = await ReadValueAsync(device, item.Tag, ct);
                if (!ValuesMatch(item.Tag, item.Target, actual))
                    throw new IOException($"读回值 {Format(actual)} 与目标值 {Format(item.Target)} 不一致。");
                statuses[index] = statuses[index] with { Status = "已写入并验证" };
            }
            catch (Exception ex)
            {
                failure = ex;
                statuses[index] = statuses[index] with
                {
                    Status = attempted.Contains(index) ? "写入或验证失败" : "未执行写入",
                    Error = ex.Message
                };
                break;
            }
        }

        if (failure is null)
        {
            try
            {
                var afterRecipe = await GetRecipeAsync(recipeId, CancellationToken.None);
                var afterDevice = await store.GetDeviceAsync(device.Id, CancellationToken.None);
                if (afterRecipe.Revision != expectedRevision || afterDevice is null ||
                    !string.Equals(
                        CreateConfigurationFingerprint(afterRecipe, afterDevice),
                        expectedConfigurationFingerprint,
                        StringComparison.Ordinal))
                    failure = new InvalidOperationException("下发期间配方、Tag 或设备配置发生变化。");
            }
            catch (Exception ex)
            {
                failure = new InvalidOperationException("下发后无法核对配方配置是否仍一致。", ex);
            }
        }

        if (failure is null)
        {
            const string success = "全部设定项已写入并通过读回验证。";
            var auditWarning = await RecordOutcomeAsync(recipeId, expectedRevision, true,
                $"{success}写入值：{string.Join("；", statuses.Select(x => $"{x.TagName}={Format(x.TargetValue)}"))}");
            return new(true, true, null,
                auditWarning is null ? success : $"{success} 但审计记录失败：{auditWarning}",
                statuses);
        }

        var rollbackSucceeded = true;
        foreach (var index in attempted.AsEnumerable().Reverse())
        {
            var item = prepared[index];
            try
            {
                var current = await ReadValueAsync(device, item.Tag, CancellationToken.None);
                if (ValuesMatch(item.Tag, item.Previous, current))
                {
                    statuses[index] = statuses[index] with
                    {
                        Status = "原值未改变",
                        Error = index == attempted[^1] ? failure.Message : null
                    };
                    continue;
                }
                if (!ValuesMatch(item.Tag, item.Target, current))
                    throw new IOException(
                        $"回滚前检测到设备值已变化（原值 {Format(item.Previous)}，目标值 {Format(item.Target)}，当前值 {Format(current)}），为避免覆盖外部操作，拒绝自动回滚。");

                await WriteObjectValueAsync(device, item.Tag, item.Previous, CancellationToken.None);
                var restored = await ReadValueAsync(device, item.Tag, CancellationToken.None);
                if (!ValuesMatch(item.Tag, item.Previous, restored))
                    throw new IOException($"回滚读回值 {Format(restored)} 与原值 {Format(item.Previous)} 不一致。");
                statuses[index] = statuses[index] with
                {
                    Status = "已回滚",
                    Error = index == attempted[^1] ? failure.Message : null
                };
            }
            catch (Exception rollbackError)
            {
                rollbackSucceeded = false;
                statuses[index] = statuses[index] with
                {
                    Status = "回滚失败，设备值需人工确认",
                    Error = $"{failure.Message}；回滚错误：{rollbackError.Message}"
                };
            }
        }

        var writesStarted = attempted.Count > 0;
        var failureSummary = !writesStarted
            ? $"下发前检查失败，未执行任何写入：{failure.Message}"
            : rollbackSucceeded
                ? $"下发失败，已回滚已尝试的设定项：{failure.Message}"
                : $"下发失败且回滚不完整，请立即核对设备：{failure.Message}";
        var warning = await RecordOutcomeAsync(recipeId, expectedRevision, false,
            $"{failureSummary} {string.Join("；", statuses.Select(x => $"{x.TagName}:{x.Status}"))}");
        if (warning is not null)
            failureSummary += $" 审计记录也失败：{warning}";
        return new(false, writesStarted, writesStarted ? rollbackSucceeded : null, failureSummary, statuses);
    }

    // 设备当前值与各配方逐项比较（每个 Tag 只读取一次，跨配方去重）；
    // 只读操作：不写入、不触发下发审计；读取使用与预览相同的通讯链路。
    public async Task<RecipeMatchResult> MatchRecipesForDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = await store.GetDeviceAsync(deviceId, ct);
        if (device is null || !device.Enabled)
            return new(deviceId, device?.Name ?? "设备不存在", "—", DateTimeOffset.UtcNow,
                "设备不存在或已禁用。", []);
        var recipes = (await store.GetRecipesAsync(ct))
            .Where(recipe => recipe.DeviceId == deviceId)
            .OrderBy(recipe => recipe.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (recipes.Length == 0)
            return new(device.Id, device.Name, GetDeviceEndpoint(device), DateTimeOffset.UtcNow,
                "该设备还没有配方。", []);
        if (communication.GetSnapshot(device.Id).Status != ConnectionStatus.Online)
            return new(device.Id, device.Name, GetDeviceEndpoint(device), DateTimeOffset.UtcNow,
                "设备当前未在线，无法比对当前配置。", []);

        var tagReads = new Dictionary<Guid, (object? Value, string? Error)>();
        foreach (var tag in recipes
                     .SelectMany(recipe => recipe.Parameters)
                     .Select(parameter => parameter.Tag)
                     .Where(tag => tag is not null)
                     .Select(tag => tag!)
                     .DistinctBy(tag => tag.Id))
        {
            try
            {
                tagReads[tag.Id] = (await ReadValueAsync(device, tag, ct), null);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException)
            {
                tagReads[tag.Id] = (null, ex.Message);
            }
        }

        var items = new List<DeviceRecipeMatch>(recipes.Length);
        foreach (var recipe in recipes)
        {
            if (await ValidateRecipeAsync(recipe, ct) is { } validation)
            {
                items.Add(new(recipe.Id, recipe.Revision, recipe.Name, recipe.Parameters.Count, 0, false, validation));
                continue;
            }

            var matching = 0;
            string? error = null;
            foreach (var parameter in recipe.Parameters)
            {
                if (parameter.Tag is not { } tag)
                {
                    error = "参数 Tag 不存在。";
                    break;
                }
                var (value, readError) = tagReads[tag.Id];
                if (readError is not null)
                {
                    error = $"读取 Tag“{tag.Name}”失败：{readError}";
                    break;
                }
                var target = tag.DataType == TagDataType.Bool
                    ? (object?)parameter.BooleanValue
                    : parameter.EngineeringValue;
                if (target is not null && value is not null && ValuesMatch(tag, target, value))
                    matching++;
            }

            items.Add(new(recipe.Id, recipe.Revision, recipe.Name, recipe.Parameters.Count,
                error is null ? matching : 0, error is null && matching == recipe.Parameters.Count, error));
        }

        return new(device.Id, device.Name, GetDeviceEndpoint(device), DateTimeOffset.UtcNow, null, items);
    }

    private async Task<Recipe> GetRecipeAsync(Guid recipeId, CancellationToken ct)
        => await store.GetRecipeAsync(recipeId, ct)
           ?? throw new InvalidOperationException("配方不存在或已被删除。");

    private async Task<string> GetDeviceNameAsync(Guid? deviceId, CancellationToken ct)
        => deviceId is Guid id
            ? (await store.GetDeviceAsync(id, ct))?.Name ?? "设备不存在"
            : "未指定设备";

    private async Task<string> GetDeviceEndpointAsync(Guid? deviceId, CancellationToken ct)
        => deviceId is Guid id
            ? GetDeviceEndpoint(await store.GetDeviceAsync(id, ct))
            : "未绑定设备";

    private static string GetDeviceEndpoint(Device? device)
        => device is null
            ? "设备连接目标不可用"
            : $"{device.Protocol.Trim()} · {device.IpAddress.Trim()}:{device.Port} · Unit {device.UnitId}";

    private async Task<string?> RecordOutcomeAsync(
        Guid recipeId, int appliedRevision, bool succeeded, string summary)
    {
        try
        {
            await store.RecordRecipeApplyAsync(recipeId, appliedRevision, succeeded, summary, CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<string?> ValidateRecipeAsync(Recipe recipe, CancellationToken ct)
    {
        if (recipe.DeviceId is not Guid deviceId)
            return "配方尚未绑定设备，请编辑配方并选择设备。";
        if (recipe.Parameters.Count == 0)
            return "配方没有设定项。";

        var device = await store.GetDeviceAsync(deviceId, ct);
        if (device is null || !device.Enabled)
            return "配方设备不存在或已禁用。";

        var reservedTagIds = new HashSet<Guid>();
        foreach (var candidate in await store.GetDevicesAsync(ct))
        {
            var bindings = await store.GetManualOutputBindingsAsync(candidate.Id, ct);
            foreach (var binding in bindings)
            {
                if (binding.TagId is Guid controlId) reservedTagIds.Add(controlId);
                if (binding.MonitorTagId is Guid monitorId) reservedTagIds.Add(monitorId);
            }
        }

        var tags = new List<Tag>();
        foreach (var parameter in recipe.Parameters)
        {
            var tag = parameter.Tag;
            if (tag is null || tag.DeviceId != deviceId ||
                !IsEligibleTag(tag) || reservedTagIds.Contains(tag.Id))
                return $"Tag“{tag?.Name ?? parameter.TagId.ToString()}”不属于配方设备或不允许由配方写入。";

            if (tag.DataType == TagDataType.Bool)
            {
                if (parameter.BooleanValue is null || parameter.EngineeringValue is not null)
                    return $"Bool Tag“{tag.Name}”的配方值无效。";
            }
            else
            {
                if (parameter.EngineeringValue is not double value ||
                    parameter.BooleanValue is not null || !double.IsFinite(value))
                    return $"数值 Tag“{tag.Name}”的配方值无效。";
                if (tag.MinValue is double min && value < min ||
                    tag.MaxValue is double max && value > max)
                    return $"Tag“{tag.Name}”的配方值超出工程量程。";
                try
                {
                    _ = TagValueCodec.Encode(tag, value);
                }
                catch (InvalidDataException ex)
                {
                    return $"Tag“{tag.Name}”无法编码：{ex.Message}";
                }
            }
            tags.Add(tag);
        }

        for (var i = 0; i < tags.Count; i++)
        for (var j = i + 1; j < tags.Count; j++)
        {
            if (TagAddressRange.Overlaps(tags[i], tags[j]))
                return $"Tag“{tags[i].Name}”与“{tags[j].Name}”的 Modbus 地址范围重叠。";
        }

        return null;
    }

    private async Task<object> ReadValueAsync(Device device, Tag tag, CancellationToken ct)
    {
        var count = tag.DataType == TagDataType.Bool ? (ushort)1 : TagValueCodec.RegisterCount(tag);
        var bytes = await communication.ReadAsync(
            device, tag.AddressType, checked((ushort)tag.Address), count, ct);
        return TagValueCodec.Decode(tag, bytes);
    }

    private async Task WriteValueAsync(Device device, PreparedItem item, CancellationToken ct)
        => await WriteObjectValueAsync(device, item.Tag, item.Target, ct);

    private async Task WriteObjectValueAsync(Device device, Tag tag, object value, CancellationToken ct)
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
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(networkBytes), ct);
        }
        else
        {
            await communication.WriteRegistersAsync(
                device, checked((ushort)tag.Address), networkBytes, ct);
        }
    }

    private static object? GetTargetValue(RecipeParameter parameter)
        => parameter.Tag?.DataType == TagDataType.Bool
            ? parameter.BooleanValue
            : parameter.EngineeringValue;

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

    private static bool IsEligibleTag(Tag tag)
        => tag.Enabled &&
           tag.AccessMode == AccessMode.ReadWrite &&
           Enum.IsDefined(tag.AddressType) &&
           Enum.IsDefined(tag.DataType) &&
           tag.Address is >= 0 and <= ushort.MaxValue &&
           (tag.AddressType == AddressType.HoldingRegister && tag.DataType != TagDataType.Bool ||
            tag.AddressType == AddressType.Coil && tag.DataType == TagDataType.Bool);

    private static string CreateConfigurationFingerprint(Recipe recipe, Device device)
    {
        var configuration = new
        {
            RecipeId = recipe.Id,
            recipe.Revision,
            Device = new
            {
                device.Id,
                device.Name,
                device.Protocol,
                device.Enabled,
                device.IpAddress,
                device.Port,
                device.UnitId,
                device.ConnectionTimeout
            },
            Parameters = recipe.Parameters.OrderBy(x => x.TagId).Select(parameter => new
            {
                parameter.TagId,
                parameter.EngineeringValue,
                parameter.BooleanValue,
                Tag = parameter.Tag is { } tag
                    ? new
                    {
                        tag.DeviceId,
                        tag.Name,
                        tag.Unit,
                        tag.AddressType,
                        tag.Address,
                        tag.DataType,
                        tag.AccessMode,
                        tag.Enabled,
                        tag.ByteOrder,
                        tag.WordOrder,
                        tag.Scale,
                        tag.Offset,
                        tag.MinValue,
                        tag.MaxValue
                    }
                    : null
            })
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(configuration))));
    }

    private static string CreateCurrentValuesFingerprint(IEnumerable<PreviewItem> items)
    {
        var values = items.OrderBy(x => x.TagId)
            .Select(x => new { x.TagId, x.CurrentValue });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(values))));
    }

    private static string GetAddress(Tag tag)
        => $"{tag.AddressType}[{tag.Address}]";

    private static string Format(object? value)
        => value switch
        {
            bool boolean => boolean ? "开" : "关",
            double number => number.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
            null => "—",
            _ => value.ToString() ?? "—"
        };

    private static PreviewItem[] AddError(IReadOnlyList<PreviewItem> items, string error)
        => items.Select(item => item with { Error = error }).ToArray();
}
