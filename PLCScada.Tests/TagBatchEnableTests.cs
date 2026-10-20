using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class TagBatchEnableTests
{
    [Fact]
    public async Task BatchDisablePersistsAndReportsIndividualFailures()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var first = await SaveTagAsync(system, "批量点一", 100);
        var second = await SaveTagAsync(system, "批量点二", 102);
        // 直接经存储写入一行非法数据（Min > Max），模拟绕过校验的历史数据
        var invalid = new Tag
        {
            Name = "批量点坏",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 104,
            DataType = TagDataType.UInt16,
            AccessMode = AccessMode.ReadWrite,
            MinValue = 100,
            MaxValue = 0
        };
        await system.Store.SaveTagAsync(invalid);
        var service = new TagService(system.Store);

        var result = await service.SetEnabledAsync([first.Id, second.Id, invalid.Id], enabled: false);

        Assert.Equal(2, result.Succeeded);
        var failure = Assert.Single(result.Failed);
        Assert.Equal("批量点坏", failure.Name);
        Assert.Contains("不合法", failure.Error);
        var tags = (await service.GetAllAsync()).ToDictionary(x => x.Name);
        Assert.False(tags["批量点一"].Enabled);
        Assert.False(tags["批量点二"].Enabled);
        Assert.True(tags["批量点坏"].Enabled);
        Assert.Equal(2, (await system.Store.GetLogsAsync())
            .Count(x => x.Message.Contains("修改 Tag 配置")));

        var reenabled = await service.SetEnabledAsync([first.Id], enabled: true);
        Assert.Equal(1, reenabled.Succeeded);
        Assert.True((await service.GetAllAsync()).Single(x => x.Id == first.Id).Enabled);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetEnabledAsync([first.Id, Guid.NewGuid()], enabled: false));

        var empty = await service.SetEnabledAsync([], enabled: false);
        Assert.Equal(0, empty.Succeeded);
        Assert.Empty(empty.Failed);
    }

    private static async Task<Tag> SaveTagAsync(TestSystem system, string name, int address)
    {
        var tag = new Tag
        {
            Name = name,
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = address,
            DataType = TagDataType.UInt16,
            AccessMode = AccessMode.ReadWrite,
            MinValue = 0,
            MaxValue = 1000
        };
        await system.Tags.SaveAsync(tag);
        return tag;
    }
}
