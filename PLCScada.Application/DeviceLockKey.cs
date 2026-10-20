using System.Net;
using PLCScada.Core;

namespace PLCScada.Application;

// 跨实例写入保护使用的资源键：同一 PLC 连接目标必须映射为同一个键。
public static class DeviceLockKey
{
    public static string For(Device device)
    {
        var host = device.IpAddress.Trim();
        if (IPAddress.TryParse(host, out var address))
            host = address.ToString();
        else
            host = host.ToLowerInvariant();

        return $"{device.Protocol.Trim().ToUpperInvariant()}|{host}|{device.Port}|{device.UnitId}";
    }
}
