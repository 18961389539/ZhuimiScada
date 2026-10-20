using Microsoft.EntityFrameworkCore;

namespace PLCScada.Infrastructure.Persistence;

// GUID 大小写根治：EF 以大写文本读写 Guid（TEXT 比较区分大小写），
// 任何外部脚本/SQL 导入的小写 GUID 会让“按 Id 精确匹配”静默失效。
// 本组件在应用启动（迁移之后）动态扫描全库，把整值是 GUID 且非大写的文本统一为大写，
// 未来新增的表/列也会自动覆盖，无需维护硬编码清单；重复执行是幂等的。
public static class SqliteGuidNormalizer
{
    // GUID 文本形状：8-4-4-4-12 位十六进制字符（GLOB 模式）
    private const string Hex4 = "[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]";
    private const string Hex6 = Hex4 + "[0-9A-Fa-f][0-9A-Fa-f]";
    private const string Hex8 = Hex4 + Hex4;
    private const string Hex12 = Hex6 + Hex6;
    private const string GuidGlob =
        Hex8 + "-" + Hex4 + "-" + Hex4 + "-" + Hex4 + "-" + Hex12;

    public static async Task<int> NormalizeAsync(DbContext db, CancellationToken ct = default)
    {
        var tables = await db.Database
            .SqlQueryRaw<string>(
                "SELECT name AS Value FROM sqlite_master WHERE type = 'table' " +
                "AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EFMigrations%'")
            .ToListAsync(ct);
        var normalized = 0;
        // 表名/列名来自 sqlite_master 与 pragma（库内 schema，非用户输入），且已转义；
        // GUID 形状判断使用常量 GLOB 模式，不含任何外部输入，故关闭 EF 的注入分析器告警。
#pragma warning disable EF1002, EF1003
        foreach (var table in tables)
        {
            var columns = await db.Database
                .SqlQueryRaw<string>(
                    $"SELECT name AS Value FROM pragma_table_info('{Escape(table)}') WHERE type = 'TEXT'")
                .ToListAsync(ct);
            foreach (var column in columns)
            {
                var name = Escape(table);
                var field = Escape(column);
                normalized += await db.Database.ExecuteSqlRawAsync(
                    $"UPDATE \"{name}\" SET \"{field}\" = upper(\"{field}\") " +
                    $"WHERE \"{field}\" GLOB '{GuidGlob}' AND \"{field}\" <> upper(\"{field}\");", ct);
            }
        }
        return normalized;
#pragma warning restore EF1002, EF1003
    }

    private static string Escape(string identifier) =>
        identifier.Replace("'", "''").Replace("\"", "\"\"");
}