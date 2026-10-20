using System.Net;
using System.Net.Sockets;
using PLCScada.Core;
using PLCScada.Infrastructure.Communication;

namespace PLCScada.Tests;

public sealed class ProtocolValidationTests
{
    [Theory]
    [InlineData("transaction")] [InlineData("unit")] [InlineData("function")] [InlineData("byteCount")]
    public async Task InvalidResponsesCannotAppearAsSuccessfulReads(string fault)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(); var stream = peer.GetStream();
            var request = new byte[12]; await stream.ReadExactlyAsync(request);
            byte[] response = [request[0], request[1], 0, 0, 0, 5, request[6], request[7], 2, 0x04, 0xd2];
            switch (fault) { case "transaction": response[1]++; break; case "unit": response[6]++; break; case "function": response[7]++; break; case "byteCount": response[8] = 1; break; }
            await stream.WriteAsync(response);
        });
        await using var client = new IoTClientConnection(new() { Port = port, ConnectionTimeout = 300 });
        await client.OpenAsync(default);
        await Assert.ThrowsAsync<IOException>(() => client.ReadAsync(1, AddressType.HoldingRegister, 10, 1, default));
        await server;
    }
    [Fact]
    public async Task WriteAcknowledgementMustEchoAddressAndValue()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(); var stream = peer.GetStream();
            var request = new byte[12]; await stream.ReadExactlyAsync(request);
            request[9]++; await stream.WriteAsync(request);
        });
        await using var client = new IoTClientConnection(new() { Port = port, ConnectionTimeout = 300 });
        await client.OpenAsync(default);
        await Assert.ThrowsAsync<IOException>(() => client.WriteSingleRegisterAsync(1, 10, 100, default));
        await server;
    }
    [Fact]
    public async Task DroppedWriteResponseNeverAutomaticallyReconnectsOrReplays()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            var request = new byte[12]; await peer.GetStream().ReadExactlyAsync(request);
            // Simulate a PLC that executed the command, then lost its response.
        });
        await using var client = new IoTClientConnection(new() { Port = port, ConnectionTimeout = 250 });
        await client.OpenAsync(default);
        await Assert.ThrowsAsync<IOException>(() => client.WriteSingleRegisterAsync(1, 10, 100, default));
        await server;
        Assert.False(listener.Pending());
    }
}
