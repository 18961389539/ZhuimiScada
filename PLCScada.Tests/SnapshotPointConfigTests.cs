using System.Text.Json;
using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

// 采集项口径（别名/单位/小数位/必填/顺序）：保存往返、抓取格式化、历史 JSON 兼容。
public sealed class SnapshotPointConfigTests
{
    [Fact]
    public async Task PointMetadataRoundTripKeepsOrderAndFields()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var trigger = new Tag
        {
            Name = "触发位", DeviceId = system.Device.Id, AddressType = AddressType.Coil,
            Address = 720, DataType = TagDataType.Bool, AccessMode = AccessMode.ReadWrite
        };
        var voltage = new Tag
        {
            Name = "电压", DeviceId = system.Device.Id, AddressType = AddressType.HoldingRegister,
            Address = 220, DataType = TagDataType.Float32, AccessMode = AccessMode.ReadWrite
        };
        var verdict = new Tag
        {
            Name = "总判定", DeviceId = system.Device.Id, AddressType = AddressType.HoldingRegister,
            Address = 230, DataType = TagDataType.UInt16, AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(trigger);
        await system.Tags.SaveAsync(voltage);
        await system.Tags.SaveAsync(verdict);

        // 故意与名称字典序相反，验证保存顺序被保留
        await system.Store.SaveSnapshotRuleAsync(
            new SnapshotRule { Name = "口径往返", TriggerTagId = trigger.Id },
            [
                new SnapshotPointInput(voltage.Id, "Voltage", "V", 3, true),
                new SnapshotPointInput(verdict.Id, "Verdict", null, 0, false)
            ]);

        var rule = Assert.Single(await system.Store.GetSnapshotRulesAsync());
        Assert.Equal(["Voltage", "Verdict"], rule.Points.Select(x => x.Alias));
        Assert.Equal(["V", null], rule.Points.Select(x => x.Unit));
        Assert.Equal([3, 0], rule.Points.Select(x => x.Decimals));
        Assert.Equal([true, false], rule.Points.Select(x => x.Required));
        Assert.Equal([0, 1], rule.Points.Select(x => x.SortOrder));
    }

    [Fact]
    public async Task InvalidPointMetadataIsRejected()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var trigger = new Tag
        {
            Name = "触发位2", DeviceId = system.Device.Id, AddressType = AddressType.Coil,
            Address = 721, DataType = TagDataType.Bool, AccessMode = AccessMode.ReadWrite
        };
        var point = new Tag
        {
            Name = "点位", DeviceId = system.Device.Id, AddressType = AddressType.HoldingRegister,
            Address = 240, DataType = TagDataType.UInt16, AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(trigger);
        await system.Tags.SaveAsync(point);

        var badDecimals = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SaveSnapshotRuleAsync(
                new SnapshotRule { Name = "小数越界", TriggerTagId = trigger.Id },
                [new SnapshotPointInput(point.Id, null, null, 7, true)]));
        Assert.Contains("小数位", badDecimals.Message);

        var badAlias = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SaveSnapshotRuleAsync(
                new SnapshotRule { Name = "别名超长", TriggerTagId = trigger.Id },
                [new SnapshotPointInput(point.Id, new string('x', 41), null, 2, true)]));
        Assert.Contains("别名", badAlias.Message);
    }

    [Fact]
    public async Task CaptureFormatsValueByPointMetadata()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var trigger = new Tag
        {
            Name = "抓拍触发2", DeviceId = system.Device.Id, AddressType = AddressType.Coil,
            Address = 722, DataType = TagDataType.Bool, AccessMode = AccessMode.ReadWrite
        };
        var signal = new Tag
        {
            Name = "实时电压", DeviceId = system.Device.Id, AddressType = AddressType.HoldingRegister,
            Address = 250, DataType = TagDataType.UInt16, AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(trigger);
        await system.Tags.SaveAsync(signal);
        await system.Devices.ConnectAsync(system.Device.Id);
        await system.Communication.WriteSingleRegisterAsync(system.Device, 250, 321, CancellationToken.None);
        await system.Runtime.GetTagValueAsync(signal.Id);

        await system.Store.SaveSnapshotRuleAsync(
            new SnapshotRule { Name = "口径抓拍", TriggerTagId = trigger.Id },
            [new SnapshotPointInput(signal.Id, "Voltage", "V", 1, true)]);
        var rule = (await system.Store.GetSnapshotRulesAsync()).Single();

        await system.Communication.WriteCoilAsync(system.Device, 722, true, CancellationToken.None);
        await system.Runtime.GetTagValueAsync(trigger.Id);
        var capture = new SnapshotCaptureService(system.Store, system.Runtime, system.Communication);
        Assert.True(await capture.TryCaptureAsync(rule, "SN-FMT-1"));

        var record = Assert.Single((await system.Store.QuerySnapshotRecordsAsync(1, 50)).Items);
        var item = Assert.Single(JsonSerializer.Deserialize<List<SnapshotValueItem>>(record.ValuesJson)!);
        Assert.Equal("Voltage", item.Name);
        Assert.Equal("321.0 V", item.Value);
        Assert.Equal("V", item.Unit);
        Assert.True(item.Required);
    }

    [Fact]
    public void LegacySnapshotJsonWithoutMetadataIsStillReadable()
    {
        const string legacy = """
            [{"TagId":"11111111-1111-1111-1111-111111111111","Name":"测量值","Value":"321","Quality":"Good"}]
            """;
        var item = Assert.Single(JsonSerializer.Deserialize<List<SnapshotValueItem>>(legacy)!);
        Assert.Equal("测量值", item.Name);
        Assert.Equal("321", item.Value);
        Assert.Null(item.Unit);
        Assert.False(item.Required);
    }

    [Fact]
    public async Task PreviewDoesNotPersistAndDoesNotTouchTrigger()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var trigger = new Tag
        {
            Name = "预览触发", DeviceId = system.Device.Id, AddressType = AddressType.Coil,
            Address = 730, DataType = TagDataType.Bool, AccessMode = AccessMode.ReadWrite
        };
        var signal = new Tag
        {
            Name = "实时电流", DeviceId = system.Device.Id, AddressType = AddressType.HoldingRegister,
            Address = 260, DataType = TagDataType.UInt16, AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(trigger);
        await system.Tags.SaveAsync(signal);
        await system.Devices.ConnectAsync(system.Device.Id);
        await system.Communication.WriteSingleRegisterAsync(system.Device, 260, 824, CancellationToken.None);
        await system.Runtime.GetTagValueAsync(signal.Id);

        await system.Store.SaveSnapshotRuleAsync(
            new SnapshotRule { Name = "预览规则", TriggerTagId = trigger.Id },
            [new SnapshotPointInput(signal.Id, "Current", "A", 3, true)]);
        var rule = (await system.Store.GetSnapshotRulesAsync()).Single();
        var capture = new SnapshotCaptureService(system.Store, system.Runtime, system.Communication);

        var preview = await capture.PreviewAsync(rule, "SN-DRY-1");
        Assert.Equal("SN-DRY-1", preview.Sn);
        Assert.Equal("Current", Assert.Single(preview.Values).Name);
        Assert.Equal("824.000 A", Assert.Single(preview.Values).Value);
        Assert.Equal(0, preview.MissingRequiredCount);

        // 试抓不落库
        Assert.Empty((await system.Store.QuerySnapshotRecordsAsync(1, 50)).Items);

        // 提交预览 → 落库并绑定 SN 记录；不触碰 PLC（触发位保持 false）
        var recordId = await capture.CommitAsync(preview);
        var stored = Assert.Single((await system.Store.QuerySnapshotRecordsAsync(1, 50)).Items);
        Assert.Equal(recordId, stored.Id);
        Assert.Equal("SN-DRY-1", stored.Sn);
        Assert.NotNull(stored.SnRecordId);
        Assert.False((bool)(system.Runtime.GetTagSnapshot(trigger.Id).CurrentValue ?? false));
    }

    [Fact]
    public async Task PreviewMarksMissingRequiredItems()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var trigger = new Tag
        {
            Name = "预览触发2", DeviceId = system.Device.Id, AddressType = AddressType.Coil,
            Address = 731, DataType = TagDataType.Bool, AccessMode = AccessMode.ReadWrite
        };
        // 未连接设备 → 点位没有读数（质量 Unknown/无值）
        var signal = new Tag
        {
            Name = "峰值电流", DeviceId = system.Device.Id, AddressType = AddressType.HoldingRegister,
            Address = 261, DataType = TagDataType.UInt16, AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(trigger);
        await system.Tags.SaveAsync(signal);

        await system.Store.SaveSnapshotRuleAsync(
            new SnapshotRule { Name = "缺数规则", TriggerTagId = trigger.Id },
            [new SnapshotPointInput(signal.Id, null, null, 0, true)]);
        var rule = (await system.Store.GetSnapshotRulesAsync()).Single();
        var capture = new SnapshotCaptureService(system.Store, system.Runtime, system.Communication);

        var preview = await capture.PreviewAsync(rule, null);
        var item = Assert.Single(preview.Values);
        Assert.Equal("", item.Value);
        Assert.NotEqual("Good", item.Quality);
        Assert.Equal(1, preview.MissingRequiredCount);
        Assert.Null(preview.Sn);
    }
}
