using System.Text;
using PLCScada.Core;

namespace PLCScada.Application;

// 运行日志 CSV 导出（Excel 兼容：UTF-8 BOM，逗号/引号/换行按 RFC 4180 转义）。
public static class LogCsv
{
    public static string Export(IReadOnlyList<OperationLog> logs)
    {
        var builder = new StringBuilder();
        builder.AppendLine("时间,级别,来源,内容");
        foreach (var log in logs)
        {
            builder.Append(log.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
                .Append(Escape(log.Level)).Append(',')
                .Append(Escape(log.Source)).Append(',')
                .Append(Escape(log.Message)).AppendLine();
        }
        return builder.ToString();
    }

    private static string Escape(string value)
        => value.AsSpan().IndexOfAny(",\"\r\n") < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
}
