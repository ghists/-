namespace WindowsChangeJournal.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = ResolveDataDirectory();
    public static string DatabasePath => Path.Combine(DataDirectory, "events.db");
    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    private static string ResolveDataDirectory()
    {
        var explicitDirectory = Environment.GetEnvironmentVariable("WINDOWS_CHANGE_JOURNAL_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(explicitDirectory)) return Path.GetFullPath(explicitDirectory);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) throw new InvalidOperationException("无法取得当前用户的数据目录。");
        return Path.Combine(local, "WindowsChangeJournal");
    }
}
