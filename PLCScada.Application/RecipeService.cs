using PLCScada.Core;
using System.Globalization;
using System.Text.Json;

namespace PLCScada.Application;

public sealed class RecipeService(IScadaStore store)
{
    public enum RevisionDiffKind { Added, Removed, Changed, Unchanged }

    public sealed record RevisionDiffItem(
        Guid TagId,
        string TagName,
        RevisionDiffKind Kind,
        string? LeftValue,
        string? RightValue,
        string? Note);

    public sealed record RevisionComparison(
        int LeftRevision,
        int RightRevision,
        string LeftName,
        string RightName,
        string LeftDescription,
        string RightDescription,
        string LeftChangeNote,
        string RightChangeNote,
        int AddedCount,
        int RemovedCount,
        int ChangedCount,
        int UnchangedCount,
        IReadOnlyList<RevisionDiffItem> Items)
    {
        public bool NameChanged => !string.Equals(LeftName, RightName, StringComparison.Ordinal);
        public bool DescriptionChanged => !string.Equals(LeftDescription, RightDescription, StringComparison.Ordinal);
        public bool ChangeNoteChanged => !string.Equals(LeftChangeNote, RightChangeNote, StringComparison.Ordinal);
    }

    public sealed record ParameterInput(
        Guid TagId,
        double? EngineeringValue = null,
        bool? BooleanValue = null);

    public sealed record RevisionHistoryItem(
        int Revision,
        DateTimeOffset CreatedAt,
        string Name,
        string Description,
        string ChangeNote,
        string DeviceName,
        string DeviceEndpoint,
        IReadOnlyList<RecipeVersionParameterSnapshot> Parameters);

    public sealed record RevisionHistoryPage(
        int CurrentRevision,
        int PageIndex,
        int PageSize,
        int TotalCount,
        IReadOnlyList<RevisionHistoryItem> Items)
    {
        public int PageCount => TotalCount == 0 ? 0 : (TotalCount + PageSize - 1) / PageSize;
    }

    public sealed record RecipePackage(
        string Format,
        int SchemaVersion,
        string Name,
        string Description,
        string DeviceName,
        IReadOnlyList<RecipePackageParameter> Parameters);

    public sealed record RecipePackageParameter(
        string TagName,
        AddressType AddressType,
        int Address,
        TagDataType DataType,
        string Unit,
        double Scale,
        double Offset,
        ByteOrder ByteOrder,
        WordOrder WordOrder,
        double? EngineeringValue,
        bool? BooleanValue);

    public sealed record RecipeBundle(
        string Format,
        int SchemaVersion,
        DateTimeOffset ExportedAt,
        IReadOnlyList<RecipePackage> Recipes);

    public sealed record RecipeBundleExportResult(string Json, int ExportedCount, int SkippedCount);

    public sealed record RecipeImportItemResult(string Name, string Status, string Message);

    public sealed record RecipeImportResult(
        int ImportedCount, int RenamedCount, int FailedCount, IReadOnlyList<RecipeImportItemResult> Items);

    public sealed record RecipeImportPreviewItem(string Name, string Action, string Message);

    public sealed record RecipeImportPreviewResult(
        int ImportableCount, int RenamedCount, int FailedCount, IReadOnlyList<RecipeImportPreviewItem> Items);

    private static readonly JsonSerializerOptions PackageJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public Task<IReadOnlyList<Recipe>> GetAllAsync(CancellationToken ct = default)
        => store.GetRecipesAsync(ct);

    public async Task<RevisionHistoryPage> GetHistoryAsync(
        Guid recipeId, int pageIndex = 0, int pageSize = 10, CancellationToken ct = default)
    {
        if (pageIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (pageSize is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        var versionPage = await store.GetRecipeVersionsPageAsync(recipeId, pageIndex, pageSize, ct);
        var items = versionPage.Versions
            .Select(version =>
            {
                var parameters = DeserializeVersionParameters(version);
                var endpoint = version.DeviceIpAddress is null || version.DevicePort is null ||
                               version.DeviceUnitId is null
                    ? "设备连接目标未留档"
                    : $"{version.DeviceProtocol ?? "未知协议"} · {version.DeviceIpAddress}:{version.DevicePort} · Unit {version.DeviceUnitId}";
                return new RevisionHistoryItem(
                    version.Revision,
                    version.CreatedAt,
                    version.Name,
                    version.Description,
                    version.ChangeNote,
                    version.DeviceName ?? "未指定设备",
                    endpoint,
                    parameters);
            })
            .ToArray();
        return new RevisionHistoryPage(
            versionPage.CurrentRevision, versionPage.PageIndex,
            pageSize, versionPage.TotalCount, items);
    }

    public async Task<RecipeApplyRecordPage> GetApplyRecordsAsync(
        Guid recipeId, int pageIndex = 0, int pageSize = 10, CancellationToken ct = default)
    {
        if (pageIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (pageSize is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        return await store.GetRecipeApplyRecordsAsync(recipeId, pageIndex, pageSize, ct);
    }

    // 基于不可变快照计算任意两个修订之间的差异，不依赖当前 Tag 配置；
    // 值相同但 Tag 签名（地址/类型/缩放等）变化时同样标记为“修改”并备注。
    public async Task<RevisionComparison> CompareRevisionsAsync(
        Guid recipeId, int leftRevision, int rightRevision, CancellationToken ct = default)
    {
        if (leftRevision < 1 || rightRevision < 1)
            throw new InvalidOperationException("版本号无效。");
        if (leftRevision == rightRevision)
            throw new InvalidOperationException("所选两个版本相同，无需对比。");
        if (await store.GetRecipeAsync(recipeId, ct) is null)
            throw new InvalidOperationException("配方不存在或已被删除。");

        var left = await store.GetRecipeVersionAsync(recipeId, leftRevision, ct)
            ?? throw new InvalidOperationException($"找不到版本 v{leftRevision} 的快照。");
        var right = await store.GetRecipeVersionAsync(recipeId, rightRevision, ct)
            ?? throw new InvalidOperationException($"找不到版本 v{rightRevision} 的快照。");
        if (left.SnapshotSchemaVersion != 1 || right.SnapshotSchemaVersion != 1)
            throw new InvalidOperationException("该版本的快照格式不受支持，无法对比。");

        var leftParameters = DeserializeVersionParameters(left).ToDictionary(x => x.TagId);
        var rightParameters = DeserializeVersionParameters(right).ToDictionary(x => x.TagId);
        var items = new List<RevisionDiffItem>();
        var added = 0;
        var removed = 0;
        var changed = 0;
        var unchanged = 0;

        foreach (var (tagId, rightSnapshot) in rightParameters)
        {
            if (!leftParameters.TryGetValue(tagId, out var leftSnapshot))
            {
                added++;
                items.Add(new(tagId, rightSnapshot.TagName, RevisionDiffKind.Added,
                    null, FormatSnapshotValue(rightSnapshot), null));
                continue;
            }

            var valueChanged = !ValuesEqual(leftSnapshot, rightSnapshot);
            var signatureChanged = !SignatureEqual(leftSnapshot, rightSnapshot);
            if (valueChanged || signatureChanged)
            {
                changed++;
                items.Add(new(tagId, rightSnapshot.TagName, RevisionDiffKind.Changed,
                    FormatSnapshotValue(leftSnapshot), FormatSnapshotValue(rightSnapshot),
                    signatureChanged ? "Tag 配置（地址/类型/缩放等）在两版本间已变化" : null));
            }
            else
            {
                unchanged++;
            }
        }

        foreach (var (tagId, leftSnapshot) in leftParameters)
        {
            if (rightParameters.ContainsKey(tagId))
                continue;
            removed++;
            items.Add(new(tagId, leftSnapshot.TagName, RevisionDiffKind.Removed,
                FormatSnapshotValue(leftSnapshot), null, null));
        }

        return new(
            leftRevision, rightRevision,
            left.Name, right.Name,
            left.Description, right.Description,
            left.ChangeNote, right.ChangeNote,
            added, removed, changed, unchanged,
            items.OrderBy(x => x.TagName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Kind)
                .ToArray());
    }

    private static bool ValuesEqual(RecipeVersionParameterSnapshot left, RecipeVersionParameterSnapshot right)
        => left.BooleanValue == right.BooleanValue && left.EngineeringValue == right.EngineeringValue;

    private static bool SignatureEqual(RecipeVersionParameterSnapshot left, RecipeVersionParameterSnapshot right)
        => left.AddressType == right.AddressType &&
           left.Address == right.Address &&
           left.DataType == right.DataType &&
           left.Scale == right.Scale &&
           left.Offset == right.Offset &&
           string.Equals(left.Unit, right.Unit, StringComparison.Ordinal) &&
           left.ByteOrder == right.ByteOrder &&
           left.WordOrder == right.WordOrder;

    private static string? FormatSnapshotValue(RecipeVersionParameterSnapshot snapshot)
        => snapshot.BooleanValue is bool boolean
            ? boolean ? "开" : "关"
            : snapshot.EngineeringValue?.ToString("0.######", CultureInfo.InvariantCulture);

    public async Task<Guid> RestoreRevisionAsync(
        Guid recipeId, int sourceRevision, int expectedCurrentRevision,
        CancellationToken ct = default)
    {
        var current = await store.GetRecipeAsync(recipeId, ct)
            ?? throw new InvalidOperationException("配方不存在或已被删除。");
        if (current.Revision != expectedCurrentRevision)
            throw new InvalidOperationException("配方已在其他窗口中修改，请刷新历史记录后重试。");
        if (sourceRevision == current.Revision)
            throw new InvalidOperationException("所选版本就是当前版本，无需恢复。");

        var version = await store.GetRecipeVersionAsync(recipeId, sourceRevision, ct)
            ?? throw new InvalidOperationException("找不到所选历史版本。");
        if (version.SnapshotSchemaVersion != 1)
            throw new InvalidOperationException("该历史版本的快照格式不受支持，无法恢复。");
        if (version.DeviceId is not Guid deviceId)
            throw new InvalidOperationException("历史版本没有设备绑定信息，无法安全恢复。");

        var device = await store.GetDeviceAsync(deviceId, ct)
            ?? throw new InvalidOperationException("历史版本关联的设备已不存在，无法恢复。");
        if (!DeviceConfigurationMatches(version, device))
            throw new InvalidOperationException(
                "历史版本的设备连接目标与当前配置不同。请先核对设备，再手动编辑配方。");

        var parameterSnapshots = DeserializeVersionParameters(version);
        if (parameterSnapshots.Count is < 1 or > 200)
            throw new InvalidOperationException("历史版本参数数量无效，无法恢复。");

        var tags = (await store.GetTagsAsync(ct)).ToDictionary(x => x.Id);
        var parameters = new List<ParameterInput>(parameterSnapshots.Count);
        foreach (var snapshot in parameterSnapshots)
        {
            if (!tags.TryGetValue(snapshot.TagId, out var tag) ||
                !TagConfigurationMatches(snapshot, tag))
                throw new InvalidOperationException(
                    $"Tag“{snapshot.TagName}”的设备、地址、类型或缩放配置已变化，不能安全恢复该版本。");
            parameters.Add(new(snapshot.TagId, snapshot.EngineeringValue, snapshot.BooleanValue));
        }

        return await SaveAsync(
            recipeId,
            expectedCurrentRevision,
            deviceId,
            version.Name,
            version.Description,
            parameters,
            changeNote: $"恢复自 v{sourceRevision}",
            ct: ct);
    }

    public async Task<IReadOnlyList<Tag>> GetEligibleTagsAsync(Guid? deviceId, CancellationToken ct = default)
    {
        if (deviceId is not Guid selectedDeviceId)
            return [];
        if (await store.GetDeviceAsync(selectedDeviceId, ct) is null)
            throw new InvalidOperationException("所选设备不存在或已删除。");

        var reservedTagIds = await GetManualOutputTagIdsAsync(ct);
        return (await store.GetTagsAsync(ct))
            .Where(tag => tag.DeviceId == selectedDeviceId &&
                          IsEligibleTag(tag) &&
                          !reservedTagIds.Contains(tag.Id))
            .OrderBy(x => x.Device?.Name)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<Guid> SaveAsync(
        Guid? recipeId,
        int? expectedRevision,
        Guid? deviceId,
        string name,
        string? description,
        IReadOnlyCollection<ParameterInput> parameters,
        string changeNote = "",
        CancellationToken ct = default)
    {
        name = name.Trim();
        description = description?.Trim() ?? "";
        changeNote = changeNote?.Trim() ?? "";
        if (name.Length is < 1 or > 100)
            throw new InvalidOperationException("配方名称长度必须为 1 到 100 个字符。");
        if (description.Length > 500)
            throw new InvalidOperationException("配方说明不能超过 500 个字符。");
        if (changeNote.Length > 200)
            throw new InvalidOperationException("变更说明不能超过 200 个字符。");
        if (deviceId is not Guid selectedDeviceId ||
            await store.GetDeviceAsync(selectedDeviceId, ct) is null)
            throw new InvalidOperationException("请选择有效的配方设备。");
        if (parameters.Count is < 1 or > 200)
            throw new InvalidOperationException("配方设定项数量必须为 1 到 200 项。");
        if (parameters.Select(x => x.TagId).Distinct().Count() != parameters.Count)
            throw new InvalidOperationException("同一个 Tag 不能在配方中重复添加。");

        var existingRecipe = recipeId is Guid selectedId
            ? await store.GetRecipeAsync(selectedId, ct)
            : null;
        if (recipeId.HasValue && existingRecipe is null)
            throw new InvalidOperationException("配方不存在或已被删除。");
        if (existingRecipe is not null && expectedRevision != existingRecipe.Revision)
            throw new InvalidOperationException("配方已在其他窗口中修改，请刷新后再保存。");
        if (await store.RecipeNameExistsAsync(name, recipeId, ct))
            throw new InvalidOperationException("配方名称已存在，请使用唯一名称。");

        var tags = await store.GetTagsAsync(ct);
        var reservedTagIds = await GetManualOutputTagIdsAsync(ct);
        var parametersToSave = new List<RecipeParameter>(parameters.Count);
        var assignedTags = new List<Tag>(parameters.Count);
        foreach (var input in parameters)
        {
            var tag = tags.SingleOrDefault(x => x.Id == input.TagId);
            if (tag is null || tag.DeviceId != selectedDeviceId ||
                !IsEligibleTag(tag) || reservedTagIds.Contains(tag.Id))
                throw new InvalidOperationException(
                    "配方项必须属于所选设备，并绑定未分配给手动输出的启用读写数值寄存器或 Bool/Coil Tag。");
            if (assignedTags.Any(existingTag => TagAddressRange.Overlaps(existingTag, tag)))
                throw new InvalidOperationException(
                    $"Tag“{tag.Name}”与配方中的其他 Tag 地址范围重叠。");
            assignedTags.Add(tag);

            if (tag.DataType == TagDataType.Bool)
            {
                if (input.BooleanValue is null || input.EngineeringValue is not null)
                    throw new InvalidOperationException($"Tag“{tag.Name}”是 Bool 类型，配方值必须为开或关。");

                parametersToSave.Add(new RecipeParameter
                {
                    TagId = tag.Id,
                    BooleanValue = input.BooleanValue
                });
                continue;
            }

            if (input.EngineeringValue is not double engineeringValue ||
                input.BooleanValue is not null ||
                !double.IsFinite(engineeringValue))
                throw new InvalidOperationException($"Tag“{tag.Name}”需要有效的数值工程值。");
            if (GetEngineeringValueError(tag, engineeringValue) is { } valueError)
                throw new InvalidOperationException(valueError);

            parametersToSave.Add(new RecipeParameter
            {
                TagId = tag.Id,
                EngineeringValue = engineeringValue
            });
        }

        return await store.SaveRecipeAsync(new Recipe
        {
            Id = recipeId ?? Guid.Empty,
            DeviceId = selectedDeviceId,
            Revision = expectedRevision ?? 1,
            Name = name,
            Description = description,
            ChangeNote = changeNote
        }, parametersToSave, ct);
    }

    public Task DeleteAsync(Guid recipeId, CancellationToken ct = default)
        => store.DeleteRecipeAsync(recipeId, ct);

    public static string? GetEngineeringValueError(Tag tag, double engineeringValue)
    {
        if (!double.IsFinite(engineeringValue))
            return $"Tag“{tag.Name}”需要有效的数值工程值。";
        if (tag.MinValue is double min && engineeringValue < min ||
            tag.MaxValue is double max && engineeringValue > max)
            return $"Tag“{tag.Name}”的配方值超出工程量程 {tag.MinValue?.ToString() ?? "—"} 至 {tag.MaxValue?.ToString() ?? "—"}。";
        try
        {
            _ = TagValueCodec.Encode(tag, engineeringValue);
        }
        catch (InvalidDataException ex)
        {
            return $"Tag“{tag.Name}”的工程值无法编码为 {tag.DataType}：{ex.Message}";
        }

        return null;
    }

    public async Task<Guid> DuplicateAsync(Guid recipeId, CancellationToken ct = default)
    {
        var source = await store.GetRecipeAsync(recipeId, ct)
            ?? throw new InvalidOperationException("配方不存在或已被删除。");
        var recipeNames = (await store.GetRecipeNamesAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = source.Name.Length > 94 ? source.Name[..94].TrimEnd() : source.Name;
        var name = $"{baseName} 副本";
        var suffix = 2;
        while (recipeNames.Contains(name))
        {
            var suffixText = $" 副本 {suffix++}";
            var prefixLength = Math.Min(source.Name.Length, 100 - suffixText.Length);
            name = $"{source.Name[..prefixLength].TrimEnd()}{suffixText}";
        }

        return await SaveAsync(
            null,
            null,
            source.DeviceId,
            name,
            source.Description,
            source.Parameters.Select(parameter => new ParameterInput(
                parameter.TagId, parameter.EngineeringValue, parameter.BooleanValue)).ToArray(),
            changeNote: $"复制自“{source.Name}”",
            ct: ct);
    }

    public string ExportJson(Recipe recipe, string deviceName)
        => JsonSerializer.Serialize(BuildPackage(recipe, deviceName), PackageJsonOptions);

    // 批量导出：无法构建的配方跳过并计数（不中断整包导出），由 UI 提示。
    public RecipeBundleExportResult ExportBundleJson(
        IReadOnlyList<Recipe> recipes, IReadOnlyDictionary<Guid, string> deviceNames)
    {
        var packages = new List<RecipePackage>(recipes.Count);
        var skipped = 0;
        foreach (var recipe in recipes)
        {
            try
            {
                packages.Add(BuildPackage(recipe, recipe.DeviceId is Guid deviceId
                    ? deviceNames.GetValueOrDefault(deviceId, "")
                    : ""));
            }
            catch (InvalidOperationException)
            {
                skipped++;
            }
        }

        var json = JsonSerializer.Serialize(
            new RecipeBundle("PLCScada.RecipeBundle", 1, DateTimeOffset.UtcNow, packages),
            PackageJsonOptions);
        return new(json, packages.Count, skipped);
    }

    private static RecipePackage BuildPackage(Recipe recipe, string deviceName)
    {
        if (recipe.DeviceId is null || string.IsNullOrWhiteSpace(deviceName))
            throw new InvalidOperationException("配方未绑定有效设备，不能导出。");

        var parameters = recipe.Parameters.Select(parameter =>
        {
            var tag = parameter.Tag
                ?? throw new InvalidOperationException("配方引用的 Tag 不存在，不能导出。");
            if (tag.DeviceId != recipe.DeviceId)
                throw new InvalidOperationException($"Tag“{tag.Name}”不属于配方设备，不能导出。");
            return new RecipePackageParameter(
                tag.Name,
                tag.AddressType,
                tag.Address,
                tag.DataType,
                tag.Unit,
                tag.Scale,
                tag.Offset,
                tag.ByteOrder,
                tag.WordOrder,
                parameter.EngineeringValue,
                parameter.BooleanValue);
        }).ToArray();

        return new RecipePackage(
            "PLCScada.Recipe",
            1,
            recipe.Name,
            recipe.Description,
            deviceName,
            parameters);
    }

    public async Task<Guid> ImportJsonAsync(string json, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1_000_000)
            throw new InvalidOperationException("配方文件为空或超过 1 MB。");

        RecipePackage package;
        try
        {
            package = JsonSerializer.Deserialize<RecipePackage>(json, PackageJsonOptions)
                ?? throw new InvalidOperationException("配方文件内容为空。");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"配方 JSON 格式无效：{ex.Message}", ex);
        }

        ValidatePackage(package);
        var (id, _) = await ImportPackageAsync(package, renameOnConflict: false, ct);
        return id;
    }

    // 批量导入：逐条处理并报告；重名自动加「（导入 n）」后缀；设备/Tag 不匹配的条目跳过并说明原因。
    public async Task<RecipeImportResult> ImportBundleJsonAsync(string json, CancellationToken ct = default)
    {
        var bundle = ParseBundle(json);
        var items = new List<RecipeImportItemResult>(bundle.Recipes.Count);
        foreach (var package in bundle.Recipes)
        {
            try
            {
                ValidatePackage(package);
                var (_, finalName) = await ImportPackageAsync(package, renameOnConflict: true, ct);
                var renamed = !string.Equals(finalName, package.Name, StringComparison.Ordinal);
                items.Add(new(package.Name,
                    renamed ? "已重命名导入" : "已导入",
                    renamed ? $"重命名为“{finalName}”" : ""));
            }
            catch (Exception ex)
            {
                items.Add(new(package.Name, "未导入", ex.Message));
            }
        }

        return new(
            items.Count(x => x.Status == "已导入"),
            items.Count(x => x.Status == "已重命名导入"),
            items.Count(x => x.Status == "未导入"),
            items);
    }

    // 导入前预览：完整执行设备/Tag 匹配与重命名模拟（含包内重复名递增），但不写库；
    // 结论与随后实际导入一致（预览与实际导入之间的其他修改除外）。
    public async Task<RecipeImportPreviewResult> PreviewBundleImportAsync(string json, CancellationToken ct = default)
    {
        var bundle = ParseBundle(json);
        var simulatedNames = (await store.GetRecipeNamesAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = new List<RecipeImportPreviewItem>(bundle.Recipes.Count);
        foreach (var package in bundle.Recipes)
        {
            try
            {
                ValidatePackage(package);
                var (device, _) = await ResolvePackageTargetAsync(package, ct);
                var finalName = ResolveBundleName(package.Name, simulatedNames);
                simulatedNames.Add(finalName);
                var renamed = !string.Equals(finalName, package.Name, StringComparison.Ordinal);
                items.Add(new(package.Name,
                    renamed ? "重命名导入" : "直接导入",
                    renamed ? $"目标名称“{finalName}”（设备 {device.Name}）" : $"设备 {device.Name}"));
            }
            catch (Exception ex)
            {
                items.Add(new(package.Name, "未导入", ex.Message));
            }
        }

        return new(
            items.Count(x => x.Action == "直接导入"),
            items.Count(x => x.Action == "重命名导入"),
            items.Count(x => x.Action == "未导入"),
            items);
    }

    private static RecipeBundle ParseBundle(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 5_000_000)
            throw new InvalidOperationException("配方包文件为空或超过 5 MB。");

        RecipeBundle bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<RecipeBundle>(json, PackageJsonOptions)
                ?? throw new InvalidOperationException("配方包内容为空。");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"配方包 JSON 格式无效：{ex.Message}", ex);
        }

        if (bundle.Format != "PLCScada.RecipeBundle" || bundle.SchemaVersion != 1)
            throw new InvalidOperationException("不支持该配方包格式或版本。");
        if (bundle.Recipes is null || bundle.Recipes.Count is < 1 or > 1000 ||
            bundle.Recipes.Any(item => item is null))
            throw new InvalidOperationException("配方包条目数量必须为 1 到 1000 项。");
        return bundle;
    }

    private static void ValidatePackage(RecipePackage package)
    {
        if (package.Format != "PLCScada.Recipe" || package.SchemaVersion != 1)
            throw new InvalidOperationException("不支持该配方文件格式或版本。");
        if (string.IsNullOrWhiteSpace(package.Name) || package.Description is null ||
            string.IsNullOrWhiteSpace(package.DeviceName))
            throw new InvalidOperationException("配方文件缺少名称、说明或设备名称。");
        if (package.Parameters is null || package.Parameters.Count is < 1 or > 200 ||
            package.Parameters.Any(item => item is null || string.IsNullOrWhiteSpace(item.TagName)))
            throw new InvalidOperationException("配方参数数量必须为 1 到 200 项。");
    }

    private async Task<(Guid Id, string Name)> ImportPackageAsync(
        RecipePackage package, bool renameOnConflict, CancellationToken ct)
    {
        var (device, parameters) = await ResolvePackageTargetAsync(package, ct);
        var name = package.Name;
        if (renameOnConflict)
        {
            var existingNames = (await store.GetRecipeNamesAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            name = ResolveBundleName(name, existingNames);
        }
        var changeNote = renameOnConflict ? "由配方包导入" : "由 JSON 文件导入";

        var id = await SaveAsync(
            null, null, device.Id, name, package.Description, parameters,
            changeNote: changeNote, ct: ct);
        return (id, name);
    }

    // 设备与 Tag 匹配解析（导入与预览共用，只读不写库）。
    private async Task<(Device Device, List<ParameterInput> Parameters)> ResolvePackageTargetAsync(
        RecipePackage package, CancellationToken ct)
    {
        var matchingDevices = (await store.GetDevicesAsync(ct))
            .Where(device => string.Equals(
                device.Name, package.DeviceName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchingDevices.Length != 1)
            throw new InvalidOperationException(
                $"找不到唯一匹配设备“{package.DeviceName}”，请先创建或重命名设备后再导入。");

        var device = matchingDevices[0];
        var eligibleTags = await GetEligibleTagsAsync(device.Id, ct);
        var parameters = new List<ParameterInput>(package.Parameters.Count);
        foreach (var item in package.Parameters)
        {
            var matches = eligibleTags.Where(tag =>
                string.Equals(tag.Name, item.TagName, StringComparison.OrdinalIgnoreCase) &&
                tag.AddressType == item.AddressType &&
                tag.Address == item.Address &&
                tag.DataType == item.DataType &&
                string.Equals(tag.Unit, item.Unit, StringComparison.Ordinal) &&
                tag.Scale == item.Scale &&
                tag.Offset == item.Offset &&
                tag.ByteOrder == item.ByteOrder &&
                tag.WordOrder == item.WordOrder).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"设备“{device.Name}”上找不到与文件匹配的可写 Tag“{item.TagName}”（地址/类型/缩放需一致）。");
            parameters.Add(new(matches[0].Id, item.EngineeringValue, item.BooleanValue));
        }
        return (device, parameters);
    }

    // 导入时以存储中的最新名称为准（每次重新读取，包内重复名依次递增）；
    // 预览时传入模拟名称集合，保证预览结论与随后导入一致。
    private static string ResolveBundleName(string name, ISet<string> existingNames)
    {
        if (!existingNames.Contains(name))
            return name;
        var baseName = name.Length > 92 ? name[..92].TrimEnd() : name;
        var candidate = $"{baseName}（导入）";
        var suffix = 2;
        while (existingNames.Contains(candidate))
        {
            var suffixText = $"（导入 {suffix++}）";
            var prefixLength = Math.Min(baseName.Length, 100 - suffixText.Length);
            candidate = $"{baseName[..prefixLength]}{suffixText}";
        }
        return candidate;
    }

    private async Task<HashSet<Guid>> GetManualOutputTagIdsAsync(CancellationToken ct)
    {
        var reserved = new HashSet<Guid>();
        foreach (var device in await store.GetDevicesAsync(ct))
        {
            var bindings = await store.GetManualOutputBindingsAsync(device.Id, ct);
            foreach (var binding in bindings)
            {
                if (binding.TagId is Guid controlTagId)
                    reserved.Add(controlTagId);
                if (binding.MonitorTagId is Guid monitorTagId)
                    reserved.Add(monitorTagId);
            }
        }
        return reserved;
    }

    private static bool IsEligibleTag(Tag tag)
        => tag.Enabled &&
           tag.AccessMode == AccessMode.ReadWrite &&
           (tag.AddressType == AddressType.HoldingRegister && tag.DataType != TagDataType.Bool ||
            tag.AddressType == AddressType.Coil && tag.DataType == TagDataType.Bool);

    private static IReadOnlyList<RecipeVersionParameterSnapshot> DeserializeVersionParameters(
        RecipeVersion version)
    {
        try
        {
            return JsonSerializer.Deserialize<RecipeVersionParameterSnapshot[]>(version.ParametersJson)
                ?? throw new InvalidOperationException("历史版本参数快照为空。");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("历史版本参数快照损坏，无法读取。", ex);
        }
    }

    private static bool DeviceConfigurationMatches(RecipeVersion version, Device device)
        => string.Equals(version.DeviceProtocol?.Trim(), device.Protocol.Trim(), StringComparison.OrdinalIgnoreCase) &&
           string.Equals(version.DeviceIpAddress?.Trim(), device.IpAddress.Trim(), StringComparison.OrdinalIgnoreCase) &&
           version.DevicePort == device.Port &&
           version.DeviceUnitId == device.UnitId;

    private static bool TagConfigurationMatches(RecipeVersionParameterSnapshot snapshot, Tag tag)
        => tag.DeviceId == snapshot.DeviceId &&
           tag.AddressType == snapshot.AddressType &&
           tag.Address == snapshot.Address &&
           tag.DataType == snapshot.DataType &&
           tag.AccessMode == snapshot.AccessMode &&
           tag.Enabled == snapshot.Enabled &&
           string.Equals(tag.Unit, snapshot.Unit, StringComparison.Ordinal) &&
           tag.Scale == snapshot.Scale &&
           tag.Offset == snapshot.Offset &&
           tag.ByteOrder == snapshot.ByteOrder &&
           tag.WordOrder == snapshot.WordOrder &&
           tag.MinValue == snapshot.MinValue &&
           tag.MaxValue == snapshot.MaxValue;
}
