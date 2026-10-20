using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class SnapshotRuleTests
{
    [Fact]
    public async Task RuleValidationRejectsInvalidTriggerAndEmptyPoints()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var trigger = new Tag
        {
            Name = "触发位", DeviceId = system.Device.Id, AddressType = AddressType.Coil,
            Address = 700, DataType = TagDataType.Bool, AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(trigger);
        var numeric = new Tag
        {
            Name = "数值触发", DeviceId = system.Device.Id, AddressType = AddressType.HoldingRegister,
            Address = 200, DataType = TagDataType.UInt16, AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(numeric);

        var badTrigger = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SaveSnapshotRuleAsync(
                new SnapshotRule { Name = "错误触发", TriggerTagId = numeric.Id }, [new SnapshotPointInput(numeric.Id)]));
        Assert.Contains("线圈", badTrigger.Message);

        var noPoints = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SaveSnapshotRuleAsync(new SnapshotRule { Name = "无点位", TriggerTagId = trigger.Id }, []));
        Assert.Contains("至少选择一个抓取点位", noPoints.Message);

        await system.Store.SaveSnapshotRuleAsync(
            new SnapshotRule { Name = "测试快照", TriggerTagId = trigger.Id }, [new SnapshotPointInput(numeric.Id)]);
        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SaveSnapshotRuleAsync(
                new SnapshotRule { Name = "测试快照", TriggerTagId = trigger.Id }, [new SnapshotPointInput(numeric.Id)]));
        Assert.Contains("同名", duplicate.Message);
    }

    [Fact]
    public async Task SingleRuleSaveKeepsExactlyOneRule()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var triggerA = new Tag
        {
            Name = "触发A", DeviceId = system.Device.Id, AddressType = AddressType.Coil,
            Address = 710, DataType = TagDataType.Bool, AccessMode = AccessMode.ReadWrite
        };
        var triggerB = new Tag
        {
            Name = "触发B", DeviceId = system.Device.Id, AddressType = AddressType.Coil,
            Address = 711, DataType = TagDataType.Bool, AccessMode = AccessMode.ReadWrite
        };
        var point = new Tag
        {
            Name = "点位X", DeviceId = system.Device.Id, AddressType = AddressType.HoldingRegister,
            Address = 210, DataType = TagDataType.UInt16, AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(triggerA);
        await system.Tags.SaveAsync(triggerB);
        await system.Tags.SaveAsync(point);

        await system.Store.SaveSnapshotRuleAsync(
            new SnapshotRule { Name = "旧规则", TriggerTagId = triggerA.Id }, [new SnapshotPointInput(point.Id)]);
        await system.Store.SaveSingleSnapshotRuleAsync(
            new SnapshotRule { Name = "单规则", TriggerTagId = triggerB.Id }, [new SnapshotPointInput(point.Id)]);

        var rule = Assert.Single(await system.Store.GetSnapshotRulesAsync());
        Assert.Equal("单规则", rule.Name);
        Assert.Equal(triggerB.Id, rule.TriggerTagId);
        Assert.Single(rule.Points);
    }

    [Fact]
    public async Task CaptureSavesRecordAndResetsTrigger()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var trigger = new Tag
        {
            Name = "抓拍触发", DeviceId = system.Device.Id, AddressType = AddressType.Coil,
            Address = 701, DataType = TagDataType.Bool, AccessMode = AccessMode.ReadWrite
        };
        var signal = new Tag
        {
            Name = "测量值", DeviceId = system.Device.Id, AddressType = AddressType.HoldingRegister,
            Address = 201, DataType = TagDataType.UInt16, AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(trigger);
        await system.Tags.SaveAsync(signal);
        await system.Devices.ConnectAsync(system.Device.Id);
        await system.Communication.WriteSingleRegisterAsync(system.Device, 201, 321, CancellationToken.None);
        await system.Runtime.GetTagValueAsync(signal.Id);

        await system.Store.SaveSnapshotRuleAsync(
            new SnapshotRule { Name = "结束抓拍", TriggerTagId = trigger.Id }, [new SnapshotPointInput(signal.Id)]);
        var rule = (await system.Store.GetSnapshotRulesAsync()).Single();

        // 触发位为假时不抓取
        var capture = new SnapshotCaptureService(system.Store, system.Runtime, system.Communication);
        Assert.False(await capture.TryCaptureAsync(rule, "SN-T1"));

        // 置真 → 抓取 → 复位为假
        await system.Communication.WriteCoilAsync(system.Device, 701, true, CancellationToken.None);
        await system.Runtime.GetTagValueAsync(trigger.Id);
        Assert.True(await capture.TryCaptureAsync(rule, "SN-T1"));

        var record = Assert.Single((await system.Store.QuerySnapshotRecordsAsync(1, 50)).Items);
        Assert.Equal("结束抓拍", record.RuleName);
        Assert.Equal("SN-T1", record.Sn);
        Assert.Contains("测量值", record.ValuesJson);
        Assert.Contains("321", record.ValuesJson);

        // SN 与本次读取的点位值绑定到同一条 SN 记录
        Assert.NotNull(record.SnRecordId);
        var snRecord = Assert.Single(await system.Store.QuerySnRecordsAsync(null, 1, 50) is var p && p.TotalCount > 0
            ? (await system.Store.QuerySnRecordsAsync(null, 1, 50)).Items.Where(x => x.Id == record.SnRecordId)
            : []);
        Assert.Equal("SN-T1", snRecord.Sn);

        await system.Runtime.GetTagValueAsync(trigger.Id);
        Assert.False((bool)system.Runtime.GetTagSnapshot(trigger.Id).CurrentValue!);
    }
}