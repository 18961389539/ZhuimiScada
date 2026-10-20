using System.Buffers.Binary;
using IoTClient.Clients.Modbus;
using PLCScada.Core;
namespace PLCScada.Infrastructure.Communication;
public sealed class IoTClientConnectionFactory : IModbusConnectionFactory
{
    public IModbusConnection Create(Device device) => new IoTClientConnection(device);
}
// The coordinator serializes calls per device. SendPackageSingle deliberately bypasses
// IoTClient's automatic reconnect/replay. Never abandon a synchronous socket call and reuse it.
public sealed class IoTClientConnection(Device device) : IModbusConnection
{
    private readonly ModbusTcpClient _client = new(device.IpAddress, device.Port, device.ConnectionTimeout);
    private ushort _transaction;
    private byte[] NextTransaction() { var id = unchecked(++_transaction); return [(byte)(id >> 8), (byte)id]; }
    public async Task OpenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await Task.Run(() => _client.Open(), CancellationToken.None);
        if (!result.IsSucceed) throw new IOException(result.Err);
        ct.ThrowIfCancellationRequested();
    }
    public async Task<byte[]> ReadAsync(byte unitId, AddressType type, ushort address, ushort count, CancellationToken ct)
    {
        var function = type switch { AddressType.Coil => 1, AddressType.DiscreteInput => 2, AddressType.HoldingRegister => 3, AddressType.InputRegister => 4, _ => throw new ArgumentOutOfRangeException(nameof(type)) };
        if (count == 0 || count > (function <= 2 ? 2000 : 125) || address + count > 65536) throw new ArgumentOutOfRangeException(nameof(count));
        var expected = function <= 2 ? (count + 7) / 8 : count * 2;
        var command = _client.GetReadCommand(address.ToString(), unitId, (byte)function, count, NextTransaction());
        var response = await ExchangeAsync(command, expected + 9, ct);
        if (response[8] != expected) throw new IOException("Modbus 字节计数无效。");
        return response[9..]; // Network byte order, without IoTClient Read()'s implicit reversal.
    }
    public async Task WriteCoilAsync(byte unitId, ushort address, bool value, CancellationToken ct)
    {
        var command = _client.GetWriteCoilCommand(address.ToString(), value, unitId, 5, NextTransaction());
        await WriteEchoAsync(command, ct);
    }
    public async Task WriteSingleRegisterAsync(byte unitId, ushort address, ushort value, CancellationToken ct)
    {
        byte[] bytes = [(byte)(value >> 8), (byte)value];
        var command = _client.GetWriteCommand(address.ToString(), bytes, unitId, 6, NextTransaction());
        await WriteEchoAsync(command, ct);
    }
    public async Task WriteRegistersAsync(byte unitId, ushort address, byte[] networkBytes, CancellationToken ct)
    {
        if (networkBytes.Length is < 2 or > 246 || networkBytes.Length % 2 != 0 || address + networkBytes.Length / 2 > 65536) throw new ArgumentOutOfRangeException(nameof(networkBytes));
        var command = _client.GetWriteCommand(address.ToString(), networkBytes, unitId, 16, NextTransaction());
        await WriteEchoAsync(command, ct);
    }
    public async Task WriteCoilsAsync(byte unitId, ushort address, bool[] values, CancellationToken ct)
    {
        if (values.Length is < 1 or > 1968 || address + values.Length > 65536) throw new ArgumentOutOfRangeException(nameof(values));
        // IoTClient 1.0.48 has no FC15 command builder; construct the standard PDU,
        // then use the same IoTClient single-request socket transport and response checks.
        var byteCount = (values.Length + 7) / 8;
        var command = new byte[13 + byteCount]; NextTransaction().CopyTo(command, 0);
        BinaryPrimitives.WriteUInt16BigEndian(command.AsSpan(4), (ushort)(7 + byteCount));
        command[6] = unitId; command[7] = 15;
        BinaryPrimitives.WriteUInt16BigEndian(command.AsSpan(8), address);
        BinaryPrimitives.WriteUInt16BigEndian(command.AsSpan(10), (ushort)values.Length);
        command[12] = (byte)byteCount;
        for (var i = 0; i < values.Length; i++) if (values[i]) command[13 + i / 8] |= (byte)(1 << (i % 8));
        await WriteEchoAsync(command, ct);
    }
    private async Task WriteEchoAsync(byte[] command, CancellationToken ct)
    {
        var response = await ExchangeAsync(command, 12, ct);
        if (!response.AsSpan(8, 4).SequenceEqual(command.AsSpan(8, 4))) throw new IOException("Modbus 写入回执地址或数值不匹配，设备结果未知。");
    }
    private async Task<byte[]> ExchangeAsync(byte[] command, int expectedLength, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await Task.Run(() => _client.SendPackageSingle(command), CancellationToken.None);
        if (!result.IsSucceed) throw new IOException(result.Err);
        ct.ThrowIfCancellationRequested();
        var response = result.Value;
        if (response is null || response.Length < 9 || !response.AsSpan(0, 4).SequenceEqual(command.AsSpan(0, 4)) ||
            response[6] != command[6] || BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4)) != response.Length - 6)
            throw new IOException("Modbus MBAP、事务号或站号无效。");
        if (response[7] == (command[7] | 0x80)) throw new IOException($"Modbus 设备异常，功能码 {command[7]:X2}，异常码 {response[8]:X2}。");
        if (response[7] != command[7] || response.Length != expectedLength) throw new IOException("Modbus 响应功能码或长度无效。");
        return response;
    }
    public ValueTask DisposeAsync() { _client.Close(); return ValueTask.CompletedTask; }
}
