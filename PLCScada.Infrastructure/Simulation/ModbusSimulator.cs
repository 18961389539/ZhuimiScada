using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PLCScada.Core;

namespace PLCScada.Infrastructure.Simulation;

public sealed class ModbusSimulator(bool available, int port, ILogger<ModbusSimulator> logger) : ISimulatorControl, IHostedService, IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<TcpClient, Task> _clients = new();
    private readonly object _memoryLock = new();
    private readonly ushort[] _registers = new ushort[65536];
    private readonly bool[] _coils = new bool[65536];
    private TcpListener? _listener;
    private CancellationTokenSource? _run;
    private Task? _acceptTask;
    private Task? _variationTask;
    public bool Available => available;
    public bool Running => _listener is not null;
    private int _boundPort = port;
    public int Port => _boundPort;
    public string? LastError { get; private set; }
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!Available) throw new InvalidOperationException("模拟器仅在 Development 且显式启用时可用。");
        await _lifecycle.WaitAsync(ct);
        try
        {
            if (Running) return;
            var listener = new TcpListener(IPAddress.Loopback, Port);
            try { listener.Start(); }
            catch (SocketException ex) { LastError = $"模拟器端口 {Port} 启动失败：{ex.Message}"; throw new IOException(LastError, ex); }
            _boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            _listener = listener; _run = new(); LastError = null;
            lock (_memoryLock) { _registers[0] = 250; _registers[10] = 1234; }
            _acceptTask = AcceptAsync(listener, _run.Token);
            _variationTask = VaryAsync(_run.Token);
            logger.LogInformation("开发模拟器已启动 127.0.0.1:{Port} / Unit 1", Port);
        }
        finally { _lifecycle.Release(); }
    }
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            if (_run is null) return;
            await _run.CancelAsync(); _listener?.Stop(); _listener = null;
            foreach (var client in _clients.Keys) client.Dispose();
            if (_acceptTask is not null) await _acceptTask;
            if (_variationTask is not null) await _variationTask;
            await Task.WhenAll(_clients.Values);
            _clients.Clear(); _run.Dispose(); _run = null;
            logger.LogInformation("开发模拟器已停止");
        }
        finally { _lifecycle.Release(); }
    }
    async Task IHostedService.StartAsync(CancellationToken ct)
    {
        if (!Available) return;
        try { await StartAsync(ct); }
        catch (IOException ex) { logger.LogWarning(ex, "模拟器未启动，可在 Settings 中重试"); }
    }
    private async Task AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                var task = ServeAsync(client, ct); _clients[client] = task;
                _ = task.ContinueWith(_ => _clients.TryRemove(client, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
    }
    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream(); var header = new byte[7];
                while (!ct.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, ct);
                    var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                    if (header[2] != 0 || header[3] != 0 || length is < 2 or > 254) return;
                    var pdu = new byte[length - 1]; await stream.ReadExactlyAsync(pdu, ct);
                    // Only unit 1 exists; wrong unit is a gateway target exception.
                    var response = header[6] == 1 ? Handle(pdu) : new byte[] { (byte)(pdu[0] | 0x80), 11 };
                    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)(response.Length + 1));
                    await stream.WriteAsync(header, ct); await stream.WriteAsync(response, ct);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { /* Disconnected TCP peer or server shutdown. */ }
        }
    }
    private byte[] Handle(byte[] pdu)
    {
        var fc = pdu[0];
        byte[] Error(byte code) => [(byte)(fc | 0x80), code];
        if (pdu.Length < 5) return Error(3);
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
        lock (_memoryLock)
        {
            switch (fc)
            {
                case 1: case 2:
                    if (pdu.Length != 5 || count is 0 or > 2000) return Error(3);
                    if (address + count > 65536) return Error(2);
                    var bits = new byte[2 + (count + 7) / 8]; bits[0] = fc; bits[1] = (byte)(bits.Length - 2);
                    for (var i = 0; i < count; i++) if (_coils[address + i]) bits[2 + i / 8] |= (byte)(1 << (i % 8));
                    return bits;
                case 3: case 4:
                    if (pdu.Length != 5 || count is 0 or > 125) return Error(3);
                    if (address + count > 65536) return Error(2);
                    var words = new byte[2 + count * 2]; words[0] = fc; words[1] = (byte)(count * 2);
                    for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16BigEndian(words.AsSpan(2 + i * 2), _registers[address + i]);
                    return words;
                case 5:
                    if (pdu.Length != 5 || count is not (0 or 0xff00)) return Error(3);
                    _coils[address] = count == 0xff00; return pdu;
                case 6:
                    if (pdu.Length != 5) return Error(3);
                    _registers[address] = count; return pdu;
                case 15: case 16:
                    var max = fc == 15 ? 1968 : 123;
                    if (count == 0 || count > max || pdu.Length < 6) return Error(3);
                    if (address + count > 65536) return Error(2);
                    var payloadLength = fc == 15 ? (count + 7) / 8 : count * 2;
                    if (pdu[5] != payloadLength || pdu.Length != 6 + payloadLength) return Error(3);
                    for (var i = 0; i < count; i++)
                        if (fc == 15) _coils[address + i] = (pdu[6 + i / 8] & (1 << (i % 8))) != 0;
                        else _registers[address + i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + i * 2));
                    return pdu[..5];
                default: return Error(1);
            }
        }
    }
    private async Task VaryAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            var step = 0;
            while (await timer.WaitForNextTickAsync(ct))
                lock (_memoryLock) { _registers[0] = (ushort)(250 + 80 * Math.Sin(++step / 10d)); _coils[1] = step % 2 == 0; }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); }
}
