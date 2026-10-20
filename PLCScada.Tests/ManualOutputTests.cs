using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class ManualOutputTests
{
    [Fact]
    public async Task ManualOutputsDefaultToExistingTagsAndUseReboundTagAddresses()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await CreateOutputTagsAsync(system);
        var manual = new ManualOutputService(system.Store, system.Communication);
        var initialBindings = await manual.GetOutputBindingsAsync(system.Device.Id);

        Assert.Equal(14, initialBindings.Count);
        Assert.All(initialBindings, binding => Assert.True(binding.IsUsable));

        var tags = (await system.Tags.GetAllAsync()).ToDictionary(x => x.Name);
        var selections = initialBindings.ToDictionary(
            binding => binding.Point.Id,
            binding => (Guid?)binding.Tag!.Id);
        var powerPointId = initialBindings.Single(x => x.Point.Name == "通电").Point.Id;
        var pressPointId = initialBindings.Single(x => x.Point.Name == "压头气缸").Point.Id;
        selections[powerPointId] = tags["压头气缸"].Id;
        selections[pressPointId] = tags["通电"].Id;
        await manual.SaveOutputBindingsAsync(system.Device.Id, selections);

        var rebound = await manual.GetOutputBindingsAsync(system.Device.Id);
        Assert.Equal(tags["压头气缸"].Id, rebound.Single(x => x.Point.Name == "通电").Tag!.Id);
        Assert.Equal(24577, rebound.Single(x => x.Point.Name == "通电").Tag!.Address);

        await manual.SetOutputAsync(system.Device.Id, powerPointId, tags["压头气缸"].Id, 24577, true);
        var outputs = await manual.ReadOutputsAsync(system.Device.Id);

        Assert.Equal(14, outputs.Count);
        Assert.True(outputs[powerPointId]);
        Assert.False(outputs[pressPointId]);
        Assert.False(outputs[rebound.Single(x => x.Point.Name == "下压气缸").Point.Id]);
        Assert.Single(await system.Store.GetLogsAsync(), x => x.Message.Contains("手动输出"));

        await manual.SetOutputAsync(system.Device.Id, powerPointId, tags["压头气缸"].Id, 24577, false);
        Assert.False((await manual.ReadOutputsAsync(system.Device.Id))[powerPointId]);
    }

    [Fact]
    public async Task ResetOutputUsesM0CoilZero()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await CreateOutputTagsAsync(system);
        var manual = new ManualOutputService(system.Store, system.Communication);
        var binding = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Name == "复位");

        Assert.Equal("M0", binding.Point.DefaultPlcAddress);
        Assert.Equal(0, binding.Point.DefaultModbusAddress);
        Assert.Equal(0, binding.Tag!.Address);

        await manual.SetOutputAsync(system.Device.Id, binding.Point.Id, binding.Tag.Id, 0, true);
        Assert.True((await manual.ReadOutputsAsync(system.Device.Id))[binding.Point.Id]);
        await manual.SetOutputAsync(system.Device.Id, binding.Point.Id, binding.Tag.Id, 0, false);
        Assert.False((await manual.ReadOutputsAsync(system.Device.Id))[binding.Point.Id]);
    }

    [Fact]
    public async Task OutputCanWriteControlTagAndMonitorSeparateReadOnlyTag()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await CreateOutputTagsAsync(system);
        var feedbackTag = new Tag
        {
            Name = "通电反馈",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 26000,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadOnly
        };
        await system.Tags.SaveAsync(feedbackTag);
        var manual = new ManualOutputService(system.Store, system.Communication);
        var power = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Name == "通电");
        var controlTag = power.Tag!;

        await manual.SaveOutputPointConfigurationAsync(
            system.Device.Id, power.Point.Id, power.Point.Name, controlTag.Id,
            monitorTagId: feedbackTag.Id);
        await system.Communication.WriteCoilAsync(system.Device, 26000, true);

        var state = await manual.ReadOutputsAsync(system.Device.Id);
        Assert.True(state[power.Point.Id]);
        await manual.SetOutputAsync(system.Device.Id, power.Point.Id, controlTag.Id, 24576, true);
        var controlBytes = await system.Communication.ReadAsync(
            system.Device, AddressType.Coil, 24576, 1, CancellationToken.None);
        Assert.True((controlBytes[0] & 1) != 0);

        await manual.SetOutputAsync(system.Device.Id, power.Point.Id, controlTag.Id, 24576, false);
        controlBytes = await system.Communication.ReadAsync(
            system.Device, AddressType.Coil, 24576, 1, CancellationToken.None);
        Assert.False((controlBytes[0] & 1) != 0);
        Assert.True((await manual.ReadOutputsAsync(system.Device.Id))[power.Point.Id]);

        await system.Communication.WriteCoilAsync(system.Device, 26000, false);
        Assert.False((await manual.ReadOutputsAsync(system.Device.Id))[power.Point.Id]);
    }

    [Fact]
    public async Task MonitorTagAddressConflictsAreRejectedWithoutPartialConfiguration()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await CreateOutputTagsAsync(system);
        var duplicateFeedback = new Tag
        {
            Name = "压头气缸反馈",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 24577,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadOnly
        };
        await system.Tags.SaveAsync(duplicateFeedback);
        var manual = new ManualOutputService(system.Store, system.Communication);
        var initial = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Name == "通电");
        var controlTag = initial.Tag!;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.SaveOutputPointConfigurationAsync(
                system.Device.Id, initial.Point.Id, "不可部分改名", controlTag.Id,
                monitorTagId: duplicateFeedback.Id));

        var unchanged = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Id == initial.Point.Id);
        Assert.Equal("通电", unchanged.Point.Name);
        Assert.Equal(controlTag.Id, unchanged.Tag!.Id);
        Assert.Null(unchanged.MonitorTag);
    }

    [Fact]
    public async Task RebindingSingleOutputPreservesOtherButtonsAndRejectsDuplicateCoil()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await CreateOutputTagsAsync(system);
        var extraTag = new Tag
        {
            Name = "备用手动输出",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 24600,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(extraTag);
        var manual = new ManualOutputService(system.Store, system.Communication);

        var initialBindings = await manual.GetOutputBindingsAsync(system.Device.Id);
        var powerPointId = initialBindings.Single(x => x.Point.Name == "通电").Point.Id;
        var pressPointId = initialBindings.Single(x => x.Point.Name == "压头气缸").Point.Id;
        await manual.SaveOutputBindingAsync(system.Device.Id, powerPointId, extraTag.Id);

        var bindings = await manual.GetOutputBindingsAsync(system.Device.Id);
        Assert.Equal(extraTag.Id, bindings.Single(x => x.Point.Name == "通电").Tag!.Id);
        Assert.Equal(24577, bindings.Single(x => x.Point.Name == "压头气缸").Tag!.Address);
        Assert.Single(await system.Store.GetManualOutputBindingsAsync(system.Device.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.SaveOutputBindingAsync(system.Device.Id, pressPointId, extraTag.Id));
    }

    [Fact]
    public async Task ManualOutputRejectsDuplicateTagsAndCoilAddresses()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await CreateOutputTagsAsync(system);
        var manual = new ManualOutputService(system.Store, system.Communication);
        var bindings = await manual.GetOutputBindingsAsync(system.Device.Id);
        var selections = bindings.ToDictionary(x => x.Point.Id, x => (Guid?)x.Tag!.Id);
        var powerPointId = bindings.Single(x => x.Point.Name == "通电").Point.Id;
        var pressPointId = bindings.Single(x => x.Point.Name == "压头气缸").Point.Id;
        selections[powerPointId] = selections[pressPointId];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.SaveOutputBindingsAsync(system.Device.Id, selections));

        var tags = (await system.Tags.GetAllAsync()).ToDictionary(x => x.Name);
        selections[powerPointId] = tags["通电"].Id;
        tags["通电"].Address = tags["压头气缸"].Address;
        await system.Tags.SaveAsync(tags["通电"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.SaveOutputBindingsAsync(system.Device.Id, selections));
    }

    [Fact]
    public async Task UnboundOutputIsUnknownAndCannotBeWritten()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await CreateOutputTagsAsync(system);
        var manual = new ManualOutputService(system.Store, system.Communication);
        var bindings = await manual.GetOutputBindingsAsync(system.Device.Id);
        var selections = bindings.ToDictionary(x => x.Point.Id, x => (Guid?)x.Tag!.Id);
        var powerPointId = bindings.Single(x => x.Point.Name == "通电").Point.Id;
        var unboundTagId = selections[powerPointId]!.Value;
        selections[powerPointId] = null;
        await manual.SaveOutputBindingsAsync(system.Device.Id, selections);

        var output = (await manual.GetOutputBindingsAsync(system.Device.Id)).Single(x => x.Point.Name == "通电");
        Assert.False(output.IsUsable);
        Assert.Null(output.Tag);
        Assert.Null((await manual.ReadOutputsAsync(system.Device.Id))[powerPointId]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.SetOutputAsync(system.Device.Id, powerPointId, unboundTagId, 24576, true));
    }

    [Fact]
    public async Task OutputButtonsCanBeCreatedRenamedAndDeletedPerDevice()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await CreateOutputTagsAsync(system);
        var manual = new ManualOutputService(system.Store, system.Communication);

        var defaultOutput = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Name == "通电");
        await manual.UpdateOutputPointAsync(system.Device.Id, defaultOutput.Point.Id, "主电源");
        var renamedDefault = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Id == defaultOutput.Point.Id);
        Assert.Equal(defaultOutput.Tag!.Id, renamedDefault.Tag!.Id);

        var pointId = await manual.CreateOutputPointAsync(system.Device.Id, "备用气缸");
        var created = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Id == pointId);
        Assert.False(created.IsUsable);
        Assert.Null(created.Tag);

        var spareTag = new Tag
        {
            Name = "备用气缸输出",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 24600,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(spareTag);
        await manual.SaveOutputBindingAsync(system.Device.Id, pointId, spareTag.Id);
        await manual.UpdateOutputPointAsync(system.Device.Id, pointId, "辅助气缸");

        var renamed = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Id == pointId);
        Assert.Equal("辅助气缸", renamed.Point.Name);
        Assert.Equal(spareTag.Id, renamed.Tag!.Id);

        var otherDevice = new Device { Name = "Other PLC" };
        await system.Devices.SaveAsync(otherDevice);
        var otherBindings = await manual.GetOutputBindingsAsync(otherDevice.Id);
        Assert.Equal(14, otherBindings.Count);
        Assert.DoesNotContain(otherBindings, x => x.Point.Id == pointId);

        await manual.DeleteOutputPointAsync(system.Device.Id, pointId);
        Assert.DoesNotContain(await system.Store.GetManualOutputPointsAsync(system.Device.Id), x => x.Id == pointId);
        Assert.DoesNotContain(await system.Store.GetManualOutputBindingsAsync(system.Device.Id), x => x.OutputPointId == pointId);
    }

    [Fact]
    public async Task OutputPointConfigurationSavesNameAndTagTogetherWithoutPartialUpdates()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await CreateOutputTagsAsync(system);
        var manual = new ManualOutputService(system.Store, system.Communication);
        var initial = await manual.GetOutputBindingsAsync(system.Device.Id);
        var powerTag = initial.Single(x => x.Point.Name == "通电").Tag!;
        var spareTag = new Tag
        {
            Name = "联合配置备用 Tag",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 24600,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(spareTag);

        var pointId = await manual.SaveOutputPointConfigurationAsync(
            system.Device.Id, null, "自定义输出", spareTag.Id);
        var created = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Id == pointId);
        Assert.Equal("自定义输出", created.Point.Name);
        Assert.Equal(spareTag.Id, created.Tag!.Id);
        Assert.True(created.IsUsable);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.SaveOutputPointConfigurationAsync(
                system.Device.Id, pointId, "不应部分更新", powerTag.Id));
        var afterRejectedServiceSave = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Id == pointId);
        Assert.Equal("自定义输出", afterRejectedServiceSave.Point.Name);
        Assert.Equal(spareTag.Id, afterRejectedServiceSave.Tag!.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SaveManualOutputPointConfigurationAsync(
                system.Device.Id, pointId, "不应部分更新", powerTag.Id));
        var afterRejectedStoreSave = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Id == pointId);
        Assert.Equal("自定义输出", afterRejectedStoreSave.Point.Name);
        Assert.Equal(spareTag.Id, afterRejectedStoreSave.Tag!.Id);

        await manual.SaveOutputPointConfigurationAsync(
            system.Device.Id, pointId, "辅助输出", null);
        var unbound = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Id == pointId);
        Assert.Equal("辅助输出", unbound.Point.Name);
        Assert.Null(unbound.Tag);
        Assert.False(unbound.IsUsable);
    }

    [Fact]
    public async Task OutputButtonNamesMustBeUniqueAndNonEmpty()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var manual = new ManualOutputService(system.Store, system.Communication);

        await manual.CreateOutputPointAsync(system.Device.Id, "自定义输出");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.CreateOutputPointAsync(system.Device.Id, " 自定义输出 "));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.CreateOutputPointAsync(system.Device.Id, "  "));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.CreateOutputPointAsync(system.Device.Id, new string('A', 81)));
    }

    [Fact]
    public async Task ManualOutputRejectsDisabledOrManuallyDisconnectedDevices()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await CreateOutputTagsAsync(system);
        var tag = (await system.Tags.GetAllAsync()).Single(x => x.Name == "通电");
        var manual = new ManualOutputService(system.Store, system.Communication);
        var outputPointId = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Name == "通电").Point.Id;

        await system.Communication.DisconnectAsync(system.Device.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.SetOutputAsync(system.Device.Id, outputPointId, tag.Id, 24576, true));

        await system.Devices.SetEnabledAsync(system.Device.Id, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.SetOutputAsync(system.Device.Id, outputPointId, tag.Id, 24576, true));
    }

    [Fact]
    public async Task InvalidatedTagBindingBecomesUnknownAndCannotBeSet()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        await CreateOutputTagsAsync(system);
        var tag = (await system.Tags.GetAllAsync()).Single(x => x.Name == "通电");
        var manual = new ManualOutputService(system.Store, system.Communication);
        var bindings = await manual.GetOutputBindingsAsync(system.Device.Id);
        var powerPointId = bindings.Single(x => x.Point.Name == "通电").Point.Id;
        await manual.SaveOutputBindingsAsync(system.Device.Id,
            bindings.ToDictionary(x => x.Point.Id, x => (Guid?)x.Tag!.Id));

        tag.Enabled = false;
        await system.Tags.SaveAsync(tag);

        var invalidBinding = (await manual.GetOutputBindingsAsync(system.Device.Id))
            .Single(x => x.Point.Name == "通电");
        Assert.False(invalidBinding.IsUsable);
        Assert.Null((await manual.ReadOutputsAsync(system.Device.Id))[powerPointId]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manual.SetOutputAsync(system.Device.Id, powerPointId, tag.Id, 24576, true));
    }

    private static async Task CreateOutputTagsAsync(TestSystem system)
    {
        foreach (var point in ManualOutputService.DefaultOutputPoints)
        {
            await system.Tags.SaveAsync(new Tag
            {
                Name = point.Name,
                Description = $"手动输出 {point.PlcAddress}",
                DeviceId = system.Device.Id,
                AddressType = AddressType.Coil,
                Address = point.ModbusAddress,
                DataType = TagDataType.Bool,
                AccessMode = AccessMode.ReadWrite
            });
        }
    }
}
