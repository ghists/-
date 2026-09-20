using System.Text;
using System.Text.Json;
using WindowsChangeJournal.Models;

namespace WindowsChangeJournal.Services;

public static class ExportService
{
    public static async Task ExportCsvAsync(string path, IEnumerable<FileEventRecord> events, bool includePaths)
    {
        var builder = new StringBuilder();
        builder.AppendLine("时间,事件,文件,旧路径,新路径,可信度,来源应用");
        foreach (var item in events)
        {
            var oldPath = includePaths ? item.OldPath : Redact(item.OldPath);
            var newPath = includePaths ? item.Path : Redact(item.Path);
            var values = new[] { item.LocalTime.ToString("yyyy-MM-dd HH:mm:ss"), item.TypeDisplay, item.DisplayName, oldPath, newPath, item.ConfidenceDisplay, item.ForegroundProcess };
            builder.AppendLine(string.Join(',', values.Select(Csv)));
        }
        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(true));
    }

    public static async Task ExportJsonAsync(string path, IEnumerable<FileEventRecord> events, bool includePaths)
    {
        var output = events.Select(item => new
        {
            time = item.TimestampUtc,
            observed = item.ObservedUtc,
            kind = item.EventType,
            file = item.DisplayName,
            oldPath = includePaths ? item.OldPath : Redact(item.OldPath),
            path = includePaths ? item.Path : Redact(item.Path),
            confidence = item.Confidence,
            source = item.Source,
            foregroundApp = item.ForegroundProcess
        });
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static string? Redact(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? "[已隐藏路径]" : $"[已隐藏目录]\\{name}";
    }

    private static string Csv(string? value)
    {
        value ??= "";
        if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
