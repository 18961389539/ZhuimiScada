using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Application;
using PLCScada.Core;
using PLCScada.Infrastructure;
using PLCScada.Infrastructure.Persistence;

namespace PLCScada.Tests;

internal sealed class TestSystem : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"plcscada-test-{Guid.NewGuid():N}.db");
    public ServiceProvider Services { get; private set; } = default!;
    public IScadaStore Store => Services.GetRequiredService<IScadaStore>();
    public IRecipeApplyLock RecipeApplyLock => Services.GetRequiredService<IRecipeApplyLock>();
    public IDeviceCommunication Communication => Services.GetRequiredService<IDeviceCommunication>();
    public ITagRuntime Runtime => Services.GetRequiredService<ITagRuntime>();
    public ISimulatorControl Simulator => Services.GetRequiredService<ISimulatorControl>();
    public DeviceService Devices => new(Store, Communication);
    public TagService Tags => new(Store);
    public Device Device { get; private set; } = default!;
    public static async Task<TestSystem> CreateAsync(bool startSimulator = true)
    {
        var system = new TestSystem();
        var services = new ServiceCollection().AddLogging();
        services.AddScada(
            $"Data Source={system._path};Pooling=False;Foreign Keys=True",
            true, 0, system._path + ".recipe-apply-locks");
        system.Services = services.BuildServiceProvider();
        await system.Services.InitializeScadaAsync(false, 0);
        if (startSimulator) await system.Simulator.StartAsync();
        system.Device = new() { Name = "Test PLC", Port = system.Simulator.Port, ConnectionTimeout = 300, PollInterval = 250, HealthCheckAddress = 10 };
        return system;
    }
    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(path)) File.Delete(path);
        var lockDirectory = _path + ".recipe-apply-locks";
        if (Directory.Exists(lockDirectory)) Directory.Delete(lockDirectory, recursive: true);
    }
}
