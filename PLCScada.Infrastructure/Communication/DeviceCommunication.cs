using System.Collections.Concurrent;
using System.Diagnostics;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using PLCScada.Core;

namespace PLCScada.Infrastructure.Communication;

public sealed class DeviceCommunication(IModbusConnectionFactory factory, IScadaStore store, ILogger<DeviceCommunication> logger)
    : IDeviceCommunication, IAsyncDisposable
{
    private sealed class Session(Guid id)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public IModbusConnection? Connection;
        public string Fingerprint = "";
        public bool ManualStop;
        public bool AlarmRaised;
        public DeviceSnapshot Snapshot = new(id, ConnectionStatus.Disconnected);
        public DateTimeOffset LastErrorLogged;
    }
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private Session Get(Guid id) => _sessions.GetOrAdd(id, x => new(x));
    public DeviceSnapshot GetSnapshot(Guid id) => Get(id).Snapshot;
    public IReadOnlyList<DeviceSnapshot> GetSnapshots() => _sessions.Values.Select(x => x.Snapshot).ToArray();

    public async Task<CommunicationTestResult> TestAsync(Device device, CancellationToken ct = default)
    {
        // A separate short-lived test never overrides the operator's manual disconnect state.
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(device.ConnectionTimeout * 3);
        try
        {
            await using var connection = factory.Create(device);
            await connection.OpenAsync(timeout.Token);
            var bytes = await connection.ReadAsync((byte)device.UnitId, AddressType.HoldingRegister, (ushort)device.HealthCheckAddress, 1, timeout.Token);
            var value = BinaryPrimitives.ReadUInt16BigEndian(bytes);
            await TryBookkeepingAsync(device, "通信测试日志",
                token => store.LogAsync(device.Name, $"通信测试通过：FC03，零基地址 {device.HealthCheckAddress}，值 {value}", ct: token), ct);
            return new(true, $"Modbus FC03 响应有效，寄存器[{device.HealthCheckAddress}] = {value}", value, watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await TryBookkeepingAsync(device, "通信测试日志",
                token => store.LogAsync(device.Name, $"通信测试失败：{ex.Message}", "Warning", token), ct);
            return new(false, ex is OperationCanceledException ? "通信测试超时。" : ex.Message, null, watch.Elapsed.TotalMilliseconds);
        }
    }
    public async Task ConnectAsync(Device device, CancellationToken ct = default)
    {
        if (!device.Enabled) throw new InvalidOperationException("设备已禁用。");
        var s = Get(device.Id);
        await s.Gate.WaitAsync(ct);
        try { s.ManualStop = false; }
        finally { s.Gate.Release(); }
        await PollAsync(device, ct);
        if (s.Snapshot.Status != ConnectionStatus.Online) throw new IOException(s.Snapshot.LastError ?? "连接失败。");
    }
    public Task DisconnectAsync(Guid id, CancellationToken ct = default) => ResetAsync(id, true, ct);
    public Task InvalidateAsync(Guid id, CancellationToken ct = default) => ResetAsync(id, false, ct);
    private async Task ResetAsync(Guid id, bool manualStop, CancellationToken ct)
    {
        var s = Get(id); await s.Gate.WaitAsync(ct);
        try
        {
            s.ManualStop = manualStop;
            await CloseAsync(s);
            s.Snapshot = s.Snapshot with { Status = ConnectionStatus.Disconnected, LastError = null };
        }
        finally { s.Gate.Release(); }
    }
    public async Task PollAsync(Device device, CancellationToken ct)
    {
        if (!device.Enabled)
        {
            var s = Get(device.Id); await s.Gate.WaitAsync(ct);
            try
            {
                await CloseAsync(s);
                if (s.Snapshot.Status != ConnectionStatus.Disabled) await store.SetCommunicationAlarmAsync(device.Id, device.Name, null, ct);
                s.AlarmRaised = false;
                s.Snapshot = s.Snapshot with { Status = ConnectionStatus.Disabled, LastError = null };
            }
            finally { s.Gate.Release(); }
            return;
        }
        try { await ReadAsync(device, AddressType.HoldingRegister, (ushort)device.HealthCheckAddress, 1, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (InvalidOperationException) { /* Manually disconnected; no automatic reconnection. */ }
        catch (IOException) { /* State and throttled diagnostics were recorded in ReadAsync. */ }
    }
    public async Task<byte[]> ReadAsync(Device device, AddressType type, ushort address, ushort count, CancellationToken ct)
    {
        var s = Get(device.Id);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(device.ConnectionTimeout * 3);
        await s.Gate.WaitAsync(deadline.Token);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!device.Enabled || s.ManualStop) throw new InvalidOperationException("设备已禁用或手动断开。");
            var watch = Stopwatch.StartNew();
            var fingerprint = $"{device.IpAddress}:{device.Port}:{device.UnitId}:{device.ConnectionTimeout}";
            if (s.Connection is null || s.Fingerprint != fingerprint)
            {
                await CloseAsync(s);
                s.Snapshot = s.Snapshot with { Status = ConnectionStatus.Connecting };
                s.Connection = factory.Create(device); s.Fingerprint = fingerprint;
                await s.Connection.OpenAsync(deadline.Token);
            }
            var bytes = await s.Connection.ReadAsync((byte)device.UnitId, type, address, count, deadline.Token);
            var recovered = s.Snapshot.Status != ConnectionStatus.Online;
            var now = DateTimeOffset.UtcNow;
            s.Snapshot = s.Snapshot with { Status = ConnectionStatus.Online, LastConnectedTime = recovered ? now : s.Snapshot.LastConnectedTime,
                LastCommunicationTime = now, LastError = null, LatencyMs = watch.Elapsed.TotalMilliseconds };
            if (recovered)
            {
                logger.LogInformation("设备 {Device} 已连接", device.Name);
                await TryBookkeepingAsync(device, "通信恢复日志",
                    token => store.LogAsync(device.Name, "Modbus 通信已连接", ct: token), ct);
                if (await TryBookkeepingAsync(device, "通信报警解除",
                        token => store.SetCommunicationAlarmAsync(device.Id, device.Name, null, token), ct))
                    s.AlarmRaised = false;
            }
            return bytes;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            await CloseAsync(s);
            s.Snapshot = s.Snapshot with { Status = ConnectionStatus.Faulted, LastError = ex is OperationCanceledException ? "通信超时或已取消。" : ex.Message, ErrorCount = s.Snapshot.ErrorCount + 1 };
            if (!ct.IsCancellationRequested && !s.AlarmRaised)
            {
                if (await TryBookkeepingAsync(device, "通信报警",
                        token => store.SetCommunicationAlarmAsync(device.Id, device.Name, s.Snapshot.LastError, token), ct))
                    s.AlarmRaised = true;
            }
            if (!ct.IsCancellationRequested && DateTimeOffset.UtcNow - s.LastErrorLogged >= TimeSpan.FromSeconds(30))
            {
                s.LastErrorLogged = DateTimeOffset.UtcNow;
                logger.LogWarning("设备 {Device} 通信失败：{Error}", device.Name, s.Snapshot.LastError);
                await TryBookkeepingAsync(device, "故障日志",
                    token => store.LogAsync(device.Name, s.Snapshot.LastError!, "Warning", token), ct);
            }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new IOException(s.Snapshot.LastError, ex);
        }
        finally { s.Gate.Release(); }
    }
    public async Task WriteCoilAsync(Device device, ushort address, bool value, CancellationToken ct = default)
    {
        var s = Get(device.Id);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(device.ConnectionTimeout * 3);
        await s.Gate.WaitAsync(deadline.Token);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!device.Enabled || s.ManualStop) throw new InvalidOperationException("设备已禁用或手动断开。");
            var watch = Stopwatch.StartNew();
            var fingerprint = $"{device.IpAddress}:{device.Port}:{device.UnitId}:{device.ConnectionTimeout}";
            if (s.Connection is null || s.Fingerprint != fingerprint)
            {
                await CloseAsync(s);
                s.Snapshot = s.Snapshot with { Status = ConnectionStatus.Connecting };
                s.Connection = factory.Create(device); s.Fingerprint = fingerprint;
                await s.Connection.OpenAsync(deadline.Token);
            }
            await s.Connection.WriteCoilAsync((byte)device.UnitId, address, value, deadline.Token);
            var recovered = s.Snapshot.Status != ConnectionStatus.Online;
            var now = DateTimeOffset.UtcNow;
            s.Snapshot = s.Snapshot with { Status = ConnectionStatus.Online,
                LastConnectedTime = recovered ? now : s.Snapshot.LastConnectedTime,
                LastCommunicationTime = now, LastError = null, LatencyMs = watch.Elapsed.TotalMilliseconds };
            if (recovered)
            {
                logger.LogInformation("设备 {Device} 已连接", device.Name);
                await TryBookkeepingAsync(device, "通信恢复日志",
                    token => store.LogAsync(device.Name, "Modbus 通信已连接", ct: token), ct);
                if (await TryBookkeepingAsync(device, "通信报警解除",
                        token => store.SetCommunicationAlarmAsync(device.Id, device.Name, null, token), ct))
                    s.AlarmRaised = false;
            }
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            await CloseAsync(s);
            s.Snapshot = s.Snapshot with { Status = ConnectionStatus.Faulted,
                LastError = ex is OperationCanceledException ? "通信超时或已取消。" : ex.Message,
                ErrorCount = s.Snapshot.ErrorCount + 1 };
            if (!ct.IsCancellationRequested && !s.AlarmRaised)
            {
                if (await TryBookkeepingAsync(device, "通信报警",
                        token => store.SetCommunicationAlarmAsync(device.Id, device.Name, s.Snapshot.LastError, token), ct))
                    s.AlarmRaised = true;
            }
            if (!ct.IsCancellationRequested && DateTimeOffset.UtcNow - s.LastErrorLogged >= TimeSpan.FromSeconds(30))
            {
                s.LastErrorLogged = DateTimeOffset.UtcNow;
                logger.LogWarning("设备 {Device} 输出写入失败：{Error}", device.Name, s.Snapshot.LastError);
                await TryBookkeepingAsync(device, "故障日志",
                    token => store.LogAsync(device.Name, s.Snapshot.LastError!, "Warning", token), ct);
            }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new IOException(s.Snapshot.LastError, ex);
        }
        finally { s.Gate.Release(); }
    }

    public Task WriteSingleRegisterAsync(
        Device device, ushort address, ushort value, CancellationToken ct = default)
        => WriteRegisterRequestAsync(
            device,
            (connection, unitId, token) => connection.WriteSingleRegisterAsync(unitId, address, value, token),
            ct);

    public Task WriteRegistersAsync(
        Device device, ushort address, byte[] networkBytes, CancellationToken ct = default)
    {
        var payload = networkBytes.ToArray();
        return WriteRegisterRequestAsync(
            device,
            (connection, unitId, token) => connection.WriteRegistersAsync(unitId, address, payload, token),
            ct);
    }

    private async Task WriteRegisterRequestAsync(
        Device device,
        Func<IModbusConnection, byte, CancellationToken, Task> write,
        CancellationToken ct)
    {
        var s = Get(device.Id);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(device.ConnectionTimeout * 3);
        await s.Gate.WaitAsync(deadline.Token);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!device.Enabled || s.ManualStop)
                throw new InvalidOperationException("设备已禁用或手动断开。");

            var watch = Stopwatch.StartNew();
            var fingerprint = $"{device.IpAddress}:{device.Port}:{device.UnitId}:{device.ConnectionTimeout}";
            if (s.Connection is null || s.Fingerprint != fingerprint)
            {
                await CloseAsync(s);
                s.Snapshot = s.Snapshot with { Status = ConnectionStatus.Connecting };
                s.Connection = factory.Create(device);
                s.Fingerprint = fingerprint;
                await s.Connection.OpenAsync(deadline.Token);
            }

            await write(s.Connection, (byte)device.UnitId, deadline.Token);
            var recovered = s.Snapshot.Status != ConnectionStatus.Online;
            var now = DateTimeOffset.UtcNow;
            s.Snapshot = s.Snapshot with
            {
                Status = ConnectionStatus.Online,
                LastConnectedTime = recovered ? now : s.Snapshot.LastConnectedTime,
                LastCommunicationTime = now,
                LastError = null,
                LatencyMs = watch.Elapsed.TotalMilliseconds
            };
            if (recovered)
            {
                logger.LogInformation("设备 {Device} 已连接", device.Name);
                await TryBookkeepingAsync(device, "通信恢复日志",
                    token => store.LogAsync(device.Name, "Modbus 通信已连接", ct: token), ct);
                if (await TryBookkeepingAsync(device, "通信报警解除",
                        token => store.SetCommunicationAlarmAsync(device.Id, device.Name, null, token), ct))
                    s.AlarmRaised = false;
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await CloseAsync(s);
            s.Snapshot = s.Snapshot with
            {
                Status = ConnectionStatus.Faulted,
                LastError = ex is OperationCanceledException ? "通信超时或已取消。" : ex.Message,
                ErrorCount = s.Snapshot.ErrorCount + 1
            };
            if (!ct.IsCancellationRequested && !s.AlarmRaised)
            {
                if (await TryBookkeepingAsync(device, "通信报警",
                        token => store.SetCommunicationAlarmAsync(device.Id, device.Name, s.Snapshot.LastError, token), ct))
                    s.AlarmRaised = true;
            }
            if (!ct.IsCancellationRequested && DateTimeOffset.UtcNow - s.LastErrorLogged >= TimeSpan.FromSeconds(30))
            {
                s.LastErrorLogged = DateTimeOffset.UtcNow;
                logger.LogWarning("设备 {Device} 寄存器写入失败：{Error}", device.Name, s.Snapshot.LastError);
                await TryBookkeepingAsync(device, "故障日志",
                    token => store.LogAsync(device.Name, s.Snapshot.LastError!, "Warning", token), ct);
            }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new IOException(s.Snapshot.LastError, ex);
        }
        finally
        {
            s.Gate.Release();
        }
    }

    // 审计/日志写入属于尽力而为的记账：写入失败只记录警告，不改变通信状态与读取/写入结果。
    // 现场持久化介质（磁盘、权限、临时文件清理）问题不应让设备通信被误判为故障。
    private async Task<bool> TryBookkeepingAsync(
        Device device, string operation, Func<CancellationToken, Task> write, CancellationToken ct)
    {
        try
        {
            await write(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("设备 {Device} 的{Operation}写入失败（不影响通信结果）：{Error}",
                device.Name, operation, ex.Message);
            return false;
        }
    }

    private static async Task CloseAsync(Session s)
    { if (s.Connection is not null) { await s.Connection.DisposeAsync(); s.Connection = null; } }
    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values)
        {
            await s.Gate.WaitAsync();
            try { await CloseAsync(s); }
            finally { s.Gate.Release(); }
        }
    }
}
