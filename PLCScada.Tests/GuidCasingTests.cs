using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PLCScada.Application;
using PLCScada.Core;
using PLCScada.Infrastructure.Persistence;

namespace PLCScada.Tests;

public sealed class GuidCasingTests
{
    [Fact]
    public async Task LowercaseGuidRowsAreNormalizedOnDemandAndIdempotently()
    {
        await using var system = await TestSystem.CreateAsync();
        await system.Devices.SaveAsync(system.Device);
        var tag = new Tag
        {
            Name = "大小写回归点位",
            DeviceId = system.Device.Id,
            AddressType = AddressType.Coil,
            Address = 300,
            DataType = TagDataType.Bool,
            AccessMode = AccessMode.ReadWrite
        };
        await system.Tags.SaveAsync(tag);

        var factory = system.Services.GetRequiredService<IDbContextFactory<ScadaDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        // 模拟外部脚本写入的小写 GUID（EF 以大写比较，历史上会导致“Tag 不存在”）
        await db.Database.ExecuteSqlRawAsync("UPDATE Tags SET Id = lower(Id) WHERE Id = upper(Id);");

        var definitions = new AlarmDefinitionService(system.Store);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            definitions.SaveAsync(new AlarmDefinition
            {
                TagId = tag.Id, Name = "小写回归", Kind = AlarmDefinitionKind.Bit, Message = "命中"
            }));
        Assert.Contains("Tag 不存在", failure.Message);

        var normalized = await SqliteGuidNormalizer.NormalizeAsync(db);
        Assert.True(normalized >= 1, "应当至少规范化一行小写 GUID");

        await definitions.SaveAsync(new AlarmDefinition
        {
            TagId = tag.Id, Name = "小写回归", Kind = AlarmDefinitionKind.Bit, Message = "命中"
        });
        Assert.Single(await definitions.GetAllAsync());

        // 幂等：重复执行不再产生变更
        Assert.Equal(0, await SqliteGuidNormalizer.NormalizeAsync(db));
    }
}