using WindowsChangeJournal.Models;

namespace WindowsChangeJournal.Services;

public enum EventOrigin
{
    UserOrApplication,
    WindowsSystem,
    MicrosoftDefender
}

public static class EventOriginService
{
    private static readonly string WindowsDirectory = NormalizeOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private static readonly string ProgramData = NormalizeOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    private static readonly string[] DefenderRoots = BuildRoots(
        "Microsoft\\Windows Defender\\Quarantine",
        "Microsoft\\Windows Defender\\Scans\\History\\Service\\DetectionHistory",
        "Microsoft\\Windows Defender\\Scans\\History\\Store");

    private static readonly string[] ProgramDataSystemRoots = BuildRoots(
        "Microsoft\\Windows",
        "Microsoft\\Diagnosis",
        "Microsoft\\Search",
        "Microsoft\\Provisioning",
        "USOPrivate",
        "USOShared");

    private static readonly HashSet<string> ReservedRootDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "$Extend", "$WinREAgent", "Recovery", "Config.Msi"
    };

    private static readonly HashSet<string> ReservedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp"
    };

    public static EventOrigin Classify(string path, string? relatedPath = null)
    {
        var primary = ClassifySingle(path);
        if (string.IsNullOrWhiteSpace(relatedPath)) return primary;

        var related = ClassifySingle(relatedPath);
        if (primary == EventOrigin.MicrosoftDefender || related == EventOrigin.MicrosoftDefender)
            return EventOrigin.MicrosoftDefender;
        return primary == EventOrigin.WindowsSystem && related == EventOrigin.WindowsSystem
            ? EventOrigin.WindowsSystem
            : EventOrigin.UserOrApplication;
    }

    public static bool ShouldRecord(AppConfig config, string path, string? relatedPath = null) => Classify(path, relatedPath) switch
    {
        EventOrigin.WindowsSystem => config.RecordWindowsSystemEvents,
        EventOrigin.MicrosoftDefender => config.RecordDefenderProtectionHistory,
        _ => true
    };

    public static string SourceFor(string path, string? relatedPath = null) => Classify(path, relatedPath) switch
    {
        EventOrigin.WindowsSystem => "WindowsSystem",
        EventOrigin.MicrosoftDefender => "MicrosoftDefenderProtectionHistory",
        _ => "DirectoryNotification"
    };

    private static EventOrigin ClassifySingle(string path)
    {
        var full = NormalizeOrEmpty(path);
        if (full.Length == 0) return EventOrigin.UserOrApplication;
        if (DefenderRoots.Any(root => PathGuard.IsUnderOrEqual(full, root))) return EventOrigin.MicrosoftDefender;
        if (WindowsDirectory.Length > 0 && PathGuard.IsUnderOrEqual(full, WindowsDirectory)) return EventOrigin.WindowsSystem;
        if (ProgramDataSystemRoots.Any(root => PathGuard.IsUnderOrEqual(full, root))) return EventOrigin.WindowsSystem;

        try
        {
            var driveRoot = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(driveRoot)) return EventOrigin.UserOrApplication;
            var relative = Path.GetRelativePath(driveRoot, full);
            var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 1 && ReservedRootFiles.Contains(segments[0])) return EventOrigin.WindowsSystem;
            if (segments.Length > 0 && ReservedRootDirectories.Contains(segments[0])) return EventOrigin.WindowsSystem;
        }
        catch { }

        return EventOrigin.UserOrApplication;
    }

    private static string[] BuildRoots(params string[] relativePaths)
    {
        if (ProgramData.Length == 0) return [];
        return relativePaths.Select(path => Path.Combine(ProgramData, path)).ToArray();
    }

    private static string NormalizeOrEmpty(string? path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? "" : PathGuard.Normalize(path); }
        catch { return ""; }
    }
}
