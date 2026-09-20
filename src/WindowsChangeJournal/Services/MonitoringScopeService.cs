using WindowsChangeJournal.Models;

namespace WindowsChangeJournal.Services;

public static class MonitoringScopeService
{
    public static List<string> Resolve(AppConfig config)
    {
        var roots = new List<string>();
        if (config.MonitorAllFixedDrives)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                        roots.Add(drive.RootDirectory.FullName);
                }
                catch
                {
                    // A drive can disappear between discovery and inspection.
                }
            }
        }

        roots.AddRange(config.WatchedFolders);
        return PathGuard.NormalizeAndCollapse(roots);
    }
}
