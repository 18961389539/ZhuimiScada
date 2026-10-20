using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Core;
using PLCScada.Infrastructure.Persistence;

namespace PLCScada.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task MigrationCreatesAllTablesAndCanBeReapplied()
    {
        await using var system = await TestSystem.CreateAsync();
        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(25, (await db.Database.GetAppliedMigrationsAsync()).Count());
        await db.Database.MigrateAsync();
        Assert.Empty(await db.HmiPages.ToListAsync()); Assert.Empty(await db.HmiControls.ToListAsync());
        Assert.Empty(await db.ManualOutputBindings.ToListAsync());
        Assert.Empty(await db.Recipes.ToListAsync()); Assert.Empty(await db.RecipeParameters.ToListAsync());
        Assert.Empty(await db.RecipeVersions.ToListAsync());
        Assert.Empty(await db.AlarmRecords.ToListAsync()); Assert.NotEmpty(await db.OperationLogs.ToListAsync());
    }

    [Fact]
    public async Task RevisionHistoryMigrationBackfillsTheCurrentRecipeAndTagSignatures()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plcscada-recipe-history-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ScadaDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False;Foreign Keys=True")
            .Options;
        try
        {
            await using var db = new ScadaDbContext(options);
            await db.Database.MigrateAsync("20261016160000_AddRecipeRevisionAndApplyAudit");

            var device = new Device { Name = "History PLC", IpAddress = "192.0.2.10", Port = 1502, UnitId = 3 };
            var numericTag = new Tag
            {
                Name = "Power limit",
                DeviceId = device.Id,
                AddressType = AddressType.HoldingRegister,
                Address = 100,
                DataType = TagDataType.Float32,
                AccessMode = AccessMode.ReadWrite,
                Scale = 0.1,
                Offset = 2,
                Unit = "kW",
                MinValue = 0,
                MaxValue = 100
            };
            var boolTag = new Tag
            {
                Name = "Camera enable",
                DeviceId = device.Id,
                AddressType = AddressType.Coil,
                Address = 20,
                DataType = TagDataType.Bool,
                AccessMode = AccessMode.ReadWrite
            };
            var recipe = new Recipe
            {
                DeviceId = device.Id,
                Name = "Existing recipe",
                Description = "pre-history",
                Revision = 3,
                Parameters =
                [
                    new RecipeParameter { TagId = numericTag.Id, EngineeringValue = 12.5 },
                    new RecipeParameter { TagId = boolTag.Id, BooleanValue = true }
                ]
            };
            db.Devices.Add(device);
            await db.SaveChangesAsync();

            // 此时数据库停留在 AddRecipeRevisionAndApplyAudit：Recipes/Tags 表尚无后续迁移新增的列；
            // 用原始 SQL 模拟旧版本代码写入，避免当前 EF 模型引用未来迁移才存在的列。
            var legacyTimestamp = DateTimeOffset.UtcNow.UtcTicks;
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO Tags (Id, Name, Description, DeviceId, AddressType, Address, DataType, AccessMode,
                                  ScanInterval, Scale, Offset, Unit, MinValue, MaxValue, DefaultValue, Enabled, ByteOrder, WordOrder)
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}, {11}, {12}, {13}, NULL, 1, 0, 0)
                """,
                numericTag.Id, numericTag.Name, numericTag.Description, device.Id,
                (int)numericTag.AddressType, numericTag.Address, (int)numericTag.DataType,
                (int)numericTag.AccessMode, numericTag.ScanInterval,
                numericTag.Scale, numericTag.Offset, numericTag.Unit,
                numericTag.MinValue, numericTag.MaxValue);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO Tags (Id, Name, Description, DeviceId, AddressType, Address, DataType, AccessMode,
                                  ScanInterval, Scale, Offset, Unit, MinValue, MaxValue, DefaultValue, Enabled, ByteOrder, WordOrder)
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}, {11}, NULL, NULL, NULL, 1, 0, 0)
                """,
                boolTag.Id, boolTag.Name, boolTag.Description, device.Id,
                (int)boolTag.AddressType, boolTag.Address, (int)boolTag.DataType,
                (int)boolTag.AccessMode, boolTag.ScanInterval,
                boolTag.Scale, boolTag.Offset, boolTag.Unit);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO Recipes (Id, DeviceId, Name, Description, Revision, CreatedAt, UpdatedAt, LastAppliedAt)
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, NULL)
                """,
                recipe.Id, device.Id, recipe.Name, recipe.Description, recipe.Revision,
                legacyTimestamp, legacyTimestamp);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO RecipeParameters (RecipeId, TagId, EngineeringValue, BooleanValue) VALUES ({0}, {1}, {2}, NULL)",
                recipe.Id, numericTag.Id, 12.5);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO RecipeParameters (RecipeId, TagId, EngineeringValue, BooleanValue) VALUES ({0}, {1}, NULL, 1)",
                recipe.Id, boolTag.Id);

            await db.Database.MigrateAsync();

            var baseline = await db.RecipeVersions.SingleAsync(
                x => x.RecipeId == recipe.Id && x.Revision == 3);
            Assert.Equal(recipe.Name, baseline.Name);
            Assert.Equal(device.Name, baseline.DeviceName);
            Assert.Equal(device.IpAddress, baseline.DeviceIpAddress);
            Assert.Equal(device.Port, baseline.DevicePort);
            var snapshots = JsonSerializer.Deserialize<RecipeVersionParameterSnapshot[]>(
                baseline.ParametersJson)!;
            Assert.Equal(2, snapshots.Length);
            Assert.Contains(snapshots, x =>
                x.TagId == numericTag.Id && x.Address == 100 && x.Scale == 0.1 &&
                x.EngineeringValue == 12.5 && x.Unit == "kW");
            Assert.Contains(snapshots, x =>
                x.TagId == boolTag.Id && x.AddressType == AddressType.Coil &&
                x.BooleanValue == true && x.EngineeringValue is null);
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task FailedRecipeSaveRollsBackRecipeParametersVersionAndAudit()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "Recipe atomicity tag",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.Float32,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(tag);

        var recipe = new Recipe
        {
            Id = Guid.Empty,
            DeviceId = system.Device.Id,
            Name = "Atomicity failure",
            Description = ""
        };
        var invalidParameter = new RecipeParameter
        {
            TagId = tag.Id,
            EngineeringValue = 12.5,
            BooleanValue = true
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SaveRecipeAsync(recipe, [invalidParameter]));
        Assert.IsType<DbUpdateException>(error.InnerException);

        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.Recipes.ToListAsync());
        Assert.Empty(await db.RecipeParameters.ToListAsync());
        Assert.Empty(await db.RecipeVersions.ToListAsync());
        Assert.Empty(await db.OperationLogs.Where(x => x.Source == "配方").ToListAsync());
    }

    [Fact]
    public async Task RecipeVersionSnapshotsCannotBeUpdatedButCascadeDeleteStillWorks()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "Recipe immutable tag",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.Float32,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(tag);
        var recipeId = await system.Store.SaveRecipeAsync(
            new Recipe
            {
                Id = Guid.Empty,
                DeviceId = system.Device.Id,
                Name = "Immutable history",
                Description = ""
            },
            [new RecipeParameter { TagId = tag.Id, EngineeringValue = 12.5 }]);

        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var version = await db.RecipeVersions.SingleAsync(x => x.RecipeId == recipeId);
            version.Name = "Tampered";
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("append-only", error.InnerException?.Message, StringComparison.OrdinalIgnoreCase);
        }

        var historyPage = await system.Store.GetRecipeVersionsPageAsync(recipeId, 0, 10);
        var savedVersion = Assert.Single(historyPage.Versions);
        Assert.Equal(1, historyPage.CurrentRevision);
        Assert.Equal(1, historyPage.TotalCount);
        Assert.Equal("Immutable history", savedVersion.Name);

        await system.Store.DeleteRecipeAsync(recipeId);
        Assert.Null(await system.Store.GetRecipeVersionAsync(recipeId, 1));
    }

    [Fact]
    public async Task RecipeLookupsReturnOneRecipeAndUseCaseInsensitiveNameChecks()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "Lookup tag",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.Float32,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(tag);

        var firstId = await system.Store.SaveRecipeAsync(
            new Recipe
            {
                Id = Guid.Empty,
                DeviceId = system.Device.Id,
                Name = "Alpha",
                Description = ""
            },
            [new RecipeParameter { TagId = tag.Id, EngineeringValue = 12.5 }]);
        await system.Store.SaveRecipeAsync(
            new Recipe
            {
                Id = Guid.Empty,
                DeviceId = system.Device.Id,
                Name = "Beta",
                Description = ""
            },
            [new RecipeParameter { TagId = tag.Id, EngineeringValue = 25 }]);

        var recipe = await system.Store.GetRecipeAsync(firstId);
        Assert.NotNull(recipe);
        Assert.Equal("Alpha", recipe.Name);
        Assert.Equal("Lookup tag", Assert.Single(recipe.Parameters).Tag?.Name);
        Assert.True(await system.Store.RecipeNameExistsAsync("aLPHa"));
        Assert.False(await system.Store.RecipeNameExistsAsync("alpha", firstId));
        Assert.Equal(new[] { "Alpha", "Beta" }, await system.Store.GetRecipeNamesAsync());
    }

    [Fact]
    public async Task ManualOutputMigrationPreservesBindingsAndSeedsExistingDevices()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plcscada-migration-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ScadaDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False;Foreign Keys=True")
            .Options;
        var deviceId = Guid.NewGuid();
        try
        {
            await using var db = new ScadaDbContext(options);
            await db.Database.MigrateAsync("20261015083858_AddManualOutputBindings");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Devices
                    (Id, Name, Description, Protocol, IpAddress, Port, UnitId, PollInterval,
                     ConnectionTimeout, HealthCheckAddress, Enabled)
                VALUES
                    ({deviceId}, {"Migration PLC"}, {""}, {"Modbus TCP"}, {"127.0.0.1"},
                     {502}, {1}, {1000}, {1500}, {0}, {true});
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ManualOutputBindings (DeviceId, OutputName, TagId)
                VALUES ({deviceId}, {"通电"}, {null});
                """);

            await db.Database.MigrateAsync();

            Assert.Equal(14, await db.ManualOutputPoints.CountAsync(x => x.DeviceId == deviceId));
            var point = await db.ManualOutputPoints.SingleAsync(x => x.DeviceId == deviceId && x.Name == "通电");
            var binding = await db.ManualOutputBindings.Include(x => x.OutputPoint)
                .SingleAsync(x => x.DeviceId == deviceId);
            Assert.Equal(point.Id, binding.OutputPointId);
            Assert.Equal("通电", binding.OutputPoint!.Name);
            Assert.Null(binding.TagId);
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task DeletingBoundTagKeepsButtonExplicitlyUnbound()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "Manual output",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 24576,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(tag);
        var outputPoint = (await system.Store.GetManualOutputPointsAsync(system.Device.Id))
            .Single(x => x.Name == "通电");
        await system.Store.SaveManualOutputBindingsAsync(system.Device.Id,
            [new ManualOutputBinding { DeviceId = system.Device.Id, OutputPointId = outputPoint.Id, TagId = tag.Id }]);

        await system.Tags.DeleteAsync(tag.Id);

        var bindings = await system.Store.GetManualOutputBindingsAsync(system.Device.Id);
        var binding = Assert.Single(bindings);
        Assert.Equal("通电", binding.OutputPoint!.Name);
        Assert.Null(binding.TagId);
    }
    [Fact]
    public async Task DeviceCrudPersistsAndAuditsEachMutation()
    {
        await using var system = await TestSystem.CreateAsync();
        var d = system.Device;
        await system.Devices.SaveAsync(d);
        d.Description = "Updated"; d.Port = 15021;
        await system.Devices.SaveAsync(d);
        Assert.Equal("Updated", (await system.Store.GetDeviceAsync(d.Id))!.Description);
        Assert.Equal(15021, (await system.Devices.GetAllAsync()).Single().Port);
        await system.Devices.SetEnabledAsync(d.Id, false);
        Assert.False((await system.Store.GetDeviceAsync(d.Id))!.Enabled);
        await system.Devices.DeleteAsync(d.Id);
        Assert.Empty(await system.Devices.GetAllAsync());
        Assert.Equal(4, (await system.Store.GetLogsAsync()).Count(x => x.Source == d.Name));
    }
    [Fact]
    public async Task DuplicateNamesAreCaseInsensitiveAndFailedSaveHasNoAudit()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await Assert.ThrowsAsync<InvalidOperationException>(() => system.Devices.SaveAsync(new() { Name = "test plc" }));
        Assert.Single(await system.Devices.GetAllAsync());
        Assert.Single(await system.Store.GetLogsAsync(), x => x.Source == system.Device.Name);
    }
    [Theory]
    [InlineData("not-ip", 502, 1)] [InlineData("127.0.0.1", 0, 1)] [InlineData("127.0.0.1", 502, 0)]
    public async Task InvalidDeviceRejectedOnServer(string ip, int port, int unit)
    {
        await using var system = await TestSystem.CreateAsync();
        await Assert.ThrowsAsync<ValidationException>(() => system.Devices.SaveAsync(new() { Name = "invalid", IpAddress = ip, Port = port, UnitId = unit }));
        Assert.Empty(await system.Devices.GetAllAsync());
    }
    [Fact]
    public async Task DeviceAndTagDeletionRespectForeignKeys()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag { Name = "Pressure", DeviceId = system.Device.Id };
        await system.Tags.SaveAsync(tag);
        await Assert.ThrowsAsync<InvalidOperationException>(() => system.Devices.DeleteAsync(system.Device.Id));
        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var page = new HmiPage { Name = "Test" }; db.HmiPages.Add(page);
            db.HmiControls.Add(new() { HmiPageId = page.Id, TagId = tag.Id }); await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => system.Tags.DeleteAsync(tag.Id));
        await using (var db = await factory.CreateDbContextAsync()) { db.HmiControls.RemoveRange(db.HmiControls); await db.SaveChangesAsync(); }
        await system.Tags.DeleteAsync(tag.Id); await system.Devices.DeleteAsync(system.Device.Id);
        Assert.Empty(await system.Store.GetTagsAsync());
    }
    [Theory]
    [InlineData(AddressType.InputRegister, TagDataType.UInt16, AccessMode.ReadWrite, 0)]
    [InlineData(AddressType.DiscreteInput, TagDataType.Bool, AccessMode.ReadWrite, 0)]
    [InlineData(AddressType.Coil, TagDataType.Int16, AccessMode.ReadOnly, 0)]
    [InlineData(AddressType.HoldingRegister, TagDataType.Float64, AccessMode.ReadOnly, 65534)]
    public async Task InvalidTagConfigurationRejected(AddressType type, TagDataType dataType, AccessMode access, int address)
    {
        await using var system = await TestSystem.CreateAsync(); await system.Devices.SaveAsync(system.Device);
        await Assert.ThrowsAsync<ValidationException>(() => system.Tags.SaveAsync(new() { Name = "Invalid", DeviceId = system.Device.Id, AddressType = type, DataType = dataType, AccessMode = access, Address = address }));
    }
}
