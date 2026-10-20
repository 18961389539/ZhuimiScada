using PLCScada.Core;

namespace PLCScada.Application;

public sealed record TagReadGroup(
    AddressType AddressType,
    int StartAddress,
    int RegisterCount,
    IReadOnlyList<Tag> Tags);

// 连续地址合并采集的分组规则：仅数值寄存器（Holding/Input Register）参与；
// 相邻或重叠（T3 允许的别名）的区间聚合为一个请求，跨度上限 120 寄存器（Modbus ≤125），
// 空隙超过 8 寄存器就分组，避免为个别分散 Tag 读取大段无用寄存器。
// 采集间隔计算：全局统一扫描间隔，最终不小于设备轮询周期。
public static class PollingMath
{
    public static int EffectiveTagIntervalMs(int globalScanInterval, int devicePollInterval)
        => Math.Max(globalScanInterval, devicePollInterval);
}

public sealed record TagBitGroup(
    AddressType AddressType,
    int StartAddress,
    int BitCount,
    IReadOnlyList<Tag> Tags);

public static class TagReadGrouping
{
    public const int MaxGroupRegisters = 120;
    public const int MaxGapRegisters = 8;
    public const int MaxBitGroupSpan = 512;

    public static IReadOnlyList<TagReadGroup> BuildGroups(IReadOnlyList<Tag> tags)
    {
        var groups = new List<TagReadGroup>();
        foreach (var byType in tags.GroupBy(tag => tag.AddressType).OrderBy(x => x.Key))
        {
            var ordered = byType.OrderBy(tag => tag.Address).ThenBy(tag => tag.Id).ToArray();
            var current = new List<Tag>();
            var start = 0;
            var end = 0;
            foreach (var tag in ordered)
            {
                var tagEnd = tag.Address + TagValueCodec.RegisterCount(tag);
                if (current.Count == 0)
                {
                    current.Add(tag);
                    start = tag.Address;
                    end = tagEnd;
                    continue;
                }

                if (tag.Address - end > MaxGapRegisters || tagEnd - start > MaxGroupRegisters)
                {
                    groups.Add(new(byType.Key, start, end - start, current));
                    current = [tag];
                    start = tag.Address;
                    end = tagEnd;
                    continue;
                }

                current.Add(tag);
                end = Math.Max(end, tagEnd);
            }

            if (current.Count > 0)
                groups.Add(new(byType.Key, start, end - start, current));
        }
        return groups;
    }

    // 位（Coil / DiscreteInput）分组：一次 FC01/FC02 读一段连续位，按位解码回各 Tag。
    public static IReadOnlyList<TagBitGroup> BuildBitGroups(IReadOnlyList<Tag> tags)
    {
        var groups = new List<TagBitGroup>();
        foreach (var byType in tags.GroupBy(tag => tag.AddressType).OrderBy(x => x.Key))
        {
            var ordered = byType.OrderBy(tag => tag.Address).ThenBy(tag => tag.Id).ToArray();
            var current = new List<Tag>();
            var start = 0;
            var end = 0;
            foreach (var tag in ordered)
            {
                var tagEnd = tag.Address + 1;
                if (current.Count == 0)
                {
                    current.Add(tag);
                    start = tag.Address;
                    end = tagEnd;
                    continue;
                }
                if (tag.Address - end > MaxGapRegisters || tagEnd - start > MaxBitGroupSpan)
                {
                    groups.Add(new(byType.Key, start, end - start, current));
                    current = [tag];
                    start = tag.Address;
                    end = tagEnd;
                    continue;
                }
                current.Add(tag);
                end = Math.Max(end, tagEnd);
            }
            if (current.Count > 0)
                groups.Add(new(byType.Key, start, end - start, current));
        }
        return groups;
    }
}
