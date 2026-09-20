using WindowsChangeJournal.Models;

namespace WindowsChangeJournal.Services;

public sealed record ProgramAssociation(string Name, string RootPath);

public static class ProgramAssociationService
{
    private static readonly HashSet<string> PublisherFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Adobe", "Apple", "ByteDance", "Google", "Kingsoft", "Microsoft", "Mozilla", "NVIDIA Corporation",
        "Oracle", "Sogou", "Tencent", "Valve"
    };

    public static ProgramAssociation? TryIdentify(string path, string? oldPath = null)
    {
        foreach (var candidate in new[] { path, oldPath })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var result = TryIdentifySingle(candidate);
            if (result is not null) return result;
        }
        return null;
    }

    public static bool IsMonitoringEnabled(AppConfig config, string programRoot)
    {
        if (config.ExcludedFolders.Any(folder => PathGuard.IsUnderOrEqual(programRoot, folder))) return false;
        return !config.DisabledProgramRoots.Any(folder => PathGuard.IsUnderOrEqual(programRoot, folder));
    }

    public static bool IsBlockedByGeneralExclusion(AppConfig config, string programRoot) =>
        config.ExcludedFolders.Any(folder => PathGuard.IsUnderOrEqual(programRoot, folder));

    public static void SetMonitoringEnabled(AppConfig config, string programRoot, bool enabled)
    {
        var normalized = PathGuard.Normalize(programRoot);
        config.DisabledProgramRoots ??= [];
        config.DisabledProgramRoots.RemoveAll(path => string.Equals(PathGuard.Normalize(path), normalized, StringComparison.OrdinalIgnoreCase));
        if (!enabled) config.DisabledProgramRoots.Add(normalized);
        config.DisabledProgramRoots = config.DisabledProgramRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathGuard.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ProgramAssociation? TryIdentifySingle(string path)
    {
        string full;
        try { full = PathGuard.Normalize(path); }
        catch { return null; }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localLow = string.IsNullOrWhiteSpace(local) ? "" : Path.Combine(Path.GetDirectoryName(local) ?? "", "LocalLow");

        foreach (var basePath in new[] { localLow, roaming, local, common, programFilesX86, programFiles }
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(value => value.Length))
        {
            var association = TryFromBase(full, basePath);
            if (association is not null) return association;
        }
        return null;
    }

    private static ProgramAssociation? TryFromBase(string fullPath, string basePath)
    {
        if (!PathGuard.IsUnderOrEqual(fullPath, basePath) || string.Equals(PathGuard.Normalize(fullPath), PathGuard.Normalize(basePath), StringComparison.OrdinalIgnoreCase))
            return null;

        var relative = Path.GetRelativePath(basePath, fullPath);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        var depth = 1;
        if (segments[0].Equals("Packages", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2) depth = 2;
        else if (PublisherFolders.Contains(segments[0]) && segments.Length >= 2) depth = 2;

        var root = Path.Combine([basePath, .. segments.Take(depth)]);
        var name = segments[depth - 1];
        if (segments[0].Equals("Packages", StringComparison.OrdinalIgnoreCase))
        {
            var underscore = name.IndexOf('_');
            if (underscore > 0) name = name[..underscore];
            var dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        }
        return new ProgramAssociation(name, PathGuard.Normalize(root));
    }
}
