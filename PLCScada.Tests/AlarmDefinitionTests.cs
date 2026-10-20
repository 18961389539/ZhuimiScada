using Microsoft.Extensions.DependencyInjection;
using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class AlarmDefinitionTests
{
    [Fact]
    public async Task ServiceValidationRejectsInvalidCombinations()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var coil = await SaveTagAsync(system, "急停信号", AddressType.Coil, 20, TagDataType.Bool);
        var status = await SaveTagAsync(system, "状态字", AddressType.HoldingRegister, 100, TagDataType.Int16);
        var definitions = new AlarmDefinitionService(system.Store);

        var bitOnNumeric = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            definitions.SaveAsync(new AlarmDefinition
            {
                TagId = status.Id, Name = "错误组合", Kind = AlarmDefinitionKind.Bit
            }));
        Assert.Contains("位报警只能绑定 Bool", bitOnNumeric.Message);

        var enumOnBool = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            definitions.SaveAsync(new AlarmDefinition
            {
                TagId = coil.Id, Name = "错误组合", Kind = AlarmDefinitionKind.Enum, EnumValues = "1"
            }));
        Assert.Contains("只能绑定整数类型", enumOnBool.Message);

        var badEnum = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            definitions.SaveAsync(new AlarmDefinition
            {
                TagId = status.Id, Name = "坏枚举", Kind = AlarmDefinitionKind.Enum, EnumValues = "a,b"
            }));
        Assert.Contains("不是合法数值", badEnum.Message);

        await definitions.SaveAsync(new AlarmDefinition
        {
            TagId = coil.Id, Name = "急停", Kind = AlarmDefinitionKind.Bit, Message = "急停：{tagName}",
            Severity = "Error"
        });
        var saved = Assert.Single(await definitions.GetAllAsync());
        Assert.Equal("急停", saved.Name);
        Assert.Equal("急停信号", saved.Tag?.Name);

        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            definitions.SaveAsync(new AlarmDefinition
            {
                TagId = coil.Id, Name = "急停", Kind = AlarmDefinitionKind.Bit
            }));
        Assert.Contains("同名", duplicate.Message);
    }

    [Fact]
    public async Task BitDefinitionRaisesAndClearsWithRenderedMessage()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveTagAsync(system, "急停信号", AddressType.Coil, 20, TagDataType.Bool);
        await system.Devices.ConnectAsync(system.Device.Id);
        var definitions = new AlarmDefinitionService(system.Store);
        await definitions.SaveAsync(new AlarmDefinition
        {
            TagId = tag.Id, Name = "急停", Kind = AlarmDefinitionKind.Bit, BitTarget = true,
            Message = "急停：{tagName} = {value}", Severity = "Error"
        });
        var runtime = system.Services.GetRequiredService<TagRuntime>();

        await system.Communication.WriteCoilAsync(system.Device, 20, false, CancellationToken.None);
        await runtime.GetTagValueAsync(tag.Id);
        Assert.Empty(await DefinitionAlarmsAsync(system));

        await system.Communication.WriteCoilAsync(system.Device, 20, true, CancellationToken.None);
        await runtime.GetTagValueAsync(tag.Id);
        var alarm = Assert.Single(await DefinitionAlarmsAsync(system));
        Assert.Equal("Error", alarm.Severity);
        Assert.Equal(AlarmStatus.Active, alarm.Status);
        Assert.Contains("急停：急停信号 = 1", alarm.Message);

        await system.Communication.WriteCoilAsync(system.Device, 20, false, CancellationToken.None);
        await runtime.GetTagValueAsync(tag.Id);
        Assert.All(await DefinitionAlarmsAsync(system), x => Assert.Equal(AlarmStatus.ClearedUnacknowledged, x.Status));
    }

    [Fact]
    public async Task EnumDefinitionMatchesConfiguredValues()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveTagAsync(system, "故障码", AddressType.HoldingRegister, 100, TagDataType.Int16);
        await system.Devices.ConnectAsync(system.Device.Id);
        var definitions = new AlarmDefinitionService(system.Store);
        await definitions.SaveAsync(new AlarmDefinition
        {
            TagId = tag.Id, Name = "设备故障", Kind = AlarmDefinitionKind.Enum, EnumValues = "5,6",
            Message = "故障码 {value}", Severity = "Error"
        });
        var runtime = system.Services.GetRequiredService<TagRuntime>();

        await system.Communication.WriteSingleRegisterAsync(system.Device, 100, 7, CancellationToken.None);
        await runtime.GetTagValueAsync(tag.Id);
        Assert.Empty(await DefinitionAlarmsAsync(system));

        await system.Communication.WriteSingleRegisterAsync(system.Device, 100, 5, CancellationToken.None);
        await runtime.GetTagValueAsync(tag.Id);
        var alarm = Assert.Single(await DefinitionAlarmsAsync(system));
        Assert.Contains("故障码 5", alarm.Message);

        await system.Communication.WriteSingleRegisterAsync(system.Device, 100, 6, CancellationToken.None);
        await runtime.GetTagValueAsync(tag.Id);
        Assert.Single(await DefinitionAlarmsAsync(system));

        await system.Communication.WriteSingleRegisterAsync(system.Device, 100, 7, CancellationToken.None);
        await runtime.GetTagValueAsync(tag.Id);
        Assert.All(await DefinitionAlarmsAsync(system), x => Assert.Equal(AlarmStatus.ClearedUnacknowledged, x.Status));
    }

    [Fact]
    public async Task DelayRequiresPersistenceAndDisabledDefinitionNeverFires()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveTagAsync(system, "延时信号", AddressType.Coil, 21, TagDataType.Bool);
        await system.Devices.ConnectAsync(system.Device.Id);
        var definitions = new AlarmDefinitionService(system.Store);
        var definition = new AlarmDefinition
        {
            TagId = tag.Id, Name = "延时报警", Kind = AlarmDefinitionKind.Bit, DelaySeconds = 1,
            Message = "延时 {value}", Severity = "Warning"
        };
        await definitions.SaveAsync(definition);
        var runtime = system.Services.GetRequiredService<TagRuntime>();

        await system.Communication.WriteCoilAsync(system.Device, 21, true, CancellationToken.None);
        await runtime.GetTagValueAsync(tag.Id);
        Assert.Empty(await DefinitionAlarmsAsync(system));
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        await runtime.GetTagValueAsync(tag.Id);
        Assert.Single(await DefinitionAlarmsAsync(system));

        // 停用后新运行时（缓存未加载过）不再触发
        var disabled = (await definitions.GetAllAsync()).Single();
        disabled.Enabled = false;
        await definitions.SaveAsync(disabled);
        var freshRuntime = new TagRuntime(system.Store, system.Communication);
        var before = (await DefinitionAlarmsAsync(system)).Count;
        await freshRuntime.GetTagValueAsync(tag.Id);
        Assert.Equal(before, (await DefinitionAlarmsAsync(system)).Count);
    }

    [Fact]
    public async Task EnumRequiresIntegerTagsAndSupportsLabelSyntax()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var floatTag = await SaveTagAsync(system, "浮点状态", AddressType.HoldingRegister, 120, TagDataType.Float32);
        var intTag = await SaveTagAsync(system, "故障码", AddressType.HoldingRegister, 122, TagDataType.Int16);
        var definitions = new AlarmDefinitionService(system.Store);

        var floatRejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            definitions.SaveAsync(new AlarmDefinition
            {
                TagId = floatTag.Id, Name = "浮点枚举", Kind = AlarmDefinitionKind.Enum, EnumValues = "5"
            }));
        Assert.Contains("只能绑定整数类型", floatRejected.Message);

        await definitions.SaveAsync(new AlarmDefinition
        {
            TagId = intTag.Id, Name = "故障枚举", Kind = AlarmDefinitionKind.Enum,
            EnumValues = "6=堵转, 5=急停；7",
            Message = "{tagName}：{label}（值 {value}）"
        });
        var saved = Assert.Single(await definitions.GetAllAsync());
        Assert.Equal("5=急停,6=堵转,7", saved.EnumValues);

        var badLabel = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            definitions.SaveAsync(new AlarmDefinition
            {
                TagId = intTag.Id, Name = "坏文案", Kind = AlarmDefinitionKind.Enum, EnumValues = "5=急停,堵转"
            }));
        Assert.Contains("不是合法数值", badLabel.Message);
        var longLabel = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            definitions.SaveAsync(new AlarmDefinition
            {
                TagId = intTag.Id, Name = "超长文案", Kind = AlarmDefinitionKind.Enum,
                EnumValues = $"5={new string('甲', 51)}"
            }));
        Assert.Contains("长度必须为 1 到 50", longLabel.Message);
    }

    [Fact]
    public async Task EnumLabelRendersAndFloatTagsNeverFire()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var intTag = await SaveTagAsync(system, "故障码", AddressType.HoldingRegister, 130, TagDataType.Int16);
        var floatTag = await SaveTagAsync(system, "浮点状态", AddressType.HoldingRegister, 132, TagDataType.Float32);
        await system.Devices.ConnectAsync(system.Device.Id);
        var definitions = new AlarmDefinitionService(system.Store);
        await definitions.SaveAsync(new AlarmDefinition
        {
            TagId = intTag.Id, Name = "故障枚举", Kind = AlarmDefinitionKind.Enum,
            EnumValues = "5=急停,6=堵转", Message = "{tagName}：{label}（值 {value}）", Severity = "Error"
        });
        var runtime = system.Services.GetRequiredService<TagRuntime>();

        await system.Communication.WriteSingleRegisterAsync(system.Device, 130, 5, CancellationToken.None);
        await runtime.GetTagValueAsync(intTag.Id);
        var alarm = Assert.Single(await DefinitionAlarmsAsync(system));
        Assert.Contains("故障码：急停（值 5）", alarm.Message);

        await system.Communication.WriteSingleRegisterAsync(system.Device, 130, 7, CancellationToken.None);
        await runtime.GetTagValueAsync(intTag.Id);
        Assert.All(await DefinitionAlarmsAsync(system), x => Assert.Equal(AlarmStatus.ClearedUnacknowledged, x.Status));

        // 绕过服务直接写入的浮点枚举定义：运行时不触发（防四舍五入误命中）
        var legacy = new AlarmDefinition
        {
            TagId = floatTag.Id, Name = "浮点枚举", Kind = AlarmDefinitionKind.Enum,
            EnumValues = "5", Message = "浮点命中 {value}"
        };
        await system.Store.SaveAlarmDefinitionAsync(legacy);
        await system.Communication.WriteRegistersAsync(
            system.Device, 132, TagValueCodec.Encode(floatTag, 5.4), CancellationToken.None);
        var freshRuntime = new TagRuntime(system.Store, system.Communication);
        await freshRuntime.GetTagValueAsync(floatTag.Id);
        Assert.DoesNotContain(await DefinitionAlarmsAsync(system), x => x.Message.Contains("浮点命中"));
    }

    [Fact]
    public async Task ActiveEnumAlarmRefreshesMessageWhenValueChanges()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveTagAsync(system, "D100状态字", AddressType.HoldingRegister, 140, TagDataType.Int16);
        await system.Devices.ConnectAsync(system.Device.Id);
        var definitions = new AlarmDefinitionService(system.Store);
        await definitions.SaveAsync(new AlarmDefinition
        {
            TagId = tag.Id, Name = "状态字报警", Kind = AlarmDefinitionKind.Enum,
            EnumValues = "1=急停,2=超限,3=检测不良", Message = "{tagName}：{label}", Severity = "Error"
        });
        var runtime = system.Services.GetRequiredService<TagRuntime>();

        await system.Communication.WriteSingleRegisterAsync(system.Device, 140, 1, CancellationToken.None);
        await runtime.GetTagValueAsync(tag.Id);
        var first = Assert.Single(await DefinitionAlarmsAsync(system));
        Assert.Contains("D100状态字：急停", first.Message);

        // 同一条报警在有效期内的取值 1→2：消息刷新为“超限”，不新增记录
        await system.Communication.WriteSingleRegisterAsync(system.Device, 140, 2, CancellationToken.None);
        await runtime.GetTagValueAsync(tag.Id);
        var refreshed = Assert.Single(await DefinitionAlarmsAsync(system));
        Assert.Equal(first.AlarmId, refreshed.AlarmId);
        Assert.Contains("D100状态字：超限", refreshed.Message);
        Assert.Equal(1, refreshed.RepeatCount);
        Assert.Equal(AlarmStatus.Active, refreshed.Status);

        await system.Communication.WriteSingleRegisterAsync(system.Device, 140, 9, CancellationToken.None);
        await runtime.GetTagValueAsync(tag.Id);
        Assert.All(await DefinitionAlarmsAsync(system), x => Assert.Equal(AlarmStatus.ClearedUnacknowledged, x.Status));
    }

    private static async Task<Tag> SaveTagAsync(
        TestSystem system, string name, AddressType addressType, int address, TagDataType dataType)
    {
        var tag = new Tag
        {
            Name = name,
            DeviceId = system.Device.Id,
            AddressType = addressType,
            Address = address,
            DataType = dataType,
            AccessMode = AccessMode.ReadWrite,
            MinValue = dataType == TagDataType.Bool ? (double?)null : -1000,
            MaxValue = dataType == TagDataType.Bool ? (double?)null : 1000
        };
        await system.Tags.SaveAsync(tag);
        return tag;
    }

    private static async Task<IReadOnlyList<AlarmRecord>> DefinitionAlarmsAsync(TestSystem system)
        => (await system.Store.GetAlarmsAsync())
            .Where(x => x.Source.StartsWith("AlarmDef:", StringComparison.Ordinal))
            .ToArray();
}
