using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Application;
using PLCScada.Core;
using PLCScada.Infrastructure.Persistence;

namespace PLCScada.Tests;

public sealed class StringTagTests
{
    [Fact]
    public void AsciiAndUtf16DecodingTrimPadding()
    {
        var ascii = new Tag
        {
            Name = "扫码内容测试",
            AddressType = AddressType.HoldingRegister,
            Address = 4000,
            DataType = TagDataType.String,
            StringRegisters = 6,
            StringEncoding = StringEncoding.Ascii,
            AccessMode = AccessMode.ReadOnly
        };
        var asciiBytes = new byte[12];
        System.Text.Encoding.ASCII.GetBytes("SN42").CopyTo(asciiBytes, 0);
        Assert.Equal("SN42", TagValueCodec.Decode(ascii, asciiBytes));
        Assert.Equal(6, TagValueCodec.RegisterCount(ascii));

        var utf16 = new Tag
        {
            Name = "UTF16测试",
            AddressType = AddressType.HoldingRegister,
            Address = 4100,
            DataType = TagDataType.String,
            StringRegisters = 4,
            StringEncoding = StringEncoding.Utf16LE,
            AccessMode = AccessMode.ReadOnly
        };
        // Modbus 字序为大端：'A' → 0x00,0x41；'B' → 0x00,0x42
        var utf16Bytes = new byte[] { 0x00, 0x41, 0x00, 0x42, 0x00, 0x00, 0x00, 0x00 };
        Assert.Equal("AB", TagValueCodec.Decode(utf16, utf16Bytes));

        Assert.Throws<InvalidDataException>(() => TagValueCodec.Encode(ascii, 1));
    }

    [Fact]
    public async Task StringValidationAndRuntimeRead()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);

        // 校验：位地址不能建 String；String 必须只读
        var tagService = new TagService(system.Store);
        var onCoil = await Assert.ThrowsAsync<ValidationException>(() => tagService.SaveAsync(new Tag
        {
            Name = "错误字符串", DeviceId = system.Device.Id, AddressType = AddressType.Coil,
            Address = 20, DataType = TagDataType.String
        }));
        Assert.Contains("位地址必须绑定 Bool", onCoil.Message);

        var writable = await Assert.ThrowsAsync<ValidationException>(() => tagService.SaveAsync(new Tag
        {
            Name = "可写字符串", DeviceId = system.Device.Id, AddressType = AddressType.HoldingRegister,
            Address = 4100, DataType = TagDataType.String, AccessMode = AccessMode.ReadWrite
        }));
        Assert.Contains("仅支持只读", writable.Message);

        // 采集：ASCII 写入 20 寄存器 → 运行时读到字符串
        var tag = new Tag
        {
            Name = "扫码内容",
            DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister,
            Address = 4000,
            DataType = TagDataType.String,
            StringRegisters = 20,
            StringEncoding = StringEncoding.Ascii,
            AccessMode = AccessMode.ReadOnly
        };
        await system.Tags.SaveAsync(tag);
        await system.Devices.ConnectAsync(system.Device.Id);
        var payload = new byte[40];
        System.Text.Encoding.ASCII.GetBytes("SN-2026-0001").CopyTo(payload, 0);
        await system.Communication.WriteRegistersAsync(system.Device, 4000, payload, CancellationToken.None);

        var snapshot = await system.Runtime.GetTagValueAsync(tag.Id);
        Assert.Equal(TagQuality.Good, snapshot.Quality);
        Assert.Equal("SN-2026-0001", snapshot.CurrentValue);
    }
}

public sealed class SnRecordTests
{
    [Fact]
    public async Task SnRecordsQuerySearchAndRetention()
    {
        await using var system = await TestSystem.CreateAsync();
        for (var index = 1; index <= 5; index++)
            await system.Store.AddSnRecordAsync($"SN000{index}", index % 2 == 0 ? "Manual" : "PLC");

        var page = await system.Store.QuerySnRecordsAsync(null, 1, 50);
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(5, page.Items.Count);

        var search = await system.Store.QuerySnRecordsAsync("0003", 1, 50);
        Assert.Equal(1, search.TotalCount);
        Assert.Equal("SN0003", search.Items[0].Sn);

        var export = await system.Store.QuerySnRecordsForExportAsync(null, cap: 3);
        Assert.Equal(3, export.Count);

        // 保留清理：超龄 SN 记录被删除
        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var old = await db.SnRecords.OrderBy(x => x.Sn).FirstAsync();
            old.CapturedAt = DateTimeOffset.UtcNow.AddDays(-200);
            await db.SaveChangesAsync();
        }
        var result = await system.Store.CleanupHistoryAsync(new DataRetentionOptions
        {
            OperationLogRetentionDays = 0,
            ClearedAlarmRetentionDays = 0,
            RecipeApplyRecordRetentionDays = 0,
            SnRecordRetentionDays = 30
        });
        Assert.Equal(1, result.SnRecordsDeleted);
        Assert.Equal(4, (await system.Store.QuerySnRecordsAsync(null, 1, 50)).TotalCount);
    }

    [Fact]
    public async Task SnSourceMustBeStringTagAndFallsBackToNamedTag()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);

        var numeric = new Tag
        {
            Name = "普通数值", DeviceId = system.Device.Id, AddressType = AddressType.HoldingRegister,
            Address = 200, DataType = TagDataType.UInt16, AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(numeric);
        var typeError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            system.Store.SetSnTagIdAsync(numeric.Id));
        Assert.Contains("String", typeError.Message);

        Assert.Null(await system.Store.GetSnTagIdAsync());

        var snTag = new Tag
        {
            Name = MonitoringDefaults.SnTagName, DeviceId = system.Device.Id,
            AddressType = AddressType.HoldingRegister, Address = 4000, DataType = TagDataType.String,
            StringRegisters = 20, StringEncoding = StringEncoding.Ascii, AccessMode = AccessMode.ReadOnly
        };
        await system.Tags.SaveAsync(snTag);
        Assert.Equal(snTag.Id, await system.Store.GetSnTagIdAsync());
    }
}