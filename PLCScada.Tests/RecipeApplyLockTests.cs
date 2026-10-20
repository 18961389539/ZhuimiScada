using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Application;
using PLCScada.Infrastructure.Coordination;

namespace PLCScada.Tests;

public sealed class RecipeApplyLockTests
{
    [Fact]
    public async Task ScadaRegistrationProvidesTheRequiredApplyLock()
    {
        await using var system = await TestSystem.CreateAsync();
        await using var scope = system.Services.CreateAsyncScope();

        Assert.IsType<RecipeApplyService>(scope.ServiceProvider.GetRequiredService<RecipeApplyService>());
    }

    [Fact]
    public async Task IndependentLockInstancesCoordinateAndReleaseTheResource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PLCScada.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var firstLock = new FileRecipeApplyLock(directory);
            var secondLock = new FileRecipeApplyLock(directory);
            var firstLease = await firstLock.TryAcquireAsync("modbus-tcp|127.0.0.1|502|1", TimeSpan.Zero)
                ?? throw new Xunit.Sdk.XunitException("Expected the first lock acquisition to succeed.");

            var blockedLease = await secondLock.TryAcquireAsync(
                "modbus-tcp|127.0.0.1|502|1", TimeSpan.FromMilliseconds(150));
            Assert.Null(blockedLease);

            await using var independentLease = await secondLock.TryAcquireAsync(
                "modbus-tcp|127.0.0.1|502|2", TimeSpan.Zero)
                ?? throw new Xunit.Sdk.XunitException("A different Modbus unit should have an independent lock.");

            await firstLease.DisposeAsync();

            await using var releasedLease = await secondLock.TryAcquireAsync(
                "modbus-tcp|127.0.0.1|502|1", TimeSpan.FromSeconds(1))
                ?? throw new Xunit.Sdk.XunitException("Expected the released resource to become available.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessLockBlocksAnotherProcessAndReleasesWhenHolderExits()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(), "PLCScada.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var resourceKey = "modbus-tcp|127.0.0.1|502|1";
        var lockName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resourceKey))) + ".lock";
        var lockPath = Path.Combine(directory, lockName);
        var readyPath = Path.Combine(directory, "holder-ready");
        var scriptPath = Path.Combine(directory, "hold-lock.ps1");
        await File.WriteAllTextAsync(scriptPath, """
            param([string]$LockPath, [string]$ReadyPath)
            $stream = [System.IO.File]::Open(
                $LockPath,
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
            [System.IO.File]::WriteAllText($ReadyPath, "ready")
            Start-Sleep -Seconds 30
            $stream.Dispose()
            """);

        using var holder = new Process
        {
            StartInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        holder.StartInfo.ArgumentList.Add("-NoProfile");
        holder.StartInfo.ArgumentList.Add("-NonInteractive");
        holder.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        holder.StartInfo.ArgumentList.Add("Bypass");
        holder.StartInfo.ArgumentList.Add("-File");
        holder.StartInfo.ArgumentList.Add(scriptPath);
        holder.StartInfo.ArgumentList.Add(lockPath);
        holder.StartInfo.ArgumentList.Add(readyPath);

        try
        {
            Assert.True(holder.Start(), "Could not start the lock-holder process.");
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(readyPath) && !holder.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(25);

            Assert.True(
                File.Exists(readyPath),
                holder.HasExited
                    ? $"Lock-holder exited early: {await holder.StandardError.ReadToEndAsync()}"
                    : "Lock-holder did not acquire the file lock in time.");

            var applyLock = new FileRecipeApplyLock(directory);
            var blocked = await applyLock.TryAcquireAsync(resourceKey, TimeSpan.Zero);
            Assert.Null(blocked);

            holder.Kill(entireProcessTree: true);
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var released = await applyLock.TryAcquireAsync(resourceKey, TimeSpan.FromSeconds(1));
            Assert.NotNull(released);
            await released!.DisposeAsync();
        }
        finally
        {
            if (!holder.HasExited)
            {
                holder.Kill(entireProcessTree: true);
                await holder.WaitForExitAsync();
            }
            Directory.Delete(directory, recursive: true);
        }
    }
}
