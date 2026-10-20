using PLCScada.Core;

namespace PLCScada.Application;

// Modbus 地址范围计算与重叠检测：只有同地址类型（寄存器/线圈完全独立）才可能重叠；
// Bool/Coil 按 1 个地址计，数值类型按寄存器字数计。
public static class TagAddressRange
{
    public static int End(Tag tag) => tag.Address + TagValueCodec.RegisterCount(tag);

    public static bool Overlaps(Tag left, Tag right)
        => left.AddressType == right.AddressType &&
           left.Address < End(right) && right.Address < End(left);
}
