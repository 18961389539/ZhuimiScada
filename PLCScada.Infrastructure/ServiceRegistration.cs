using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Application;
using PLCScada.Core;
using PLCScada.Infrastructure.Coordination;
using PLCScada.Infrastructure.Communication;
using PLCScada.Infrastructure.Persistence;
using PLCScada.Infrastructure.Simulation;

namespace PLCScada.Infrastructure;

public static class ServiceRegistration
{
    public static IServiceCollection AddScada(
        this IServiceCollection services,
        string connectionString,
        bool simulatorEnabled,
        int simulatorPort,
        string? recipeApplyLockDirectory = null,
        DataRetentionOptions? retentionOptions = null)
    {
        services.AddDbContextFactory<ScadaDbContext>(o => o.UseSqlite(connectionString));
        services.AddSingleton<IScadaStore, ScadaStore>();
        services.AddSingleton(retentionOptions ?? new DataRetentionOptions());
        services.AddHostedService<DataRetentionService>();
        services.AddHostedService<SnWatcherService>();
        services.AddSingleton<SnapshotCaptureService>();
        services.AddHostedService<SnapshotTriggerService>();
        var lockDirectory = string.IsNullOrWhiteSpace(recipeApplyLockDirectory)
            ? GetDefaultRecipeApplyLockDirectory(connectionString)
            : Path.GetFullPath(recipeApplyLockDirectory);
        services.AddSingleton<IRecipeApplyLock>(_ => new FileRecipeApplyLock(lockDirectory));
        services.AddSingleton<IModbusConnectionFactory, IoTClientConnectionFactory>();
        services.AddSingleton<IDeviceCommunication, DeviceCommunication>();
        services.AddSingleton<TagRuntime>();
        services.AddSingleton<ITagRuntime>(sp => sp.GetRequiredService<TagRuntime>());
        services.AddScoped<DeviceService>(); services.AddScoped<TagService>(); services.AddScoped<TagWriteService>(); services.AddScoped<ManualOutputService>(); services.AddScoped<HmiService>(); services.AddScoped<RecipeService>(); services.AddScoped<RecipeApplyService>(); services.AddScoped<AlarmDefinitionService>();
        services.AddSingleton(sp => new ModbusSimulator(simulatorEnabled, simulatorPort, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ModbusSimulator>>()));
        services.AddSingleton<ISimulatorControl>(sp => sp.GetRequiredService<ModbusSimulator>());
        services.AddHostedService(sp => sp.GetRequiredService<ModbusSimulator>());
        services.AddHostedService<ModbusPollingService>();
        return services;
    }

    private static string GetDefaultRecipeApplyLockDirectory(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) ||
            dataSource == ":memory:" ||
            dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Path.GetTempPath(), "PLCScada", "recipe-apply-locks");

        var databasePath = Path.GetFullPath(dataSource);
        return Path.Combine(
            Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory,
            ".plcscada-recipe-apply-locks");
    }

    public static async Task InitializeScadaAsync(this IServiceProvider services, bool seedDemo, int simulatorPort)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        await SqliteGuidNormalizer.NormalizeAsync(db);
        if (seedDemo && !await db.Devices.AnyAsync())
        {
            var device = new Device { Name = "SIM-01 · 开发模拟器", Description = "测试数据 · 非真实 PLC", Port = simulatorPort };
            db.Devices.Add(device);
            db.Tags.AddRange(new Tag { Name = "SIM.Temperature", Description = "模拟温度", DeviceId = device.Id, Scale = 0.1, Unit = "°C" },
                new Tag { Name = "SIM.Running", Description = "模拟状态（每秒变化）", DeviceId = device.Id, AddressType = AddressType.Coil, Address = 1, DataType = TagDataType.Bool });
        }
        // Resolve old in-memory sessions as cleared on restart; current faults are raised again by polling.
        foreach (var alarm in await db.AlarmRecords.Where(x => x.Status != AlarmStatus.Cleared).ToListAsync()) alarm.Status = AlarmStatus.Cleared;
        db.OperationLogs.Add(new() { Source = "System", Message = "PLCScada 启动" });
        await db.SaveChangesAsync();
    }
}
