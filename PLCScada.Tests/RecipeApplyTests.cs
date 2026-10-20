using PLCScada.Application;
using PLCScada.Core;

namespace PLCScada.Tests;

public sealed class RecipeApplyTests
{
    [Fact]
    public async Task PreviewAndApplyWritesNumericAndBoolSettingsThenVerifiesReadback()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var numericTag = new Tag
        {
            Name = "功率上限设定",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.Float32,
            AccessMode = AccessMode.ReadWrite,
            Scale = 1,
            MinValue = 0,
            MaxValue = 100
        };
        var boolTag = new Tag
        {
            Name = "扫码启用",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 20,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(numericTag);
        await system.Tags.SaveAsync(boolTag);
        await system.Devices.ConnectAsync(system.Device.Id);

        var recipes = new RecipeService(system.Store);
        var recipeId = await recipes.SaveAsync(
            null, null, system.Device.Id, "扫码功率工况", "",
            [
                new(numericTag.Id, 12.5),
                new(boolTag.Id, BooleanValue: true)
            ]);
        var applier = new RecipeApplyService(system.Store, system.Communication, system.RecipeApplyLock);

        var preview = await applier.PreviewAsync(recipeId);
        Assert.True(preview.CanApply);
        Assert.Equal(
            $"Modbus TCP · {system.Device.IpAddress}:{system.Device.Port} · Unit {system.Device.UnitId}",
            preview.DeviceEndpoint);
        Assert.Equal(2, preview.Items.Count);
        Assert.Contains(preview.Items, x => x.CurrentValue is double value && value == 0);
        Assert.Contains(preview.Items, x => x.TargetValue is bool enabled && enabled);

        var result = await applier.ApplyAsync(
            recipeId, preview.RecipeRevision, preview.ConfigurationFingerprint,
            preview.CurrentValuesFingerprint);

        Assert.True(result.Succeeded);
        Assert.True(result.WritesStarted);
        Assert.Null(result.RollbackSucceeded);
        Assert.All(result.Items, item => Assert.Equal("已写入并验证", item.Status));
        var actualRegisters = await system.Communication.ReadAsync(
            system.Device, AddressType.HoldingRegister, 100, 2, CancellationToken.None);
        Assert.Equal(12.5, TagValueCodec.Decode(numericTag, actualRegisters));
        var actualCoil = await system.Communication.ReadAsync(
            system.Device, AddressType.Coil, 20, 1, CancellationToken.None);
        Assert.True((actualCoil[0] & 1) != 0);

        var savedRecipe = Assert.Single(await recipes.GetAllAsync());
        Assert.NotNull(savedRecipe.LastAppliedAt);
        Assert.Contains(await system.Store.GetLogsAsync(), x => x.Message.Contains("下发成功"));
    }

    [Fact]
    public async Task ConcurrentRecipeAppliesToOneDeviceAreSerialized()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveFloatTagAsync(system, "功率上限", 100);
        await system.Devices.ConnectAsync(system.Device.Id);
        var recipes = new RecipeService(system.Store);
        var firstId = await recipes.SaveAsync(null, null, system.Device.Id, "工况一", "", [new(tag.Id, 12.5)]);
        var secondId = await recipes.SaveAsync(null, null, system.Device.Id, "工况二", "", [new(tag.Id, 25)]);
        var applier = new RecipeApplyService(system.Store, system.Communication, system.RecipeApplyLock);
        var firstPreview = await applier.PreviewAsync(firstId);
        var secondPreview = await applier.PreviewAsync(secondId);

        var results = await Task.WhenAll(
            applier.ApplyAsync(firstId, firstPreview.RecipeRevision,
                firstPreview.ConfigurationFingerprint, firstPreview.CurrentValuesFingerprint),
            applier.ApplyAsync(secondId, secondPreview.RecipeRevision,
                secondPreview.ConfigurationFingerprint, secondPreview.CurrentValuesFingerprint));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => !result.Succeeded && !result.WritesStarted);
        var bytes = await system.Communication.ReadAsync(
            system.Device, AddressType.HoldingRegister, 100, 2, CancellationToken.None);
        var finalValue = (double)TagValueCodec.Decode(tag, bytes);
        Assert.Contains(finalValue, new[] { 12.5, 25d });
    }

    [Fact]
    public async Task ApplyDoesNotWriteWhenAnotherProcessOwnsTheDeviceLock()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveFloatTagAsync(system, "功率上限", 100);
        await system.Devices.ConnectAsync(system.Device.Id);
        var recipes = new RecipeService(system.Store);
        var recipeId = await recipes.SaveAsync(
            null, null, system.Device.Id, "设备锁占用测试", "", [new(tag.Id, 12.5)]);
        var preview = await new RecipeApplyService(
            system.Store, system.Communication, system.RecipeApplyLock).PreviewAsync(recipeId);
        var applier = new RecipeApplyService(
            system.Store, system.Communication, new BusyRecipeApplyLock());

        var result = await applier.ApplyAsync(
            recipeId, preview.RecipeRevision, preview.ConfigurationFingerprint,
            preview.CurrentValuesFingerprint);

        Assert.False(result.Succeeded);
        Assert.False(result.WritesStarted);
        Assert.Contains("另一个应用实例", result.Summary);
        var bytes = await system.Communication.ReadAsync(
            system.Device, AddressType.HoldingRegister, 100, 2, CancellationToken.None);
        Assert.Equal(0d, TagValueCodec.Decode(tag, bytes));
        Assert.Contains(await system.Store.GetLogsAsync(), x => x.Message.Contains("另一个应用实例"));
    }

    [Fact]
    public async Task ApplyDoesNotWriteWhenDeviceEndpointChangesAfterLockAcquisition()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveFloatTagAsync(system, "功率上限", 100);
        await system.Devices.ConnectAsync(system.Device.Id);
        var recipes = new RecipeService(system.Store);
        var recipeId = await recipes.SaveAsync(
            null, null, system.Device.Id, "端点锁竞态测试", "", [new(tag.Id, 12.5)]);
        var communication = new UnitOneCommunication(system.Communication);
        var applier = new RecipeApplyService(
            system.Store, communication, system.RecipeApplyLock);

        var device = (await system.Store.GetDeviceAsync(system.Device.Id))!;
        device.UnitId = 2;
        await system.Store.SaveDeviceAsync(device);
        var preview = await applier.PreviewAsync(recipeId);
        Assert.True(preview.CanApply);

        device.UnitId = 1;
        await system.Store.SaveDeviceAsync(device);
        var changingLock = new CallbackRecipeApplyLock(async ct =>
        {
            var latest = (await system.Store.GetDeviceAsync(system.Device.Id, ct))!;
            latest.UnitId = 2;
            await system.Store.SaveDeviceAsync(latest, ct);
        });
        var guardedApplier = new RecipeApplyService(
            system.Store, communication, changingLock);

        var result = await guardedApplier.ApplyAsync(
            recipeId, preview.RecipeRevision, preview.ConfigurationFingerprint,
            preview.CurrentValuesFingerprint);

        Assert.False(result.Succeeded);
        Assert.False(result.WritesStarted);
        Assert.Null(result.RollbackSucceeded);
        Assert.Contains("连接目标", result.Summary);
        Assert.Equal("未执行写入", Assert.Single(result.Items).Status);
    }

    [Fact]
    public async Task OfflineDeviceCannotPreviewOrWriteRecipe()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "时间设定",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.UInt16,
            AccessMode = AccessMode.ReadWrite,
            MinValue = 0,
            MaxValue = 100
        };
        await system.Tags.SaveAsync(tag);
        var recipes = new RecipeService(system.Store);
        var recipeId = await recipes.SaveAsync(null, null, system.Device.Id, "离线测试", "", [new(tag.Id, 25)]);
        var applier = new RecipeApplyService(system.Store, system.Communication, system.RecipeApplyLock);

        var preview = await applier.PreviewAsync(recipeId);
        Assert.False(preview.CanApply);
        Assert.Contains("未在线", preview.BlockReason);

        var result = await applier.ApplyAsync(
            recipeId, preview.RecipeRevision, preview.ConfigurationFingerprint,
            preview.CurrentValuesFingerprint);
        Assert.False(result.Succeeded);
        Assert.False(result.WritesStarted);
        Assert.Contains("未在线", result.Summary);
    }

    [Fact]
    public async Task PreviewIsInvalidatedWhenTagAddressChangesBeforeApply()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveFloatTagAsync(system, "功率上限", 100);
        await system.Devices.ConnectAsync(system.Device.Id);
        var recipes = new RecipeService(system.Store);
        var recipeId = await recipes.SaveAsync(null, null, system.Device.Id, "配置变更测试", "", [new(tag.Id, 12.5)]);
        var applier = new RecipeApplyService(system.Store, system.Communication, system.RecipeApplyLock);
        var preview = await applier.PreviewAsync(recipeId);
        Assert.True(preview.CanApply);

        var oldTag = tag.Copy();
        tag.Address = 102;
        await system.Tags.SaveAsync(tag);

        var result = await applier.ApplyAsync(
            recipeId, preview.RecipeRevision, preview.ConfigurationFingerprint,
            preview.CurrentValuesFingerprint);

        Assert.False(result.Succeeded);
        Assert.False(result.WritesStarted);
        Assert.Contains("配置", result.Summary);
        var oldAddress = await system.Communication.ReadAsync(
            system.Device, AddressType.HoldingRegister, 100, 2, CancellationToken.None);
        var newAddress = await system.Communication.ReadAsync(
            system.Device, AddressType.HoldingRegister, 102, 2, CancellationToken.None);
        Assert.Equal(0d, TagValueCodec.Decode(oldTag, oldAddress));
        Assert.Equal(0d, TagValueCodec.Decode(tag, newAddress));
    }

    [Fact]
    public async Task PreviewIsInvalidatedWhenDeviceValueChangesBeforeApply()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveFloatTagAsync(system, "功率上限", 100);
        await system.Devices.ConnectAsync(system.Device.Id);
        var recipes = new RecipeService(system.Store);
        var recipeId = await recipes.SaveAsync(null, null, system.Device.Id, "当前值变更测试", "", [new(tag.Id, 12.5)]);
        var applier = new RecipeApplyService(system.Store, system.Communication, system.RecipeApplyLock);
        var preview = await applier.PreviewAsync(recipeId);
        Assert.True(preview.CanApply);

        await system.Communication.WriteRegistersAsync(
            system.Device, 100, TagValueCodec.Encode(tag, 5));

        var result = await applier.ApplyAsync(
            recipeId, preview.RecipeRevision, preview.ConfigurationFingerprint,
            preview.CurrentValuesFingerprint);

        Assert.False(result.Succeeded);
        Assert.False(result.WritesStarted);
        Assert.Contains("当前值", result.Summary);
        var registers = await system.Communication.ReadAsync(
            system.Device, AddressType.HoldingRegister, 100, 2, CancellationToken.None);
        Assert.Equal(5d, TagValueCodec.Decode(tag, registers));
    }

    [Fact]
    public async Task ApplyReportsPreWriteRaceWithoutClaimingWritesOrRollback()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = await SaveFloatTagAsync(system, "功率上限", 100);
        await system.Devices.ConnectAsync(system.Device.Id);
        var recipes = new RecipeService(system.Store);
        var recipeId = await recipes.SaveAsync(
            null, null, system.Device.Id, "预写入竞争测试", "", [new(tag.Id, 12.5)]);
        var preview = await new RecipeApplyService(
            system.Store, system.Communication, system.RecipeApplyLock).PreviewAsync(recipeId);
        Assert.True(preview.CanApply);

        var racingCommunication = new ChangeBeforeWriteRead(system.Communication, tag, 5);
        var applier = new RecipeApplyService(
            system.Store, racingCommunication, system.RecipeApplyLock);
        var result = await applier.ApplyAsync(
            recipeId, preview.RecipeRevision, preview.ConfigurationFingerprint,
            preview.CurrentValuesFingerprint);

        Assert.False(result.Succeeded);
        Assert.False(result.WritesStarted);
        Assert.Null(result.RollbackSucceeded);
        Assert.Contains("未执行任何写入", result.Summary);
        Assert.Equal("未执行写入", Assert.Single(result.Items).Status);
        var registers = await system.Communication.ReadAsync(
            system.Device, AddressType.HoldingRegister, 100, 2, CancellationToken.None);
        Assert.Equal(0d, TagValueCodec.Decode(tag, registers));
    }

    [Fact]
    public async Task PreviewBlocksOverlappingRegisterTagsBeforeAnyWrite()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var first = new Tag
        {
            Name = "双字设定",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 100,
            DataType = TagDataType.Float32,
            AccessMode = AccessMode.ReadWrite
        };
        var second = new Tag
        {
            Name = "重叠设定",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 101,
            DataType = TagDataType.UInt16,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(first);
        await system.Tags.SaveAsync(second);
        var recipes = new RecipeService(system.Store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recipes.SaveAsync(null, null, system.Device.Id, "地址冲突", "",
                [new(first.Id, 5), new(second.Id, 1)]));
        Assert.Empty(await recipes.GetAllAsync());
    }

    [Fact]
    public async Task FailedPartialApplyRollsBackEveryAttemptedValue()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var firstTag = await SaveFloatTagAsync(system, "第一设定", 100);
        var secondTag = await SaveFloatTagAsync(system, "第二设定", 102);
        await system.Devices.ConnectAsync(system.Device.Id);
        var recipes = new RecipeService(system.Store);
        var recipeId = await recipes.SaveAsync(
            null, null, system.Device.Id, "回滚测试", "",
            [new(firstTag.Id, 12.5), new(secondTag.Id, 25)]);
        var communication = new FailSecondRegisterWrite(system.Communication);
        var applier = new RecipeApplyService(system.Store, communication, system.RecipeApplyLock);
        var preview = await applier.PreviewAsync(recipeId);

        var result = await applier.ApplyAsync(
            recipeId, preview.RecipeRevision, preview.ConfigurationFingerprint,
            preview.CurrentValuesFingerprint);

        Assert.False(result.Succeeded);
        Assert.True(result.WritesStarted);
        Assert.True(result.RollbackSucceeded);
        Assert.Single(result.Items, item => item.Status == "已回滚");
        Assert.Single(result.Items, item => item.Status == "原值未改变");
        var firstBytes = await system.Communication.ReadAsync(
            system.Device, AddressType.HoldingRegister, 100, 2, CancellationToken.None);
        var secondBytes = await system.Communication.ReadAsync(
            system.Device, AddressType.HoldingRegister, 102, 2, CancellationToken.None);
        Assert.Equal(0d, TagValueCodec.Decode(firstTag, firstBytes));
        Assert.Equal(0d, TagValueCodec.Decode(secondTag, secondBytes));
    }

    [Fact]
    public async Task RollbackDoesNotOverwriteValueChangedByExternalOperator()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var firstTag = await SaveFloatTagAsync(system, "第一设定", 100);
        var secondTag = await SaveFloatTagAsync(system, "第二设定", 102);
        await system.Devices.ConnectAsync(system.Device.Id);
        var recipes = new RecipeService(system.Store);
        var recipeId = await recipes.SaveAsync(
            null, null, system.Device.Id, "外部变更回滚保护", "",
            [new(firstTag.Id, 12.5), new(secondTag.Id, 25)]);
        var communication = new ExternalChangeBeforeRollback(system.Communication, firstTag);
        var applier = new RecipeApplyService(system.Store, communication, system.RecipeApplyLock);
        var preview = await applier.PreviewAsync(recipeId);
        Assert.True(preview.CanApply);

        var result = await applier.ApplyAsync(
            recipeId, preview.RecipeRevision, preview.ConfigurationFingerprint,
            preview.CurrentValuesFingerprint);

        Assert.False(result.Succeeded);
        Assert.True(result.WritesStarted);
        Assert.False(result.RollbackSucceeded);
        Assert.Contains("回滚不完整", result.Summary);
        Assert.Contains("拒绝自动回滚", result.Items.Single(x => x.TagId == firstTag.Id).Error);
        Assert.Equal("回滚失败，设备值需人工确认",
            result.Items.Single(x => x.TagId == firstTag.Id).Status);
        var firstBytes = await system.Communication.ReadAsync(
            system.Device, AddressType.HoldingRegister, 100, 2, CancellationToken.None);
        var secondBytes = await system.Communication.ReadAsync(
            system.Device, AddressType.HoldingRegister, 102, 2, CancellationToken.None);
        Assert.Equal(7d, TagValueCodec.Decode(firstTag, firstBytes));
        Assert.Equal(0d, TagValueCodec.Decode(secondTag, secondBytes));
    }

    private static async Task<Tag> SaveFloatTagAsync(TestSystem system, string name, int address)
    {
        var tag = new Tag
        {
            Name = name,
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = address,
            DataType = TagDataType.Float32,
            AccessMode = AccessMode.ReadWrite,
            MinValue = 0,
            MaxValue = 100
        };
        await system.Tags.SaveAsync(tag);
        return tag;
    }

    private sealed class BusyRecipeApplyLock : IRecipeApplyLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(
            string resourceKey, TimeSpan waitTimeout, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);
    }

    private sealed class CallbackRecipeApplyLock(Func<CancellationToken, Task> onAcquire) : IRecipeApplyLock
    {
        public async Task<IAsyncDisposable?> TryAcquireAsync(
            string resourceKey, TimeSpan waitTimeout, CancellationToken ct = default)
        {
            await onAcquire(ct);
            return new Lease();
        }

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class UnitOneCommunication(IDeviceCommunication inner) : IDeviceCommunication
    {
        public DeviceSnapshot GetSnapshot(Guid id) => inner.GetSnapshot(id);
        public IReadOnlyList<DeviceSnapshot> GetSnapshots() => inner.GetSnapshots();
        public Task<CommunicationTestResult> TestAsync(Device device, CancellationToken ct = default)
            => inner.TestAsync(ForSimulator(device), ct);
        public Task ConnectAsync(Device device, CancellationToken ct = default)
            => inner.ConnectAsync(ForSimulator(device), ct);
        public Task DisconnectAsync(Guid id, CancellationToken ct = default) => inner.DisconnectAsync(id, ct);
        public Task InvalidateAsync(Guid id, CancellationToken ct = default) => inner.InvalidateAsync(id, ct);
        public Task PollAsync(Device device, CancellationToken ct)
            => inner.PollAsync(ForSimulator(device), ct);
        public Task<byte[]> ReadAsync(
            Device device, AddressType type, ushort address, ushort count, CancellationToken ct)
            => inner.ReadAsync(ForSimulator(device), type, address, count, ct);
        public Task WriteCoilAsync(
            Device device, ushort address, bool value, CancellationToken ct = default)
            => inner.WriteCoilAsync(ForSimulator(device), address, value, ct);
        public Task WriteSingleRegisterAsync(
            Device device, ushort address, ushort value, CancellationToken ct = default)
            => inner.WriteSingleRegisterAsync(ForSimulator(device), address, value, ct);
        public Task WriteRegistersAsync(
            Device device, ushort address, byte[] networkBytes, CancellationToken ct = default)
            => inner.WriteRegistersAsync(ForSimulator(device), address, networkBytes, ct);

        private static Device ForSimulator(Device device)
        {
            var copy = device.Copy();
            copy.UnitId = 1;
            return copy;
        }
    }

    private sealed class ChangeBeforeWriteRead(
        IDeviceCommunication inner,
        Tag tag,
        double changedValue) : IDeviceCommunication
    {
        private int _readCount;

        public DeviceSnapshot GetSnapshot(Guid id) => inner.GetSnapshot(id);
        public IReadOnlyList<DeviceSnapshot> GetSnapshots() => inner.GetSnapshots();
        public Task<CommunicationTestResult> TestAsync(Device device, CancellationToken ct = default)
            => inner.TestAsync(device, ct);
        public Task ConnectAsync(Device device, CancellationToken ct = default) => inner.ConnectAsync(device, ct);
        public Task DisconnectAsync(Guid id, CancellationToken ct = default) => inner.DisconnectAsync(id, ct);
        public Task InvalidateAsync(Guid id, CancellationToken ct = default) => inner.InvalidateAsync(id, ct);
        public Task PollAsync(Device device, CancellationToken ct) => inner.PollAsync(device, ct);
        public Task<byte[]> ReadAsync(
            Device device, AddressType type, ushort address, ushort count, CancellationToken ct)
            => Interlocked.Increment(ref _readCount) == 2
                ? Task.FromResult(TagValueCodec.Encode(tag, changedValue))
                : inner.ReadAsync(device, type, address, count, ct);
        public Task WriteCoilAsync(Device device, ushort address, bool value, CancellationToken ct = default)
            => inner.WriteCoilAsync(device, address, value, ct);
        public Task WriteSingleRegisterAsync(
            Device device, ushort address, ushort value, CancellationToken ct = default)
            => inner.WriteSingleRegisterAsync(device, address, value, ct);
        public Task WriteRegistersAsync(
            Device device, ushort address, byte[] networkBytes, CancellationToken ct = default)
            => inner.WriteRegistersAsync(device, address, networkBytes, ct);
    }

    private sealed class FailSecondRegisterWrite(IDeviceCommunication inner) : IDeviceCommunication
    {
        private int _registerWriteCount;

        public DeviceSnapshot GetSnapshot(Guid id) => inner.GetSnapshot(id);
        public IReadOnlyList<DeviceSnapshot> GetSnapshots() => inner.GetSnapshots();
        public Task<CommunicationTestResult> TestAsync(Device device, CancellationToken ct = default)
            => inner.TestAsync(device, ct);
        public Task ConnectAsync(Device device, CancellationToken ct = default) => inner.ConnectAsync(device, ct);
        public Task DisconnectAsync(Guid id, CancellationToken ct = default) => inner.DisconnectAsync(id, ct);
        public Task InvalidateAsync(Guid id, CancellationToken ct = default) => inner.InvalidateAsync(id, ct);
        public Task PollAsync(Device device, CancellationToken ct) => inner.PollAsync(device, ct);
        public Task<byte[]> ReadAsync(Device device, AddressType type, ushort address, ushort count, CancellationToken ct)
            => inner.ReadAsync(device, type, address, count, ct);
        public Task WriteCoilAsync(Device device, ushort address, bool value, CancellationToken ct = default)
            => inner.WriteCoilAsync(device, address, value, ct);
        public Task WriteSingleRegisterAsync(Device device, ushort address, ushort value, CancellationToken ct = default)
            => inner.WriteSingleRegisterAsync(device, address, value, ct);

        public Task WriteRegistersAsync(Device device, ushort address, byte[] networkBytes, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _registerWriteCount) == 2)
                throw new IOException("测试注入：第二组寄存器写入失败。");
            return inner.WriteRegistersAsync(device, address, networkBytes, ct);
        }
    }

    private sealed class ExternalChangeBeforeRollback(
        IDeviceCommunication inner,
        Tag externallyChangedTag) : IDeviceCommunication
    {
        private int _registerWriteCount;

        public DeviceSnapshot GetSnapshot(Guid id) => inner.GetSnapshot(id);
        public IReadOnlyList<DeviceSnapshot> GetSnapshots() => inner.GetSnapshots();
        public Task<CommunicationTestResult> TestAsync(Device device, CancellationToken ct = default)
            => inner.TestAsync(device, ct);
        public Task ConnectAsync(Device device, CancellationToken ct = default) => inner.ConnectAsync(device, ct);
        public Task DisconnectAsync(Guid id, CancellationToken ct = default) => inner.DisconnectAsync(id, ct);
        public Task InvalidateAsync(Guid id, CancellationToken ct = default) => inner.InvalidateAsync(id, ct);
        public Task PollAsync(Device device, CancellationToken ct) => inner.PollAsync(device, ct);
        public Task<byte[]> ReadAsync(Device device, AddressType type, ushort address, ushort count, CancellationToken ct)
            => inner.ReadAsync(device, type, address, count, ct);
        public Task WriteCoilAsync(Device device, ushort address, bool value, CancellationToken ct = default)
            => inner.WriteCoilAsync(device, address, value, ct);
        public Task WriteSingleRegisterAsync(Device device, ushort address, ushort value, CancellationToken ct = default)
            => inner.WriteSingleRegisterAsync(device, address, value, ct);

        public async Task WriteRegistersAsync(
            Device device, ushort address, byte[] networkBytes, CancellationToken ct = default)
        {
            var write = Interlocked.Increment(ref _registerWriteCount);
            if (write == 2)
            {
                await inner.WriteRegistersAsync(
                    device,
                    checked((ushort)externallyChangedTag.Address),
                    TagValueCodec.Encode(externallyChangedTag, 7),
                    CancellationToken.None);
                throw new IOException("测试注入：第二组寄存器写入失败。");
            }

            await inner.WriteRegistersAsync(device, address, networkBytes, ct);
        }
    }
}
