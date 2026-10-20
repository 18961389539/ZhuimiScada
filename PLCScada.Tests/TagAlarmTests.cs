using System.ComponentModel.DataAnnotations;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class TagAlarmTests
{
    [Fact]
    public async Task LimitAlarmsRaiseAcknowledgeAndClearWithDeadbandHysteresis()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "压力上限报警",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 10,
            DataType = TagDataType.UInt16,
            AccessMode = AccessMode.ReadOnly,
            MinValue = 0,
            MaxValue = 5000,
            AlarmHigh = 1000,
            AlarmLow = 200,
            AlarmDeadband = 100
        };
        await system.Tags.SaveAsync(tag);

        // 初始值 1234 ≥ 上限 1000：触发高限报警
        var first = await system.Runtime.GetTagValueAsync(tag.Id);
        Assert.Equal(TagQuality.Good, first.Quality);
        var highAlarm = Assert.Single(await system.Store.GetAlarmsAsync());
        Assert.StartsWith($"Tag:{tag.Id}", highAlarm.Source);
        Assert.Equal(AlarmStatus.Active, highAlarm.Status);
        Assert.StartsWith("压力上限报警", highAlarm.Message);
        Assert.Contains("达到或高于报警上限", highAlarm.Message);
        Assert.Contains("1000", highAlarm.Message);

        // 仍超限的重复读取不产生新记录
        await system.Runtime.GetTagValueAsync(tag.Id);
        Assert.Single(await system.Store.GetAlarmsAsync());

        // 人工确认后维持激活状态
        await system.Store.AcknowledgeAlarmAsync(highAlarm.AlarmId);
        Assert.Equal(AlarmStatus.Acknowledged, Assert.Single(await system.Store.GetAlarmsAsync()).Status);

        // 950 落在死区内（≥ 1000 − 100）：保持报警，不解除
        await system.Communication.WriteSingleRegisterAsync(system.Device, 10, 950);
        Assert.Equal(950d, (await system.Runtime.GetTagValueAsync(tag.Id)).CurrentValue);
        Assert.Equal(AlarmStatus.Acknowledged, Assert.Single(await system.Store.GetAlarmsAsync()).Status);

        // 899 < 900：跨过死区，解除高限报警（此前已人工确认 → 直接置已解除）
        await system.Communication.WriteSingleRegisterAsync(system.Device, 10, 899);
        await system.Runtime.GetTagValueAsync(tag.Id);
        Assert.Equal(AlarmStatus.Cleared, Assert.Single(await system.Store.GetAlarmsAsync()).Status);

        // 100 ≤ 下限 200：触发低限报警（新记录）
        await system.Communication.WriteSingleRegisterAsync(system.Device, 10, 100);
        await system.Runtime.GetTagValueAsync(tag.Id);
        var alarms = await system.Store.GetAlarmsAsync();
        Assert.Equal(2, alarms.Count);
        var lowAlarm = Assert.Single(alarms, x => x.Status is not (AlarmStatus.Cleared or AlarmStatus.ClearedUnacknowledged));
        Assert.Contains("达到或低于报警下限", lowAlarm.Message);

        // 250 落在死区内（≤ 200 + 100）：保持低限报警
        await system.Communication.WriteSingleRegisterAsync(system.Device, 10, 250);
        await system.Runtime.GetTagValueAsync(tag.Id);
        Assert.Contains(await system.Store.GetAlarmsAsync(), x => x.Status == AlarmStatus.Active);

        // 301 > 300：解除低限报警
        await system.Communication.WriteSingleRegisterAsync(system.Device, 10, 301);
        await system.Runtime.GetTagValueAsync(tag.Id);
        var finalAlarms = await system.Store.GetAlarmsAsync();
        Assert.Contains(finalAlarms, x => x.Message.Contains("上限") && x.Status == AlarmStatus.Cleared);
        Assert.Contains(finalAlarms, x => x.Message.Contains("下限") && x.Status == AlarmStatus.ClearedUnacknowledged);

        // 正常值读取不产生新记录
        await system.Communication.WriteSingleRegisterAsync(system.Device, 10, 500);
        await system.Runtime.GetTagValueAsync(tag.Id);
        Assert.Equal(2, (await system.Store.GetAlarmsAsync()).Count);
    }

    [Fact]
    public async Task LimitAlarmConfigurationIsValidated()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);

        Tag Build(string name) => new()
        {
            Name = name,
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.UInt16,
            AccessMode = AccessMode.ReadOnly,
            MinValue = 0,
            MaxValue = 100
        };

        var inverted = Build("上下限反置");
        inverted.AlarmLow = 60;
        inverted.AlarmHigh = 50;
        await Assert.ThrowsAsync<ValidationException>(() => system.Tags.SaveAsync(inverted));

        var negativeDeadband = Build("负死区");
        negativeDeadband.AlarmDeadband = -1;
        await Assert.ThrowsAsync<ValidationException>(() => system.Tags.SaveAsync(negativeDeadband));

        var highBeyondRange = Build("上限超量程");
        highBeyondRange.AlarmHigh = 150;
        await Assert.ThrowsAsync<ValidationException>(() => system.Tags.SaveAsync(highBeyondRange));

        var lowBelowRange = Build("下限低于量程");
        lowBelowRange.AlarmLow = -5;
        await Assert.ThrowsAsync<ValidationException>(() => system.Tags.SaveAsync(lowBelowRange));

        var boolWithLimit = new Tag
        {
            Name = "Bool 带限值",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 20,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite,
            AlarmHigh = 1
        };
        await Assert.ThrowsAsync<ValidationException>(() => system.Tags.SaveAsync(boolWithLimit));

        var valid = Build("合法限值");
        valid.AlarmHigh = 90;
        valid.AlarmLow = 10;
        valid.AlarmDeadband = 2;
        await system.Tags.SaveAsync(valid);
        var saved = (await system.Tags.GetAllAsync()).Single(x => x.Id == valid.Id);
        Assert.Equal(90, saved.AlarmHigh);
        Assert.Equal(10, saved.AlarmLow);
        Assert.Equal(2, saved.AlarmDeadband);
    }

    [Fact]
    public async Task RemovingLimitsWhileAlarmActiveClearsItOnNextRead()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "限值移除测试",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 10,
            DataType = TagDataType.UInt16,
            AccessMode = AccessMode.ReadOnly,
            AlarmHigh = 1000
        };
        await system.Tags.SaveAsync(tag);

        await system.Runtime.GetTagValueAsync(tag.Id);
        Assert.Equal(AlarmStatus.Active, Assert.Single(await system.Store.GetAlarmsAsync()).Status);

        var saved = (await system.Tags.GetAllAsync()).Single(x => x.Id == tag.Id);
        saved.AlarmHigh = null;
        await system.Tags.SaveAsync(saved);

        await system.Runtime.GetTagValueAsync(tag.Id);
        Assert.Equal(AlarmStatus.ClearedUnacknowledged, Assert.Single(await system.Store.GetAlarmsAsync()).Status);

        // 再次读取不产生新记录
        await system.Runtime.GetTagValueAsync(tag.Id);
        Assert.Single(await system.Store.GetAlarmsAsync());
    }

    [Fact]
    public async Task OutOfEngineeringRangeReadStillEvaluatesLimits()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "越量程评估",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 10,
            DataType = TagDataType.UInt16,
            AccessMode = AccessMode.ReadOnly,
            MinValue = 0,
            MaxValue = 2000,
            AlarmHigh = 1500
        };
        await system.Tags.SaveAsync(tag);

        var normal = await system.Runtime.GetTagValueAsync(tag.Id);
        Assert.Equal(TagQuality.Good, normal.Quality);
        Assert.Empty(await system.Store.GetAlarmsAsync());

        await system.Communication.WriteSingleRegisterAsync(system.Device, 10, 5000);
        var outOfRange = await system.Runtime.GetTagValueAsync(tag.Id);
        Assert.Equal(TagQuality.Bad, outOfRange.Quality);
        Assert.Contains("高于最大值", outOfRange.LastError);
        var alarm = Assert.Single(await system.Store.GetAlarmsAsync());
        Assert.Equal(AlarmStatus.Active, alarm.Status);
        Assert.Contains("达到或高于报警上限", alarm.Message);
    }
}
