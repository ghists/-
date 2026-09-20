namespace WindowsChangeJournal.Models;

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 5;
    public bool MonitorAllFixedDrives { get; set; } = true;
    public bool RecordWindowsSystemEvents { get; set; }
    public bool RecordDefenderProtectionHistory { get; set; }
    public List<string> WatchedFolders { get; set; } = [];
    public List<string> ExcludedFolders { get; set; } = [];
    public List<string> DisabledProgramRoots { get; set; } = [];
    public List<string> ExcludedProcesses { get; set; } = [];
    public bool IncludeSubdirectories { get; set; } = true;
    public bool RecordContentChanges { get; set; } = true;
    public string ExcludedProcessMode { get; set; } = "Foreground";
    public int RetentionDays { get; set; } = 30;
    public int MaxStorageMb { get; set; } = 512;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
}
