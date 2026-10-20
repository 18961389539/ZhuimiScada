using System.ComponentModel.DataAnnotations;
using System.Net;
using PLCScada.Core;

namespace PLCScada.Application;

public sealed class DeviceService(IScadaStore store, IDeviceCommunication communication)
{
    public Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) => store.GetDevicesAsync(ct);
    public DeviceSnapshot GetState(Guid id) => communication.GetSnapshot(id);
    public async Task SaveAsync(Device input, CancellationToken ct = default)
    {
        var device = input.Copy();
        device.Name = device.Name.Trim();
        device.IpAddress = device.IpAddress.Trim();
        device.Protocol = "Modbus TCP";
        Validator.ValidateObject(device, new ValidationContext(device), true);
        if (!IPAddress.TryParse(device.IpAddress, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ValidationException("请输入合法的 IPv4 地址。");
        await store.SaveDeviceAsync(device, ct);
        await communication.InvalidateAsync(device.Id, ct);
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await store.DeleteDeviceAsync(id, ct);
        await communication.DisconnectAsync(id, ct);
    }
    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        var device = await RequiredAsync(id, ct);
        device.Enabled = enabled;
        await SaveAsync(device, ct);
    }
    public async Task ConnectAsync(Guid id, CancellationToken ct = default)
    {
        var device = await RequiredAsync(id, ct);
        if (!device.Enabled) throw new InvalidOperationException("请先启用设备。");
        await communication.ConnectAsync(device, ct);
    }
    public async Task DisconnectAsync(Guid id, CancellationToken ct = default)
    {
        var device = await RequiredAsync(id, ct);
        await communication.DisconnectAsync(id, ct);
        await store.SetCommunicationAlarmAsync(device.Id, device.Name, null, ct);
        await store.LogAsync(device.Name, "用户手动断开连接；自动重连已暂停，点击连接恢复。", ct: ct);
    }
    public async Task<CommunicationTestResult> TestAsync(Guid id, CancellationToken ct = default)
        => await communication.TestAsync(await RequiredAsync(id, ct), ct);
    private async Task<Device> RequiredAsync(Guid id, CancellationToken ct)
        => await store.GetDeviceAsync(id, ct) ?? throw new InvalidOperationException("设备已不存在，请刷新列表。");
}
