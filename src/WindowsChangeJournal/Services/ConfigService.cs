using System.Text.Json;
using WindowsChangeJournal.Models;

namespace WindowsChangeJournal.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task<AppConfig> LoadAsync()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        if (!File.Exists(AppPaths.ConfigPath)) return new AppConfig();
        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(AppPaths.ConfigPath), Options) ?? new();
            Sanitize(config);
            return config;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("配置文件无法读取。原文件没有被覆盖，可在设置中导入有效配置。", ex);
        }
    }

    public async Task SaveAsync(AppConfig config)
    {
        Sanitize(config);
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var temporary = AppPaths.ConfigPath + ".tmp";
        var backup = AppPaths.ConfigPath + ".bak";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(config, Options));
        if (File.Exists(AppPaths.ConfigPath)) File.Replace(temporary, AppPaths.ConfigPath, backup, true);
        else File.Move(temporary, AppPaths.ConfigPath);
    }

    public static string Serialize(AppConfig config)
    {
        Sanitize(config);
        return JsonSerializer.Serialize(config, Options);
    }

    public static AppConfig Deserialize(string json)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(json, Options) ?? throw new InvalidOperationException("配置内容为空。");
        Sanitize(config);
        return config;
    }

    private static void Sanitize(AppConfig config)
    {
        config.SchemaVersion = 5;
        config.WatchedFolders = PathGuard.NormalizeAndCollapse(config.WatchedFolders);
        config.ExcludedFolders = PathGuard.NormalizeAndCollapse(config.ExcludedFolders);
        config.DisabledProgramRoots ??= [];
        config.DisabledProgramRoots = config.DisabledProgramRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathGuard.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.ExcludedProcesses = config.ExcludedProcesses
            .Select(p => Path.GetFileNameWithoutExtension(p.Trim()))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.ExcludedProcessMode = string.Equals(config.ExcludedProcessMode, "Running", StringComparison.OrdinalIgnoreCase) ? "Running" : "Foreground";
        config.RetentionDays = Math.Clamp(config.RetentionDays, 1, 3650);
        config.MaxStorageMb = Math.Clamp(config.MaxStorageMb, 128, 4096);
    }
}
