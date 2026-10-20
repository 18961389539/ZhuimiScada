using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Infrastructure.Persistence;

public sealed class ScadaStore(IDbContextFactory<ScadaDbContext> factory) : IScadaStore
{
    public async Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken ct = default)
    { await using var db = await factory.CreateDbContextAsync(ct); return await db.Devices.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct); }
    public async Task<Device?> GetDeviceAsync(Guid id, CancellationToken ct = default)
    { await using var db = await factory.CreateDbContextAsync(ct); return await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); }
    public async Task SaveDeviceAsync(Device device, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var exists = await db.Devices.AnyAsync(x => x.Id == device.Id, ct);
        if (exists) db.Devices.Update(device); else db.Devices.Add(device);
        db.OperationLogs.Add(Log(device.Name, exists ? "修改设备配置" : "新增设备"));
        if (!exists)
        {
            db.ManualOutputPoints.AddRange(ManualOutputService.DefaultOutputPoints.Select((point, index) =>
                new ManualOutputPoint
                {
                    DeviceId = device.Id,
                    Name = point.Name,
                    SortOrder = index,
                    DefaultPlcAddress = point.PlcAddress,
                    DefaultModbusAddress = point.ModbusAddress
                }));
        }
        await SaveAsync(db, ct);
    }
    public async Task DeleteDeviceAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Recipes.AnyAsync(x => x.DeviceId == id, ct))
            throw new InvalidOperationException("该设备仍被配方引用，请先删除或迁移相关配方。");
        if (await db.Tags.AnyAsync(x => x.DeviceId == id, ct)) throw new InvalidOperationException("该设备仍有关联 Tag，请先删除或转移 Tag。");
        var item = await db.Devices.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new InvalidOperationException("设备不存在。");
        db.Devices.Remove(item); db.OperationLogs.Add(Log(item.Name, "删除设备"));
        foreach (var alarm in await db.AlarmRecords
                     .Where(x => x.Source == $"Device:{id}" && (x.Status == AlarmStatus.Active || x.Status == AlarmStatus.Acknowledged))
                     .ToListAsync(ct))
        {
            alarm.Status = alarm.AcknowledgedAt is null ? AlarmStatus.ClearedUnacknowledged : AlarmStatus.Cleared;
            alarm.ClearedAt = DateTimeOffset.UtcNow;
        }
        await SaveAsync(db, ct);
    }
    public async Task<IReadOnlyList<Tag>> GetTagsAsync(CancellationToken ct = default)
    { await using var db = await factory.CreateDbContextAsync(ct); return await db.Tags.AsNoTracking().Include(x => x.Device).OrderBy(x => x.Name).ToListAsync(ct); }
    public async Task SaveTagAsync(Tag tag, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var exists = await db.Tags.AnyAsync(x => x.Id == tag.Id, ct);
        if (exists) db.Tags.Update(tag); else db.Tags.Add(tag);
        db.OperationLogs.Add(Log(tag.Name, exists ? "修改 Tag 配置" : "新增 Tag"));
        await SaveAsync(db, ct);
    }
    public async Task DeleteTagAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.HmiControls.AnyAsync(x => x.TagId == id, ct)) throw new InvalidOperationException("Tag 已绑定 HMI 控件，请先解除绑定。");
        if (await db.RecipeParameters.AnyAsync(x => x.TagId == id, ct)) throw new InvalidOperationException("Tag 已被配方引用，请先从配方中移除。");
        var tag = await db.Tags.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new InvalidOperationException("Tag 不存在。");
        db.Tags.Remove(tag); db.OperationLogs.Add(Log(tag.Name, "删除 Tag")); await SaveAsync(db, ct);
    }
    public async Task<HmiPage> EnsureHmiPageAsync(
        HmiPage page, IReadOnlyList<HmiControl> initialControls, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.HmiPages.AsNoTracking().FirstOrDefaultAsync(x => x.Name == page.Name, ct);
        if (existing is not null) return existing;

        page.Name = page.Name.Trim();
        db.HmiPages.Add(page);
        foreach (var control in initialControls)
        {
            control.HmiPageId = page.Id;
            control.HmiPage = null;
            control.Tag = null;
            db.HmiControls.Add(control);
        }
        db.OperationLogs.Add(Log("HMI", $"创建画面“{page.Name}”并初始化 {initialControls.Count} 个展示项"));
        await SaveAsync(db, ct);
        await transaction.CommitAsync(ct);
        return page;
    }
    public async Task<HmiPage?> GetHmiPageAsync(string name, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.HmiPages.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name, ct);
    }
    public async Task<IReadOnlyList<HmiControl>> GetHmiControlsAsync(Guid pageId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.HmiControls.AsNoTracking().Include(x => x.Tag).ThenInclude(tag => tag!.Device)
            .Where(x => x.HmiPageId == pageId)
            .OrderBy(x => x.Position).ThenBy(x => x.Name)
            .ToListAsync(ct);
    }
    public async Task<Guid> SaveHmiControlAsync(HmiControl control, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.HmiPages.AnyAsync(x => x.Id == control.HmiPageId, ct))
            throw new InvalidOperationException("HMI 画面不存在，请刷新后重试。");
        var tag = await db.Tags.SingleOrDefaultAsync(x => x.Id == control.TagId, ct);
        if (tag is null || !tag.Enabled || tag.AccessMode != AccessMode.ReadOnly ||
            tag.AddressType is not (AddressType.HoldingRegister or AddressType.InputRegister) ||
            tag.DataType == TagDataType.Bool)
            throw new InvalidOperationException("HMI 仅支持启用的只读数值寄存器 Tag。");
        if (!Enum.TryParse<HmiDisplayType>(control.ControlType, out var displayType) ||
            !Enum.IsDefined(displayType))
            throw new InvalidOperationException("HMI 展示类型无效。");
        if (displayType is HmiDisplayType.Status or HmiDisplayType.TotalResult or HmiDisplayType.Result &&
            tag.DataType is TagDataType.Float32 or TagDataType.Float64)
            throw new InvalidOperationException("状态与判定展示项必须绑定整数寄存器 Tag。");

        var existing = control.Id == Guid.Empty
            ? null
            : await db.HmiControls.SingleOrDefaultAsync(
                x => x.Id == control.Id && x.HmiPageId == control.HmiPageId, ct);
        if (control.Id != Guid.Empty && existing is null)
            throw new InvalidOperationException("HMI 展示项不存在或已被删除。");
        var currentId = existing?.Id ?? Guid.Empty;
        var otherControls = db.HmiControls.Where(x => x.HmiPageId == control.HmiPageId && x.Id != currentId);
        if (await otherControls.AnyAsync(x => x.TagId == control.TagId, ct))
            throw new InvalidOperationException("同一个 Tag 不能在同一 HMI 画面重复添加。");
        if (displayType is HmiDisplayType.Status or HmiDisplayType.TotalResult &&
            await otherControls.AnyAsync(x => x.ControlType == control.ControlType, ct))
            throw new InvalidOperationException("运行状态和总判定在画面中各只能有一个。");

        control.Name = control.Name.Trim();
        if (control.Name.Length is < 1 or > 100)
            throw new InvalidOperationException("展示名称长度必须为 1 到 100 个字符。");
        if (await otherControls.AnyAsync(x => x.Name.ToLower() == control.Name.ToLower(), ct))
            throw new InvalidOperationException("HMI 展示名称不能重复。");

        if (existing is null)
        {
            control.Position = await db.HmiControls.Where(x => x.HmiPageId == control.HmiPageId)
                .Select(x => (int?)x.Position).MaxAsync(ct) is int maxPosition ? maxPosition + 1 : 0;
            control.Tag = null;
            control.HmiPage = null;
            db.HmiControls.Add(control);
        }
        else
        {
            existing.TagId = control.TagId;
            existing.Name = control.Name;
            existing.ControlType = control.ControlType;
            existing.ConfigurationJson = control.ConfigurationJson;
        }
        db.OperationLogs.Add(Log("HMI",
            $"{(existing is null ? "新增" : "修改")}画面“{(await db.HmiPages.SingleAsync(x => x.Id == control.HmiPageId, ct)).Name}”展示项“{control.Name}”"));
        await SaveAsync(db, ct);
        await transaction.CommitAsync(ct);
        return existing?.Id ?? control.Id;
    }
    public async Task DeleteHmiControlAsync(Guid pageId, Guid controlId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var control = await db.HmiControls.SingleOrDefaultAsync(
            x => x.Id == controlId && x.HmiPageId == pageId, ct)
            ?? throw new InvalidOperationException("HMI 展示项不存在或已被删除。");
        db.HmiControls.Remove(control);
        db.OperationLogs.Add(Log("HMI", $"从画面移除展示项“{control.Name}”（底层 Tag 保留）"));
        await SaveAsync(db, ct);
    }
    public async Task<IReadOnlyList<Recipe>> GetRecipesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var recipes = await db.Recipes.AsNoTracking()
            .Include(x => x.Parameters).ThenInclude(x => x.Tag)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        foreach (var recipe in recipes)
            recipe.Parameters = recipe.Parameters
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Tag!.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return recipes;
    }
    public async Task<Recipe?> GetRecipeAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var recipe = await db.Recipes.AsNoTracking()
            .Include(x => x.Parameters).ThenInclude(x => x.Tag)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (recipe is not null)
            recipe.Parameters = recipe.Parameters
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Tag!.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return recipe;
    }
    public async Task<IReadOnlyList<string>> GetRecipeNamesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Recipes.AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => x.Name)
            .ToListAsync(ct);
    }
    public async Task<bool> RecipeNameExistsAsync(
        string name, Guid? exceptId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.Recipes.AsNoTracking().Where(x => x.Name == name);
        if (exceptId is Guid id)
            query = query.Where(x => x.Id != id);
        return await query.AnyAsync(ct);
    }
    public async Task<RecipeVersionPage> GetRecipeVersionsPageAsync(
        Guid recipeId, int pageIndex, int pageSize, CancellationToken ct = default)
    {
        if (pageIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (pageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var currentRevision = await db.Recipes.AsNoTracking()
            .Where(x => x.Id == recipeId)
            .Select(x => (int?)x.Revision)
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("配方不存在或已被删除。");
        var query = db.RecipeVersions.AsNoTracking()
            .Where(x => x.RecipeId == recipeId);
        var totalCount = await query.CountAsync(ct);
        var pageCount = totalCount == 0 ? 0 : (totalCount + pageSize - 1) / pageSize;
        var currentPage = pageCount == 0 ? 0 : Math.Min(pageIndex, pageCount - 1);
        var skip = checked(currentPage * pageSize);
        var versions = await query.OrderByDescending(x => x.Revision)
            .Skip(skip).Take(pageSize).ToListAsync(ct);
        await transaction.CommitAsync(ct);
        return new RecipeVersionPage(currentRevision, currentPage, totalCount, versions);
    }
    public async Task<RecipeVersion?> GetRecipeVersionAsync(
        Guid recipeId, int revision, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.RecipeVersions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RecipeId == recipeId && x.Revision == revision, ct);
    }
    public async Task<RecipeApplyRecordPage> GetRecipeApplyRecordsAsync(
        Guid recipeId, int pageIndex, int pageSize, CancellationToken ct = default)
    {
        if (pageIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (pageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.RecipeApplyRecords.AsNoTracking().Where(x => x.RecipeId == recipeId);
        var totalCount = await query.CountAsync(ct);
        var pageCount = totalCount == 0 ? 0 : (totalCount + pageSize - 1) / pageSize;
        var currentPage = pageCount == 0 ? 0 : Math.Min(pageIndex, pageCount - 1);
        var skip = checked(currentPage * pageSize);
        var records = await query.OrderByDescending(x => x.Id)
            .Skip(skip).Take(pageSize).ToListAsync(ct);
        return new RecipeApplyRecordPage(currentPage, pageSize, totalCount, records);
    }
    public async Task<Guid> SaveRecipeAsync(
        Recipe recipe, IReadOnlyList<RecipeParameter> parameters, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (parameters.Count is < 1 or > 200)
            throw new InvalidOperationException("配方设定项数量必须为 1 到 200 项。");
        if (parameters.Select(x => x.TagId).Distinct().Count() != parameters.Count)
            throw new InvalidOperationException("同一个 Tag 不能在配方中重复添加。");
        if (recipe.DeviceId is not Guid deviceId ||
            !await db.Devices.AnyAsync(x => x.Id == deviceId, ct))
            throw new InvalidOperationException("请选择有效的配方设备。");

        var parameterTagIds = parameters.Select(x => x.TagId).ToArray();
        var parameterDevices = await db.Tags.AsNoTracking()
            .Where(x => parameterTagIds.Contains(x.Id))
            .Select(x => new { x.Id, x.DeviceId })
            .ToDictionaryAsync(x => x.Id, x => x.DeviceId, ct);
        if (parameterDevices.Count != parameters.Count ||
            parameterDevices.Values.Any(id => id != deviceId))
            throw new InvalidOperationException("配方中的所有 Tag 必须属于同一台所选设备。");

        var existing = recipe.Id == Guid.Empty
            ? null
            : await db.Recipes.Include(x => x.Parameters)
                .SingleOrDefaultAsync(x => x.Id == recipe.Id, ct);
        if (recipe.Id != Guid.Empty && existing is null)
            throw new InvalidOperationException("配方不存在或已被删除。");
        if (await db.Recipes.AnyAsync(x => x.Id != recipe.Id && x.Name == recipe.Name, ct))
            throw new InvalidOperationException("配方名称已存在，请使用唯一名称。");
        if (existing is not null && recipe.Revision != existing.Revision)
            throw new InvalidOperationException("配方已在其他窗口中修改，请刷新后再保存。");

        var now = DateTimeOffset.UtcNow;
        // 传入集合的顺序即显示顺序（编辑对话框上移/下移的结果）
        for (var index = 0; index < parameters.Count; index++)
            parameters[index].SortOrder = index;
        var saved = existing ?? new Recipe
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            Revision = 1,
            CreatedAt = now
        };
        if (existing is not null)
            existing.Revision++;
        saved.DeviceId = deviceId;
        saved.Name = recipe.Name;
        saved.Description = recipe.Description;
        saved.ChangeNote = recipe.ChangeNote;
        saved.UpdatedAt = now;

        if (existing is null)
        {
            db.Recipes.Add(saved);
            foreach (var parameter in parameters)
            {
                parameter.RecipeId = saved.Id;
                parameter.Recipe = null;
                parameter.Tag = null;
                db.RecipeParameters.Add(parameter);
            }
        }
        else
        {
            var incomingByTag = parameters.ToDictionary(x => x.TagId);
            var existingByTag = existing.Parameters.ToDictionary(x => x.TagId);
            db.RecipeParameters.RemoveRange(existing.Parameters.Where(x => !incomingByTag.ContainsKey(x.TagId)));
            foreach (var (tagId, parameter) in incomingByTag)
            {
                if (existingByTag.TryGetValue(tagId, out var current))
                {
                    current.EngineeringValue = parameter.EngineeringValue;
                    current.BooleanValue = parameter.BooleanValue;
                    current.SortOrder = parameter.SortOrder;
                    continue;
                }

                parameter.RecipeId = saved.Id;
                parameter.Recipe = null;
                parameter.Tag = null;
                db.RecipeParameters.Add(parameter);
            }
        }

        var deviceSnapshot = await db.Devices.AsNoTracking()
            .SingleAsync(x => x.Id == deviceId, ct);
        var snapshotTags = await db.Tags.AsNoTracking()
            .Where(x => parameterTagIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var parameterSnapshots = parameters.Select(parameter =>
        {
            var tag = snapshotTags[parameter.TagId];
            return new RecipeVersionParameterSnapshot(
                tag.Id,
                tag.Name,
                tag.DeviceId,
                tag.AddressType,
                tag.Address,
                tag.DataType,
                tag.AccessMode,
                tag.Enabled,
                tag.Unit,
                tag.Scale,
                tag.Offset,
                tag.ByteOrder,
                tag.WordOrder,
                tag.MinValue,
                tag.MaxValue,
                parameter.EngineeringValue,
                parameter.BooleanValue,
                parameter.SortOrder);
        }).OrderBy(x => x.SortOrder).ThenBy(x => x.TagName, StringComparer.OrdinalIgnoreCase).ToArray();
        db.RecipeVersions.Add(new RecipeVersion
        {
            RecipeId = saved.Id,
            Revision = saved.Revision,
            CreatedAt = now,
            Name = saved.Name,
            Description = saved.Description,
            ChangeNote = saved.ChangeNote,
            DeviceId = deviceSnapshot.Id,
            DeviceName = deviceSnapshot.Name,
            DeviceProtocol = deviceSnapshot.Protocol,
            DeviceIpAddress = deviceSnapshot.IpAddress,
            DevicePort = deviceSnapshot.Port,
            DeviceUnitId = deviceSnapshot.UnitId,
            SnapshotSchemaVersion = 1,
            ParametersJson = JsonSerializer.Serialize(parameterSnapshots)
        });

        var tagNames = await db.Tags.AsNoTracking()
            .Where(x => parameterTagIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var values = string.Join(", ", parameters.Select(parameter =>
        {
            var value = parameter.BooleanValue is bool booleanValue
                ? booleanValue ? "开" : "关"
                : parameter.EngineeringValue?.ToString("G", CultureInfo.InvariantCulture) ?? "无效";
            return $"{tagNames.GetValueOrDefault(parameter.TagId, parameter.TagId.ToString())}={value}";
        }));
        db.OperationLogs.Add(Log(
            "配方",
            $"{(existing is null ? "新增" : "修改")}配方“{saved.Name}”，设备 {deviceId}，版本 {saved.Revision}：{values}" +
            (string.IsNullOrWhiteSpace(saved.ChangeNote) ? "" : $"；变更说明：{saved.ChangeNote}")));
        try
        {
            await SaveAsync(db, ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new InvalidOperationException("配方刚被其他操作修改，请刷新后重试。", ex);
        }
        await transaction.CommitAsync(ct);
        return saved.Id;
    }
    public async Task DeleteRecipeAsync(Guid recipeId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var recipe = await db.Recipes.SingleOrDefaultAsync(x => x.Id == recipeId, ct)
            ?? throw new InvalidOperationException("配方不存在或已被删除。");
        db.Recipes.Remove(recipe);
        db.OperationLogs.Add(Log("配方", $"删除本地配方“{recipe.Name}”"));
        await SaveAsync(db, ct);
    }
    public async Task RecordRecipeApplyAsync(
        Guid recipeId, int appliedRevision, bool succeeded, string summary, CancellationToken ct = default)
    {
        summary = summary.Trim();
        if (summary.Length > 2000)
            summary = summary[..2000];

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var recipe = await db.Recipes.SingleOrDefaultAsync(x => x.Id == recipeId, ct)
            ?? throw new InvalidOperationException("配方不存在或已被删除。");
        var revisionStillCurrent = recipe.Revision == appliedRevision;
        if (succeeded && revisionStillCurrent)
            recipe.LastAppliedAt = DateTimeOffset.UtcNow;
        db.RecipeApplyRecords.Add(new RecipeApplyRecord
        {
            RecipeId = recipeId,
            RecipeName = recipe.Name,
            DeviceId = recipe.DeviceId,
            Revision = appliedRevision,
            Succeeded = succeeded,
            Summary = summary,
            OccurredAt = DateTimeOffset.UtcNow
        });
        db.OperationLogs.Add(Log(
            "配方",
            $"配方“{recipe.Name}”版本 {appliedRevision} 下发{(succeeded ? "成功" : "失败")}" +
            $"{(succeeded && !revisionStillCurrent ? $"（记录时当前版本已变为 {recipe.Revision}）" : "")}：{summary}",
            succeeded && revisionStillCurrent ? "Information" : "Warning"));
        await SaveAsync(db, ct);
        await transaction.CommitAsync(ct);
    }
    public async Task SetTagQualityAlarmAsync(Guid tagId, string tagName, string? error, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var source = $"TagQuality:{tagId}";
        if (error is null) await ClearAlarmAsync(db, source, ct);
        else await RaiseAlarmAsync(db, source, $"{tagName}: {error}", "Warning", ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AlarmDefinition>> GetAlarmDefinitionsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AlarmDefinitions.AsNoTracking()
            .Include(x => x.Tag)
            .OrderBy(x => x.Tag!.Name).ThenBy(x => x.Name)
            .ToListAsync(ct);
    }

    public async Task SaveAlarmDefinitionAsync(AlarmDefinition definition, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Tags.AllAsync(x => x.Id != definition.TagId, ct))
            throw new InvalidOperationException("报警定义引用的 Tag 不存在。");
        var now = DateTimeOffset.UtcNow;
        var existing = await db.AlarmDefinitions.SingleOrDefaultAsync(x => x.Id == definition.Id, ct);
        if (await db.AlarmDefinitions.AnyAsync(x =>
                x.Id != definition.Id && x.TagId == definition.TagId && x.Name == definition.Name, ct))
            throw new InvalidOperationException("该 Tag 已存在同名报警定义。");
        if (existing is null)
        {
            existing = new() { Id = definition.Id, CreatedAt = now };
            db.AlarmDefinitions.Add(existing);
        }
        existing.TagId = definition.TagId;
        existing.Name = definition.Name;
        existing.Kind = definition.Kind;
        existing.BitTarget = definition.BitTarget;
        existing.EnumValues = definition.EnumValues;
        existing.Severity = definition.Severity;
        existing.Message = definition.Message;
        existing.DelaySeconds = definition.DelaySeconds;
        existing.Enabled = definition.Enabled;
        existing.UpdatedAt = now;
        db.OperationLogs.Add(Log("Alarms", $"保存报警定义“{definition.Name}”"));
        await SaveAsync(db, ct);
    }

    public async Task DeleteAlarmDefinitionAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.AlarmDefinitions.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException("报警定义不存在或已被删除。");
        db.AlarmDefinitions.Remove(existing);
        db.OperationLogs.Add(Log("Alarms", $"删除报警定义“{existing.Name}”"));
        await SaveAsync(db, ct);
    }

    public async Task SetAlarmDefinitionAlarmAsync(
        Guid definitionId, string? message, string severity = "Warning", CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var source = $"AlarmDef:{definitionId}";
        if (message is null)
        {
            await ClearAlarmAsync(db, source, ct);
        }
        else
        {
            await RaiseAlarmAsync(db, source, message, severity, ct);
            // 已在活动（同一状态字取值从 1→2 等）：刷新消息/级别，让文案跟随当前条件
            var active = await db.AlarmRecords
                .Where(x => x.Source == source && (x.Status == AlarmStatus.Active || x.Status == AlarmStatus.Acknowledged))
                .ToListAsync(ct);
            if (active.Count > 0 && (active[0].Message != message || active[0].Severity != severity))
            {
                active[0].Message = message;
                active[0].Severity = severity;
                active[0].Priority = PriorityFromSeverity(severity);
            }
        }
        await db.SaveChangesAsync(ct);
    }
    public async Task<IReadOnlyList<ManualOutputPoint>> GetManualOutputPointsAsync(Guid deviceId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ManualOutputPoints.AsNoTracking().Where(x => x.DeviceId == deviceId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(ct);
    }
    public async Task CreateManualOutputPointAsync(ManualOutputPoint point, CancellationToken ct = default)
    {
        point.Name = NormalizeOutputName(point.Name);
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Devices.AnyAsync(x => x.Id == point.DeviceId, ct))
            throw new InvalidOperationException("目标设备不存在。");
        if (await db.ManualOutputPoints.AnyAsync(x => x.DeviceId == point.DeviceId && x.Name == point.Name, ct))
            throw new InvalidOperationException("该设备已存在同名输出按钮。");
        db.ManualOutputPoints.Add(new ManualOutputPoint
        {
            Id = point.Id,
            DeviceId = point.DeviceId,
            Name = point.Name,
            SortOrder = point.SortOrder
        });
        db.OperationLogs.Add(Log("手动输出", $"设备 {point.DeviceId} 新增按钮“{point.Name}”"));
        await SaveAsync(db, ct);
    }
    public async Task UpdateManualOutputPointAsync(Guid deviceId, Guid pointId, string name, CancellationToken ct = default)
    {
        name = NormalizeOutputName(name);
        await using var db = await factory.CreateDbContextAsync(ct);
        var point = await db.ManualOutputPoints.SingleOrDefaultAsync(
            x => x.DeviceId == deviceId && x.Id == pointId, ct)
            ?? throw new InvalidOperationException("输出按钮不存在或已被删除。");
        if (await db.ManualOutputPoints.AnyAsync(
                x => x.DeviceId == deviceId && x.Id != pointId && x.Name == name, ct))
            throw new InvalidOperationException("该设备已存在同名输出按钮。");
        var oldName = point.Name;
        point.Name = name;
        db.OperationLogs.Add(Log("手动输出", $"设备 {deviceId} 修改按钮“{oldName}”为“{name}”"));
        await SaveAsync(db, ct);
    }
    public async Task UpdateManualOutputPointOrderAsync(
        Guid deviceId, IReadOnlyList<Guid> orderedPointIds, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var points = await db.ManualOutputPoints.Where(x => x.DeviceId == deviceId).ToListAsync(ct);
        if (orderedPointIds.Count != points.Count ||
            orderedPointIds.Distinct().Count() != orderedPointIds.Count ||
            points.Any(point => !orderedPointIds.Contains(point.Id)))
            throw new InvalidOperationException("按钮排序清单与当前按钮不一致，请刷新后重试。");
        for (var index = 0; index < orderedPointIds.Count; index++)
            points.Single(point => point.Id == orderedPointIds[index]).SortOrder = index;
        db.OperationLogs.Add(Log("手动输出", $"设备 {deviceId} 调整输出按钮顺序"));
        await SaveAsync(db, ct);
    }
    public async Task<Guid> SaveManualOutputPointConfigurationAsync(
        Guid deviceId, Guid? pointId, string name, Guid? tagId, CancellationToken ct = default,
        Guid? monitorTagId = null, ManualOutputMode outputMode = ManualOutputMode.Hold, int pulseDurationMs = 300)
    {
        name = NormalizeOutputName(name);
        if (!Enum.IsDefined(outputMode))
            throw new InvalidOperationException("输出模式无效。");
        if (pulseDurationMs is < 50 or > 10000)
            throw new InvalidOperationException("脉冲时长必须为 50 到 10000 毫秒。");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        if (!await db.Devices.AnyAsync(x => x.Id == deviceId, ct))
            throw new InvalidOperationException("目标设备不存在。");

        ManualOutputPoint point;
        var isNew = pointId is null;
        if (pointId is Guid id)
        {
            point = await db.ManualOutputPoints.SingleOrDefaultAsync(
                x => x.DeviceId == deviceId && x.Id == id, ct)
                ?? throw new InvalidOperationException("输出按钮不存在或已被删除。");
        }
        else
        {
            var lastSortOrder = await db.ManualOutputPoints
                .Where(x => x.DeviceId == deviceId)
                .Select(x => (int?)x.SortOrder)
                .MaxAsync(ct) ?? -1;
            point = new ManualOutputPoint
            {
                DeviceId = deviceId,
                Name = name,
                SortOrder = lastSortOrder + 1
            };
            db.ManualOutputPoints.Add(point);
        }

        if (await db.ManualOutputPoints.AnyAsync(
                x => x.DeviceId == deviceId && x.Id != point.Id && x.Name == name, ct))
            throw new InvalidOperationException("该设备已存在同名输出按钮。");

        var deviceTags = await db.Tags.Where(x => x.DeviceId == deviceId).ToListAsync(ct);
        var selectedControlTag = tagId is Guid selectedTagId
            ? deviceTags.SingleOrDefault(x => x.Id == selectedTagId)
                ?? throw new InvalidOperationException("控制 Tag 必须属于当前设备。")
            : null;
        if (selectedControlTag is not null && !IsValidControlTag(selectedControlTag))
            throw new InvalidOperationException("控制 Tag 必须是当前设备上启用的 Bool/Coil/ReadWrite。");

        var selectedMonitorTag = monitorTagId is Guid selectedMonitorTagId
            ? deviceTags.SingleOrDefault(x => x.Id == selectedMonitorTagId)
                ?? throw new InvalidOperationException("状态监控 Tag 必须属于当前设备。")
            : null;
        if (selectedMonitorTag is not null && !IsValidMonitorTag(selectedMonitorTag))
            throw new InvalidOperationException("状态监控 Tag 必须是当前设备上启用的 Bool/Coil 或 Bool/DiscreteInput。");

        var selectedTags = new[] { selectedControlTag, selectedMonitorTag }
            .Where(x => x is not null).Cast<Tag>().ToArray();
        if (selectedTags.GroupBy(x => (x.Id, x.AddressType, x.Address)).Any(x => x.Count() > 1) ||
            selectedTags.GroupBy(x => (x.AddressType, x.Address)).Any(x => x.Count() > 1))
            throw new InvalidOperationException("控制 Tag 与状态监控 Tag 必须使用不同的 Tag 和地址。");

        var otherPoints = await db.ManualOutputPoints
            .Where(x => x.DeviceId == deviceId && x.Id != point.Id)
            .ToListAsync(ct);
        var otherBindings = await db.ManualOutputBindings
            .Where(x => x.DeviceId == deviceId && x.OutputPointId != point.Id)
            .ToDictionaryAsync(x => x.OutputPointId, ct);
        var reservedTags = new List<Tag>();
        foreach (var otherPoint in otherPoints)
        {
            if (otherBindings.TryGetValue(otherPoint.Id, out var savedBinding))
            {
                if (savedBinding.TagId is Guid savedTagId &&
                    deviceTags.SingleOrDefault(x => x.Id == savedTagId) is { } savedControlTag)
                    reservedTags.Add(savedControlTag);
                if (savedBinding.MonitorTagId is Guid savedMonitorId &&
                    deviceTags.SingleOrDefault(x => x.Id == savedMonitorId) is { } savedMonitorTag)
                    reservedTags.Add(savedMonitorTag);
            }
            else if (otherPoint.DefaultModbusAddress is int defaultAddress)
            {
                var defaultTag = deviceTags.FirstOrDefault(x =>
                    string.Equals(x.Name, otherPoint.Name, StringComparison.OrdinalIgnoreCase) &&
                    x.Address == defaultAddress);
                if (defaultTag is not null) reservedTags.Add(defaultTag);
            }
        }
        if (selectedTags.Any(selected => reservedTags.Any(reserved =>
                reserved.Id == selected.Id ||
                reserved.AddressType == selected.AddressType && reserved.Address == selected.Address)))
            throw new InvalidOperationException("控制或监控 Tag/地址已绑定到其他按钮。");

        var oldName = isNew ? null : point.Name;
        point.Name = name;
        point.OutputMode = outputMode;
        point.PulseDurationMs = pulseDurationMs;
        var binding = await db.ManualOutputBindings.SingleOrDefaultAsync(
            x => x.DeviceId == deviceId && x.OutputPointId == point.Id, ct);
        if (binding is null)
        {
            db.ManualOutputBindings.Add(new ManualOutputBinding
            {
                DeviceId = deviceId,
                OutputPointId = point.Id,
                TagId = tagId,
                MonitorTagId = monitorTagId
            });
        }
        else
        {
            binding.TagId = tagId;
            binding.MonitorTagId = monitorTagId;
        }

        var action = isNew ? "新增" : "更新";
        var nameChange = isNew || oldName != name ? $"按钮名称为“{name}”" : $"按钮“{name}”";
        db.OperationLogs.Add(Log("手动输出",
            $"设备 {deviceId} {action}{nameChange}、控制 Tag 与状态监控 Tag 绑定"));
        await SaveAsync(db, ct);
        await transaction.CommitAsync(ct);
        return point.Id;
    }
    public async Task DeleteManualOutputPointAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var point = await db.ManualOutputPoints.SingleOrDefaultAsync(
            x => x.DeviceId == deviceId && x.Id == pointId, ct)
            ?? throw new InvalidOperationException("输出按钮不存在或已被删除。");
        db.ManualOutputPoints.Remove(point);
        db.OperationLogs.Add(Log("手动输出", $"设备 {deviceId} 删除按钮“{point.Name}”"));
        await SaveAsync(db, ct);
    }
    public async Task<IReadOnlyList<ManualOutputBinding>> GetManualOutputBindingsAsync(Guid deviceId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ManualOutputBindings.AsNoTracking()
            .Include(x => x.Tag).Include(x => x.MonitorTag).Include(x => x.OutputPoint)
            .Where(x => x.DeviceId == deviceId).OrderBy(x => x.OutputPoint!.SortOrder).ToListAsync(ct);
    }
    public async Task SaveManualOutputBindingsAsync(Guid deviceId, IReadOnlyList<ManualOutputBinding> bindings, CancellationToken ct = default)
    {
        if (bindings.Any(x => x.DeviceId != deviceId || x.OutputPointId == Guid.Empty))
            throw new InvalidOperationException("按钮绑定配置包含无效设备或输出名称。");
        if (bindings.Select(x => x.OutputPointId).Distinct().Count() != bindings.Count)
            throw new InvalidOperationException("按钮绑定配置中存在重复的输出名称。");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Devices.AnyAsync(x => x.Id == deviceId, ct))
            throw new InvalidOperationException("目标设备不存在。");
        var devicePoints = await db.ManualOutputPoints.Where(x => x.DeviceId == deviceId).ToListAsync(ct);
        if (bindings.Any(binding => devicePoints.All(point => binding.OutputPointId != point.Id)))
            throw new InvalidOperationException("按钮配置已改变，请刷新后重试。");

        var selectedIds = bindings.SelectMany(x => new[] { x.TagId, x.MonitorTagId })
            .Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        if (selectedIds.Distinct().Count() != selectedIds.Length)
            throw new InvalidOperationException("同一个 Tag 不能在多个按钮或控制/监控角色中重复绑定。");
        var deviceTags = await db.Tags.Where(x => x.DeviceId == deviceId).ToListAsync(ct);
        var selectedTags = selectedIds.Select(id => deviceTags.SingleOrDefault(tag => tag.Id == id)
            ?? throw new InvalidOperationException("绑定 Tag 必须属于当前设备。")).ToArray();
        foreach (var binding in bindings)
        {
            if (binding.TagId is Guid controlId &&
                !IsValidControlTag(deviceTags.Single(tag => tag.Id == controlId)))
                throw new InvalidOperationException("控制 Tag 必须是当前设备上启用的 Bool/Coil/ReadWrite。");
            if (binding.MonitorTagId is Guid monitorId &&
                !IsValidMonitorTag(deviceTags.Single(tag => tag.Id == monitorId)))
                throw new InvalidOperationException("状态监控 Tag 必须是当前设备上启用的 Bool/Coil 或 Bool/DiscreteInput。");
        }
        var submittedPointIds = bindings.Select(x => x.OutputPointId).ToHashSet();
        var finalTags = selectedTags.ToList();
        foreach (var point in devicePoints.Where(x => !submittedPointIds.Contains(x.Id)))
        {
            if (point.DefaultModbusAddress is int defaultAddress &&
                deviceTags.FirstOrDefault(tag =>
                    string.Equals(tag.Name, point.Name, StringComparison.OrdinalIgnoreCase) &&
                    tag.Address == defaultAddress) is { } defaultTag)
                finalTags.Add(defaultTag);
        }
        if (finalTags.GroupBy(x => x.Id).Any(x => x.Count() > 1) ||
            finalTags.GroupBy(x => (x.AddressType, x.Address)).Any(x => x.Count() > 1))
            throw new InvalidOperationException("不同按钮或控制/监控角色不能绑定到相同的 Tag 地址。");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.ManualOutputBindings.Where(x => x.DeviceId == deviceId).ToListAsync(ct);
        db.ManualOutputBindings.RemoveRange(existing);
        await db.SaveChangesAsync(ct);

        db.ManualOutputBindings.AddRange(bindings.Select(x => new ManualOutputBinding
        {
            DeviceId = deviceId,
            OutputPointId = x.OutputPointId,
            TagId = x.TagId,
            MonitorTagId = x.MonitorTagId
        }));
        db.OperationLogs.Add(Log("手动输出", $"更新设备 {deviceId} 的按钮 Tag 绑定"));
        await SaveAsync(db, ct);
        await transaction.CommitAsync(ct);
    }
    public async Task SaveManualOutputBindingAsync(Guid deviceId, ManualOutputBinding binding, CancellationToken ct = default)
    {
        if (binding.DeviceId != deviceId || binding.OutputPointId == Guid.Empty)
            throw new InvalidOperationException("按钮绑定配置包含无效设备或输出名称。");

        var point = (await GetManualOutputPointsAsync(deviceId, ct))
            .SingleOrDefault(x => x.Id == binding.OutputPointId)
            ?? throw new InvalidOperationException("输出按钮不存在或已被删除。");
        await SaveManualOutputPointConfigurationAsync(
            deviceId, point.Id, point.Name, binding.TagId, ct, binding.MonitorTagId,
            point.OutputMode, point.PulseDurationMs);
    }
    public async Task<LogQueryResult> QueryLogsAsync(LogQuery query, CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(query.PageSize, 10, 200);
        var page = Math.Max(query.Page, 1);
        await using var db = await factory.CreateDbContextAsync(ct);
        var logs = ApplyLogFilter(db, query);
        var total = await logs.CountAsync(ct);
        var items = await logs.OrderByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return new(items, total, page, pageSize);
    }

    public async Task<IReadOnlyList<OperationLog>> QueryLogsForExportAsync(
        LogQuery query, int cap = 5000, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await ApplyLogFilter(db, query)
            .OrderByDescending(x => x.Id)
            .Take(Math.Clamp(cap, 1, 20_000))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetLogSourcesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.OperationLogs.AsNoTracking()
            .Select(x => x.Source)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);
    }

    private static IQueryable<OperationLog> ApplyLogFilter(ScadaDbContext db, LogQuery query)
    {
        var logs = db.OperationLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            logs = logs.Where(x => x.Source.Contains(term) || x.Message.Contains(term));
        }
        if (query.Levels is { Count: > 0 } levels)
            logs = logs.Where(x => levels.Contains(x.Level));
        if (!string.IsNullOrWhiteSpace(query.Source))
            logs = logs.Where(x => x.Source == query.Source);
        if (query.From is { } from)
            logs = logs.Where(x => x.Timestamp >= from);
        if (query.To is { } to)
            logs = logs.Where(x => x.Timestamp < to);
        return logs;
    }

    public async Task<IReadOnlyList<OperationLog>> GetLogsAsync(CancellationToken ct = default)
    { await using var db = await factory.CreateDbContextAsync(ct); return await db.OperationLogs.AsNoTracking().OrderByDescending(x => x.Id).Take(500).ToListAsync(ct); }
    public async Task LogAsync(string source, string message, string level = "Information", CancellationToken ct = default)
    { await using var db = await factory.CreateDbContextAsync(ct); db.OperationLogs.Add(Log(source, message, level)); await db.SaveChangesAsync(ct); }

    // 数据保留清理：仅删除超龄的普通日志、已解除报警与（可选）配方下发记录；
    // 未解除/已确认未解除的报警一律保留；清理动作本身写一条审计。
    public async Task<RetentionCleanupResult> CleanupHistoryAsync(DataRetentionOptions options, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var logsDeleted = 0;
        var alarmsDeleted = 0;
        var appliesDeleted = 0;
        var snDeleted = 0;
        await using var db = await factory.CreateDbContextAsync(ct);
        if (options.OperationLogRetentionDays > 0)
        {
            var cutoff = now.AddDays(-options.OperationLogRetentionDays);
            logsDeleted = await db.OperationLogs.Where(x => x.Timestamp < cutoff).ExecuteDeleteAsync(ct);
        }
        if (options.ClearedAlarmRetentionDays > 0)
        {
            var cutoff = now.AddDays(-options.ClearedAlarmRetentionDays);
            alarmsDeleted = await db.AlarmRecords
                .Where(x => x.Status == AlarmStatus.Cleared && x.OccurredAt < cutoff)
                .ExecuteDeleteAsync(ct);
        }
        if (options.RecipeApplyRecordRetentionDays > 0)
        {
            var cutoff = now.AddDays(-options.RecipeApplyRecordRetentionDays);
            appliesDeleted = await db.RecipeApplyRecords
                .Where(x => x.OccurredAt < cutoff)
                .ExecuteDeleteAsync(ct);
        }
        if (options.SnRecordRetentionDays > 0)
        {
            var cutoff = now.AddDays(-options.SnRecordRetentionDays);
            snDeleted = await db.SnRecords.Where(x => x.CapturedAt < cutoff).ExecuteDeleteAsync(ct);
        }
        if (logsDeleted + alarmsDeleted + appliesDeleted + snDeleted > 0)
        {
            db.OperationLogs.Add(Log("System",
                $"数据保留清理：日志 {logsDeleted} 条、已解除报警 {alarmsDeleted} 条、配方下发记录 {appliesDeleted} 条、SN 记录 {snDeleted} 条" +
                $"（策略：日志 {options.OperationLogRetentionDays} 天 / 报警 {options.ClearedAlarmRetentionDays} 天 / 下发记录 {options.RecipeApplyRecordRetentionDays} 天 / SN {options.SnRecordRetentionDays} 天）"));
            await db.SaveChangesAsync(ct);
        }
        return new(logsDeleted, alarmsDeleted, appliesDeleted, snDeleted);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ScadaSettings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value, ct);
    }

    public async Task SaveSettingsAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        if (values.Count == 0) return;
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.ScadaSettings.ToListAsync(ct);
        foreach (var (key, value) in values)
        {
            var row = existing.FirstOrDefault(x => x.Key == key);
            if (row is null) db.ScadaSettings.Add(new() { Key = key, Value = value });
            else row.Value = value;
        }
        db.OperationLogs.Add(Log("System", $"更新系统设置：{string.Join("，", values.Select(x => $"{x.Key}={x.Value}"))}"));
        await SaveAsync(db, ct);
    }

    // 保留策略以数据库为准，缺省/非法值回退到注入的默认配置。
    public async Task<DataRetentionOptions> GetRetentionOptionsAsync(
        DataRetentionOptions fallback, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        int Read(string key, int defaultValue)
            => settings.TryGetValue(key, out var text) && int.TryParse(text, out var value) && value is >= 0 and <= 3650
                ? value
                : defaultValue;
        return new DataRetentionOptions
        {
            OperationLogRetentionDays = Read(RetentionOperationLogDaysKey, fallback.OperationLogRetentionDays),
            ClearedAlarmRetentionDays = Read(RetentionClearedAlarmDaysKey, fallback.ClearedAlarmRetentionDays),
            RecipeApplyRecordRetentionDays = Read(RetentionRecipeApplyDaysKey, fallback.RecipeApplyRecordRetentionDays),
            SnRecordRetentionDays = Read(RetentionSnRecordDaysKey, fallback.SnRecordRetentionDays),
            CheckInterval = fallback.CheckInterval
        };
    }

    public async Task SaveRetentionOptionsAsync(DataRetentionOptions options, CancellationToken ct = default)
    {
        foreach (var (label, days) in new[]
                 {
                     ("操作日志", options.OperationLogRetentionDays),
                     ("已解除报警", options.ClearedAlarmRetentionDays),
                     ("配方下发记录", options.RecipeApplyRecordRetentionDays),
                     ("SN 追溯记录", options.SnRecordRetentionDays)
                 })
        {
            if (days is < 0 or > 3650)
                throw new InvalidOperationException($"“{label}”保留天数必须为 0 到 3650（0 = 永久保留）。");
        }
        await SaveSettingsAsync(new Dictionary<string, string>
        {
            [RetentionOperationLogDaysKey] = options.OperationLogRetentionDays.ToString(),
            [RetentionClearedAlarmDaysKey] = options.ClearedAlarmRetentionDays.ToString(),
            [RetentionRecipeApplyDaysKey] = options.RecipeApplyRecordRetentionDays.ToString(),
            [RetentionSnRecordDaysKey] = options.SnRecordRetentionDays.ToString()
        }, ct);
    }

    private const string TagScanIntervalKey = "Polling.TagScanIntervalMs";
    private const string QualityAlarmFailuresKey = "Alarms.QualityAlarmFailures";
    private const string RunningStateTagIdKey = "Monitoring.RunningStateTagId";
    private const string LegacyRunningStateElementKey = "Monitoring.RunningStateElement";
    private const string SnTagIdKey = "Trace.SnTagId";
    private const string RetentionSnRecordDaysKey = "Retention.SnRecordDays";
    private const string RetentionOperationLogDaysKey = "Retention.OperationLogDays";
    private const string RetentionClearedAlarmDaysKey = "Retention.ClearedAlarmDays";
    private const string RetentionRecipeApplyDaysKey = "Retention.RecipeApplyDays";
    // 全局 Tag 扫描间隔：单个 Tag 扫描间隔 = 0 表示跟随该全局值；非法/脏数据回退默认。
    public async Task<int> GetTagScanIntervalAsync(int fallbackMilliseconds, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        return settings.TryGetValue(TagScanIntervalKey, out var text) &&
               int.TryParse(text, out var value) && value is >= 250 and <= 60000
            ? value
            : fallbackMilliseconds;
    }

    public async Task SaveTagScanIntervalAsync(int milliseconds, CancellationToken ct = default)
    {
        if (milliseconds is < 250 or > 60000)
            throw new InvalidOperationException("全局 Tag 扫描间隔必须为 250 到 60000 毫秒。");
        await SaveSettingsAsync(new Dictionary<string, string>
        {
            [TagScanIntervalKey] = milliseconds.ToString()
        }, ct);
    }

    // 全局质量报警阈值：连续读取失败达到该次数生成质量报警（0 = 关闭）。
    public async Task<int> GetQualityAlarmFailuresAsync(int fallback, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        return settings.TryGetValue(QualityAlarmFailuresKey, out var text) &&
               int.TryParse(text, out var value) && value is >= 0 and <= 1000
            ? value
            : fallback;
    }

    public async Task SaveQualityAlarmFailuresAsync(int failures, CancellationToken ct = default)
    {
        if (failures is < 0 or > 1000)
            throw new InvalidOperationException("质量报警阈值必须为 0 到 1000（0 = 关闭）。");
        await SaveSettingsAsync(new Dictionary<string, string>
        {
            [QualityAlarmFailuresKey] = failures.ToString()
        }, ct);
    }

    // 运行状态监控：从全部 Tag 中任选一个；Bool=true 或数值=1 视为运行中，其他值视为已停止。
    // 未配置（且无历史设置）时回退到名为「运行状态监控」的 Tag；显式保存空值表示不启用。
    // 同一 SN 的历史统计（出现次数 / 首次 / 最近），用于复测提示。
    public async Task<(int Total, DateTimeOffset? First, DateTimeOffset? Last)> GetSnStatsAsync(
        string sn, CancellationToken ct = default)
    {
        var trimmed = (sn ?? "").Trim();
        if (trimmed.Length == 0) return (0, null, null);
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = db.SnRecords.AsNoTracking().Where(x => x.Sn == trimmed);
        var total = await rows.CountAsync(ct);
        if (total == 0) return (0, null, null);
        var first = await rows.MinAsync(x => x.CapturedAt, ct);
        var last = await rows.MaxAsync(x => x.CapturedAt, ct);
        return (total, first, last);
    }

    // 批量统计：给定 SN 集合在记录中的出现次数（列表标记“重复”）。
    public async Task<IReadOnlyDictionary<string, int>> GetSnCountsAsync(
        IReadOnlyCollection<string> sns, CancellationToken ct = default)
    {
        var values = sns.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        if (values.Length == 0) return new Dictionary<string, int>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.SnRecords.AsNoTracking()
            .Where(x => values.Contains(x.Sn))
            .GroupBy(x => x.Sn)
            .Select(group => new { Sn = group.Key, Count = group.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.Sn, x => x.Count);
    }

    // 每条规则的最近触发时间（规则列表展示）。
    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetSnapshotLastTimesAsync(
        IReadOnlyCollection<Guid> ruleIds, CancellationToken ct = default)
    {
        if (ruleIds.Count == 0) return new Dictionary<Guid, DateTimeOffset>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = ruleIds.ToArray();
        var rows = await db.SnapshotRecords.AsNoTracking()
            .Where(x => x.RuleId != null && ids.Contains(x.RuleId.Value))
            .GroupBy(x => x.RuleId!.Value)
            .Select(group => new { Id = group.Key, Last = group.Max(x => x.OccurredAt) })
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.Id, x => x.Last);
    }

    public async Task<IReadOnlyList<SnapshotRecord>> QuerySnapshotRecordsForExportAsync(
        string? search = null, DateTimeOffset? from = null, int cap = 20000,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = FilterSnapshots(db.SnapshotRecords.AsNoTracking(), search, from);
        return await query.OrderByDescending(x => x.OccurredAt)
            .Take(Math.Clamp(cap, 1, 50_000)).ToListAsync(ct);
    }

    private static IQueryable<SnapshotRecord> FilterSnapshots(
        IQueryable<SnapshotRecord> query, string? search, DateTimeOffset? from)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.RuleName.Contains(term) || (x.Sn != null && x.Sn.Contains(term)));
        }
        if (from is { } cutoff)
            query = query.Where(x => x.OccurredAt >= cutoff);
        return query;
    }

    public async Task<Guid?> GetRunningStateTagIdAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        if (settings.TryGetValue(RunningStateTagIdKey, out var text))
        {
            return Guid.TryParse(text, out var configured) &&
                   await db.Tags.AnyAsync(x => x.Id == configured, ct)
                ? configured
                : null;
        }
        if (settings.ContainsKey(LegacyRunningStateElementKey))
            return null;
        return await db.Tags.Where(x => x.Name == MonitoringDefaults.RunningStateTagName)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
    }

    // SN 追溯来源：String 类型 Tag；未配置时回退到名为「扫码内容」的 Tag。
    public async Task<Guid?> GetSnTagIdAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        if (settings.TryGetValue(SnTagIdKey, out var text))
        {
            return Guid.TryParse(text, out var configured) &&
                   await db.Tags.AnyAsync(x => x.Id == configured && x.DataType == TagDataType.String, ct)
                ? configured
                : null;
        }
        return await db.Tags
            .Where(x => x.Name == MonitoringDefaults.SnTagName && x.DataType == TagDataType.String)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
    }

    public async Task SetSnTagIdAsync(Guid? tagId, CancellationToken ct = default)
    {
        if (tagId is Guid id)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var tag = await db.Tags.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new InvalidOperationException("所选 Tag 不存在或已被删除。");
            if (tag.DataType != TagDataType.String)
                throw new InvalidOperationException("SN 来源只能是 String 类型的 Tag。");
        }
        await SaveSettingsAsync(new Dictionary<string, string>
        {
            [SnTagIdKey] = tagId?.ToString() ?? ""
        }, ct);
    }

    public async Task<SnRecord> AddSnRecordAsync(
        string sn, string source = "PLC", string? deviceName = null,
        string? recipeName = null, int? recipeVersion = null, string? note = null,
        CancellationToken ct = default)
    {
        var trimmed = (sn ?? "").Trim();
        if (trimmed.Length is < 1 or > 200)
            throw new InvalidOperationException("SN 长度必须为 1 到 200 个字符。");
        await using var db = await factory.CreateDbContextAsync(ct);
        var record = new SnRecord
        {
            Sn = trimmed,
            Source = source,
            CapturedAt = DateTimeOffset.UtcNow,
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim(),
            RecipeName = string.IsNullOrWhiteSpace(recipeName) ? null : recipeName.Trim(),
            RecipeVersion = recipeVersion,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };
        db.SnRecords.Add(record);
        await db.SaveChangesAsync(ct);
        return record;
    }

    public async Task<SnRecord?> GetLatestSnRecordBySnAsync(string sn, CancellationToken ct = default)
    {
        var trimmed = (sn ?? "").Trim();
        if (trimmed.Length == 0) return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.SnRecords.AsNoTracking()
            .Where(x => x.Sn == trimmed)
            .OrderByDescending(x => x.CapturedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<SnapshotRecord>> GetSnapshotsBySnRecordAsync(
        Guid snRecordId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.SnapshotRecords.AsNoTracking()
            .Where(x => x.SnRecordId == snRecordId)
            .OrderByDescending(x => x.OccurredAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetSnapshotCountsAsync(
        IReadOnlyCollection<Guid> snRecordIds, CancellationToken ct = default)
    {
        if (snRecordIds.Count == 0) return new Dictionary<Guid, int>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = snRecordIds.ToArray();
        var rows = await db.SnapshotRecords.AsNoTracking()
            .Where(x => x.SnRecordId != null && ids.Contains(x.SnRecordId.Value))
            .GroupBy(x => x.SnRecordId!.Value)
            .Select(group => new { Id = group.Key, Count = group.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.Id, x => x.Count);
    }

    // 当时生效的配方：取最近一次下发成功的配方（无下发则为空）。
    public async Task<(string? Name, int? Version)> GetActiveRecipeAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var recipe = await db.Recipes.AsNoTracking()
            .Where(x => x.LastAppliedAt != null)
            .OrderByDescending(x => x.LastAppliedAt)
            .Select(x => new { x.Name, x.Revision })
            .FirstOrDefaultAsync(ct);
        return recipe is null ? (null, null) : (recipe.Name, recipe.Revision);
    }

    public async Task<SnRecordPage> QuerySnRecordsAsync(
        string? search, int page, int pageSize, CancellationToken ct = default)
    {
        var size = Math.Clamp(pageSize, 10, 200);
        var current = Math.Max(page, 1);
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.SnRecords.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Sn.Contains(term));
        }
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CapturedAt)
            .Skip((current - 1) * size).Take(size).ToListAsync(ct);
        return new(items, total, current, size);
    }

    public async Task<IReadOnlyList<SnRecord>> QuerySnRecordsForExportAsync(
        string? search, int cap = 10000, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.SnRecords.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Sn.Contains(term));
        }
        return await query.OrderByDescending(x => x.CapturedAt)
            .Take(Math.Clamp(cap, 1, 50_000)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SnapshotRule>> GetSnapshotRulesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.SnapshotRules.AsNoTracking()
            .Include(x => x.TriggerTag)
            .Include(x => x.Points.OrderBy(p => p.SortOrder)).ThenInclude(x => x.Tag)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
    }

    // 触发快照规则：触发源必须是 Coil 上的 Bool（读取后可复位为 False），至少一个抓取点位。
    public async Task SaveSnapshotRuleAsync(
        SnapshotRule rule, IReadOnlyList<SnapshotPointInput> points, CancellationToken ct = default)
    {
        var name = (rule.Name ?? "").Trim();
        if (name.Length is < 1 or > 80)
            throw new InvalidOperationException("规则名称长度必须为 1 到 80 个字符。");
        if (points.Count == 0)
            throw new InvalidOperationException("请至少选择一个抓取点位。");
        var tagIds = points.Select(x => x.TagId).Distinct().ToArray();
        foreach (var item in points)
        {
            if (item.Decimals is < 0 or > 6)
                throw new InvalidOperationException("小数位必须为 0 到 6 位。");
            if ((item.Alias ?? "").Trim().Length > 40)
                throw new InvalidOperationException("采集项别名不能超过 40 个字符。");
            if ((item.Unit ?? "").Trim().Length > 20)
                throw new InvalidOperationException("采集项单位不能超过 20 个字符。");
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var trigger = await db.Tags.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rule.TriggerTagId, ct)
            ?? throw new InvalidOperationException("触发源 Tag 不存在或已被删除。");
        if (trigger.DataType != TagDataType.Bool || trigger.AddressType != AddressType.Coil)
            throw new InvalidOperationException("触发源必须是线圈（Coil）上的 Bool 点位，才能读取后复位为 False。");
        var validTags = await db.Tags.Where(x => tagIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
        if (validTags.Count != tagIds.Length)
            throw new InvalidOperationException("抓取点位中包含不存在的 Tag。");
        if (await db.SnapshotRules.AnyAsync(x => x.Id != rule.Id && x.Name == name, ct))
            throw new InvalidOperationException("已存在同名快照规则。");

        var now = DateTimeOffset.UtcNow;
        var existing = await db.SnapshotRules.Include(x => x.Points)
            .SingleOrDefaultAsync(x => x.Id == rule.Id, ct);
        if (existing is null)
        {
            existing = new SnapshotRule { Id = rule.Id, CreatedAt = now };
            db.SnapshotRules.Add(existing);
        }
        existing.Name = name;
        existing.TriggerTagId = rule.TriggerTagId;
        existing.Enabled = rule.Enabled;
        existing.UpdatedAt = now;
        foreach (var point in existing.Points.ToArray())
            db.SnapshotRulePoints.Remove(point);
        // 采集项顺序即用户排序：按传入次序写 SortOrder（同名点位保留首次出现的口径）。
        var order = 0;
        foreach (var item in points.GroupBy(x => x.TagId).Select(x => x.First()))
        {
            db.SnapshotRulePoints.Add(new SnapshotRulePoint
            {
                RuleId = existing.Id,
                TagId = item.TagId,
                Alias = Normalize(item.Alias),
                Unit = Normalize(item.Unit),
                Decimals = item.Decimals,
                Required = item.Required,
                SortOrder = order++
            });
        }
        db.OperationLogs.Add(Log("Trace", $"保存触发快照规则“{name}”（触发源 {trigger.Name}，点位 {tagIds.Length} 个）"));
        await SaveAsync(db, ct);
    }

    // 单规则语义：系统只保留一条触发快照规则；保存时更新该条并删除任何多余规则。
    public async Task SaveSingleSnapshotRuleAsync(
        SnapshotRule rule, IReadOnlyList<SnapshotPointInput> points, CancellationToken ct = default)
    {
        var rules = await GetSnapshotRulesAsync(ct);
        var keep = rules.OrderBy(x => x.CreatedAt).FirstOrDefault();
        foreach (var extra in rules.Where(x => keep is null || x.Id != keep.Id))
            await DeleteSnapshotRuleAsync(extra.Id, ct);
        await SaveSnapshotRuleAsync(new SnapshotRule
        {
            Id = keep?.Id ?? rule.Id,
            Name = string.IsNullOrWhiteSpace(rule.Name) ? "触发快照" : rule.Name,
            TriggerTagId = rule.TriggerTagId,
            Enabled = rule.Enabled
        }, points, ct);
    }

    public async Task DeleteSnapshotRuleAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.SnapshotRules.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException("快照规则不存在或已被删除。");
        db.SnapshotRules.Remove(existing);
        db.OperationLogs.Add(Log("Trace", $"删除触发快照规则“{existing.Name}”"));
        await SaveAsync(db, ct);
    }

    public async Task AddSnapshotRecordAsync(SnapshotRecord record, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.SnapshotRecords.Add(record);
        await db.SaveChangesAsync(ct);
    }

    public async Task<SnapshotRecordPage> QuerySnapshotRecordsAsync(
        int page, int pageSize, string? search = null, DateTimeOffset? from = null,
        CancellationToken ct = default)
    {
        var size = Math.Clamp(pageSize, 10, 200);
        var current = Math.Max(page, 1);
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = FilterSnapshots(db.SnapshotRecords.AsNoTracking(), search, from);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.OccurredAt)
            .Skip((current - 1) * size).Take(size).ToListAsync(ct);
        return new(items, total, current, size);
    }

    public async Task SetRunningStateTagIdAsync(Guid? tagId, CancellationToken ct = default)
    {
        if (tagId is Guid id)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var tag = await db.Tags.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new InvalidOperationException("所选 Tag 不存在或已被删除。");
            if (tag.DataType != TagDataType.Bool)
                throw new InvalidOperationException("运行状态监控只能选择布尔（Bool）类型 Tag。");
        }
        await SaveSettingsAsync(new Dictionary<string, string>
        {
            [RunningStateTagIdKey] = tagId?.ToString() ?? ""
        }, ct);
    }

    public async Task<IReadOnlyList<AlarmRecord>> GetAlarmsAsync(CancellationToken ct = default)
    { await using var db = await factory.CreateDbContextAsync(ct); return await db.AlarmRecords.AsNoTracking().OrderByDescending(x => x.OccurredAt).Take(500).ToListAsync(ct); }
    public async Task SetCommunicationAlarmAsync(Guid deviceId, string name, string? error, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var source = $"Device:{deviceId}";
        if (error is null) await ClearAlarmAsync(db, source, ct);
        else await RaiseAlarmAsync(db, source, $"{name}: {error}", "Error", ct);
        await db.SaveChangesAsync(ct);
    }
    public async Task SetTagAlarmAsync(Guid tagId, string tagName, string? error, string? condition = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // 高限/低限是不同报警条件，来源需区分（避免泛滥合并把两个条件并成一条）
        var source = string.IsNullOrWhiteSpace(condition) ? $"Tag:{tagId}" : $"Tag:{tagId}:{condition}";
        if (error is null) await ClearAlarmAsync(db, source, ct);
        else await RaiseAlarmAsync(db, source, $"{tagName}: {error}", "Warning", ct);
        await db.SaveChangesAsync(ct);
    }
    public async Task AcknowledgeAlarmAsync(Guid id, string? note = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var alarm = await db.AlarmRecords.SingleOrDefaultAsync(x => x.AlarmId == id, ct)
            ?? throw new InvalidOperationException("报警不存在或已被清理。");
        if (alarm.Status is not (AlarmStatus.Active or AlarmStatus.ClearedUnacknowledged))
            throw new InvalidOperationException("该报警已确认，无需重复确认。");
        var trimmedNote = (note ?? "").Trim();
        if (trimmedNote.Length > 200) trimmedNote = trimmedNote[..200];
        alarm.AcknowledgedAt = DateTimeOffset.UtcNow;
        alarm.Status = alarm.Status == AlarmStatus.Active ? AlarmStatus.Acknowledged : AlarmStatus.Cleared;
        alarm.AcknowledgeNote = trimmedNote.Length == 0 ? null : trimmedNote;
        db.OperationLogs.Add(Log("Alarms",
            $"确认报警“{alarm.Message}”" + (alarm.AcknowledgeNote is null ? "" : $"，备注：{alarm.AcknowledgeNote}")));
        await SaveAsync(db, ct);
    }

    // 搁置：临时抑制某条活动报警（1–1440 分钟，需理由），到期自动失效；全程审计。
    public async Task ShelveAlarmAsync(Guid id, int minutes, string reason, CancellationToken ct = default)
    {
        if (minutes is < 1 or > 1440)
            throw new InvalidOperationException("搁置时长必须为 1 到 1440 分钟。");
        var trimmedReason = (reason ?? "").Trim();
        if (trimmedReason.Length is < 1 or > 200)
            throw new InvalidOperationException("搁置理由长度必须为 1 到 200 个字符。");
        await using var db = await factory.CreateDbContextAsync(ct);
        var alarm = await db.AlarmRecords.SingleOrDefaultAsync(x => x.AlarmId == id, ct)
            ?? throw new InvalidOperationException("报警不存在或已被清理。");
        if (alarm.Status is not (AlarmStatus.Active or AlarmStatus.Acknowledged))
            throw new InvalidOperationException("只有未解除的报警才能搁置。");
        alarm.ShelvedUntil = DateTimeOffset.UtcNow.AddMinutes(minutes);
        alarm.ShelveReason = trimmedReason;
        db.OperationLogs.Add(Log("Alarms",
            $"搁置报警“{alarm.Message}”{minutes} 分钟，理由：{trimmedReason}"));
        await SaveAsync(db, ct);
    }

    public async Task UnShelveAlarmAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var alarm = await db.AlarmRecords.SingleOrDefaultAsync(x => x.AlarmId == id, ct)
            ?? throw new InvalidOperationException("报警不存在或已被清理。");
        if (alarm.ShelvedUntil is null)
            return;
        alarm.ShelvedUntil = null;
        alarm.ShelveReason = null;
        db.OperationLogs.Add(Log("Alarms", $"取消搁置报警“{alarm.Message}”"));
        await SaveAsync(db, ct);
    }

    public async Task<IReadOnlyList<AlarmSuppression>> GetSuppressionsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AlarmSuppressions.AsNoTracking().Include(x => x.Device)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
    }

    // 工况抑制：按设备临时拦截新报警（1–1440 分钟，需理由），到期自动失效；全程审计。
    public async Task AddSuppressionAsync(Guid deviceId, int minutes, string reason, CancellationToken ct = default)
    {
        if (minutes is < 1 or > 1440)
            throw new InvalidOperationException("抑制时长必须为 1 到 1440 分钟。");
        var trimmedReason = (reason ?? "").Trim();
        if (trimmedReason.Length is < 1 or > 200)
            throw new InvalidOperationException("抑制理由长度必须为 1 到 200 个字符。");
        await using var db = await factory.CreateDbContextAsync(ct);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId, ct)
            ?? throw new InvalidOperationException("设备不存在或已被删除。");
        var now = DateTimeOffset.UtcNow;
        var existing = await db.AlarmSuppressions
            .Where(x => x.DeviceId == deviceId && x.Until > now)
            .ToListAsync(ct);
        foreach (var item in existing)
            item.Until = now.AddMinutes(minutes);
        if (existing.Count == 0)
            db.AlarmSuppressions.Add(new AlarmSuppression
            {
                DeviceId = deviceId,
                Reason = trimmedReason,
                Until = now.AddMinutes(minutes),
                CreatedAt = now
            });
        db.OperationLogs.Add(Log("Alarms",
            $"抑制设备“{device.Name}”的报警 {minutes} 分钟，理由：{trimmedReason}"));
        await SaveAsync(db, ct);
    }

    public async Task RemoveSuppressionAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var suppression = await db.AlarmSuppressions.Include(x => x.Device)
            .SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException("报警抑制不存在或已被删除。");
        db.AlarmSuppressions.Remove(suppression);
        db.OperationLogs.Add(Log("Alarms", $"解除设备“{suppression.Device?.Name ?? suppression.DeviceId.ToString()}”的报警抑制"));
        await SaveAsync(db, ct);
    }

    public async Task<AlarmBadgeSummary> GetAlarmBadgeSummaryAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var active = db.AlarmRecords.AsNoTracking()
            .Where(x => x.Status == AlarmStatus.Active &&
                        (x.ShelvedUntil == null || x.ShelvedUntil <= now));
        var unacknowledged = await active.CountAsync(ct);
        var p1 = await active.CountAsync(x => x.Priority == AlarmPriority.P1, ct);
        var rtn = await db.AlarmRecords.CountAsync(x => x.Status == AlarmStatus.ClearedUnacknowledged, ct);
        return new(unacknowledged, p1, rtn);
    }

    public async Task<AlarmQueryResult> QueryAlarmsAsync(AlarmQuery query, CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(query.PageSize, 10, 200);
        var page = Math.Max(query.Page, 1);
        await using var db = await factory.CreateDbContextAsync(ct);
        var filtered = ApplyAlarmFilter(db, query);
        var total = await filtered.CountAsync(ct);
        var ordered = query.SortByTime
            ? filtered.OrderByDescending(x => x.OccurredAt)
            : filtered.OrderBy(x => x.Priority).ThenByDescending(x => x.OccurredAt);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(items, total, page, pageSize);
    }

    public async Task<IReadOnlyList<AlarmRecord>> QueryAlarmsForExportAsync(
        AlarmQuery query, int cap = 5000, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var filtered = ApplyAlarmFilter(db, query);
        var ordered = query.SortByTime
            ? filtered.OrderByDescending(x => x.OccurredAt)
            : filtered.OrderBy(x => x.Priority).ThenByDescending(x => x.OccurredAt);
        return await ordered.Take(Math.Clamp(cap, 1, 20_000)).ToListAsync(ct);
    }

    private static IQueryable<AlarmRecord> ApplyAlarmFilter(ScadaDbContext db, AlarmQuery query)
    {
        var now = DateTimeOffset.UtcNow;
        var records = db.AlarmRecords.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            records = records.Where(x => x.Message.Contains(term));
        }
        if (query.ActiveOnly)
            records = records.Where(x => x.Status == AlarmStatus.Active || x.Status == AlarmStatus.Acknowledged);
        if (query.Statuses is { Count: > 0 } statuses)
            records = records.Where(x => statuses.Contains(x.Status));
        if (query.Priorities is { Count: > 0 } priorities)
            records = records.Where(x => priorities.Contains(x.Priority));
        if (!string.IsNullOrWhiteSpace(query.SourcePrefix))
            records = records.Where(x => x.Source.StartsWith(query.SourcePrefix));
        if (query.Sources is { Count: > 0 } sources)
            records = records.Where(x => sources.Contains(x.Source));
        if (query.ShelvedOnly)
            records = records.Where(x =>
                (x.Status == AlarmStatus.Active || x.Status == AlarmStatus.Acknowledged) &&
                x.ShelvedUntil != null && x.ShelvedUntil > now);
        if (query.StandingOnly)
            records = records.Where(x =>
                (x.Status == AlarmStatus.Active || x.Status == AlarmStatus.Acknowledged) &&
                x.OccurredAt < now.AddHours(-24));
        return records;
    }

    // 报警 KPI：区间流量 + 当前快照 + Top10 来源 + 常驻报警（>24 小时未解除）。
    public async Task<AlarmStatistics> GetAlarmStatisticsAsync(
        DateTimeOffset from, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var range = db.AlarmRecords.AsNoTracking().Where(x => x.OccurredAt >= from);
        var rangeTotal = await range.CountAsync(ct);
        var ackedRows = await range
            .Where(x => x.AcknowledgedAt != null)
            .Select(x => new { x.OccurredAt, x.AcknowledgedAt })
            .Take(5000)
            .ToListAsync(ct);
        double? averageAckSeconds = ackedRows.Count == 0
            ? null
            : ackedRows.Average(x => (x.AcknowledgedAt!.Value - x.OccurredAt).TotalSeconds);
        var topSources = (await range
                .GroupBy(x => x.Source)
                .Select(group => new { Source = group.Key, Count = group.Count() })
                .OrderByDescending(x => x.Count)
                .Take(10)
                .ToListAsync(ct))
            .Select(x => new AlarmSourceStat(x.Source, x.Count))
            .ToArray();

        var open = db.AlarmRecords.AsNoTracking()
            .Where(x => x.Status == AlarmStatus.Active || x.Status == AlarmStatus.Acknowledged);
        var activeCount = await open.CountAsync(ct);
        var unacknowledged = await open.CountAsync(x => x.Status == AlarmStatus.Active, ct);
        var clearedUnacknowledged = await db.AlarmRecords
            .CountAsync(x => x.Status == AlarmStatus.ClearedUnacknowledged, ct);
        var shelvedActive = await open
            .CountAsync(x => x.ShelvedUntil != null && x.ShelvedUntil > now, ct);
        var standingCutoff = now.AddHours(-24);
        var standingCount = await open.CountAsync(x => x.OccurredAt < standingCutoff, ct);
        var standingAlarms = await open
            .Where(x => x.OccurredAt < standingCutoff)
            .OrderBy(x => x.OccurredAt)
            .Take(10)
            .ToListAsync(ct);

        return new(from, rangeTotal, ackedRows.Count, averageAckSeconds, activeCount,
            unacknowledged, clearedUnacknowledged, shelvedActive, standingCount, topSources, standingAlarms);
    }

    // 报警写入统一入口：活动去重；60 秒窗口内同源「恢复→再触发」合并为同一条（RepeatCount++）；
    // 优先级按严重度映射。恢复时未确认过 → ClearedUnacknowledged（仍待确认）。
    private static async Task RaiseAlarmAsync(
        ScadaDbContext db, string source, string message, string severity, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // 工况抑制：该来源所属设备处于抑制期内 → 不产生新报警（抑制记录本身即审计）
        if (await ResolveDeviceIdAsync(db, source, ct) is Guid suppressedDevice &&
            await db.AlarmSuppressions.AnyAsync(
                x => x.DeviceId == suppressedDevice && x.Until > now, ct))
            return;
        var active = await db.AlarmRecords
            .Where(x => x.Source == source && (x.Status == AlarmStatus.Active || x.Status == AlarmStatus.Acknowledged))
            .ToListAsync(ct);
        if (active.Count > 0) return;
        var mergeable = await db.AlarmRecords
            .Where(x => x.Source == source &&
                        (x.Status == AlarmStatus.Cleared || x.Status == AlarmStatus.ClearedUnacknowledged) &&
                        x.ClearedAt != null && x.ClearedAt > now.AddSeconds(-60))
            .OrderByDescending(x => x.OccurredAt)
            .FirstOrDefaultAsync(ct);
        if (mergeable is not null)
        {
            mergeable.Status = AlarmStatus.Active;
            mergeable.OccurredAt = now;
            mergeable.ClearedAt = null;
            mergeable.AcknowledgedAt = null;
            mergeable.AcknowledgeNote = null;
            mergeable.Message = message;
            mergeable.Severity = severity;
            mergeable.Priority = PriorityFromSeverity(severity);
            mergeable.RepeatCount += 1;
            return;
        }
        db.AlarmRecords.Add(new AlarmRecord
        {
            Source = source,
            Message = message,
            Severity = severity,
            Priority = PriorityFromSeverity(severity),
            OccurredAt = now,
            Status = AlarmStatus.Active,
            RepeatCount = 1
        });
    }

    private static async Task ClearAlarmAsync(ScadaDbContext db, string source, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var active = await db.AlarmRecords
            .Where(x => x.Source == source && (x.Status == AlarmStatus.Active || x.Status == AlarmStatus.Acknowledged))
            .ToListAsync(ct);
        foreach (var alarm in active)
        {
            alarm.Status = alarm.AcknowledgedAt is null ? AlarmStatus.ClearedUnacknowledged : AlarmStatus.Cleared;
            alarm.ClearedAt = now;
        }
    }

    // 报警来源 → 所属设备（用于工况抑制判定；无法解析则不抑制）。
    private static async Task<Guid?> ResolveDeviceIdAsync(
        ScadaDbContext db, string source, CancellationToken ct)
    {
        if (source.StartsWith("Device:", StringComparison.Ordinal) &&
            Guid.TryParse(source[7..], out var deviceId))
            return deviceId;
        if (source.StartsWith("TagQuality:", StringComparison.Ordinal) &&
            Guid.TryParse(source[11..], out var qualityTagId))
            return await db.Tags.Where(x => x.Id == qualityTagId)
                .Select(x => (Guid?)x.DeviceId).FirstOrDefaultAsync(ct);
        if (source.StartsWith("Tag:", StringComparison.Ordinal))
        {
            var rest = source[4..];
            var separator = rest.IndexOf(':');
            var tagText = separator < 0 ? rest : rest[..separator];
            if (Guid.TryParse(tagText, out var tagId))
                return await db.Tags.Where(x => x.Id == tagId)
                    .Select(x => (Guid?)x.DeviceId).FirstOrDefaultAsync(ct);
        }
        if (source.StartsWith("AlarmDef:", StringComparison.Ordinal) &&
            Guid.TryParse(source[9..], out var definitionId))
            return await db.AlarmDefinitions.Where(x => x.Id == definitionId)
                .Select(x => (Guid?)x.Tag!.DeviceId).FirstOrDefaultAsync(ct);
        return null;
    }

    private static AlarmPriority PriorityFromSeverity(string severity) => severity switch
    {
        "Error" => AlarmPriority.P1,
        "Info" => AlarmPriority.P3,
        _ => AlarmPriority.P2
    };
    private static OperationLog Log(string source, string message, string level = "Information") => new() { Source = source, Message = message, Level = level };
    // 采集项的可选文本：空白视为未填写，统一存 null。
    private static string? Normalize(string? text)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
    private static string NormalizeOutputName(string name)
    {
        var normalized = name.Trim();
        if (normalized.Length is < 1 or > 80)
            throw new InvalidOperationException("按钮名称长度必须为 1 到 80 个字符。");
        return normalized;
    }
    private static bool IsValidControlTag(Tag tag)
        => tag.Enabled && tag.AddressType == AddressType.Coil && tag.DataType == TagDataType.Bool &&
           tag.AccessMode == AccessMode.ReadWrite && tag.Address is >= 0 and <= ushort.MaxValue;
    private static bool IsValidMonitorTag(Tag tag)
        => tag.Enabled && tag.DataType == TagDataType.Bool &&
           tag.AddressType is AddressType.Coil or AddressType.DiscreteInput &&
           tag.Address is >= 0 and <= ushort.MaxValue;
    private static async Task SaveAsync(ScadaDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        { throw new InvalidOperationException("保存失败：名称重复，或关联配置已改变，请刷新后重试。", ex); }
    }
}
