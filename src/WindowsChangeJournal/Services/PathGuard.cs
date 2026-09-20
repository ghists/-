namespace WindowsChangeJournal.Services;

public static class PathGuard
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空。", nameof(path));
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsUnderOrEqual(string path, string directory)
    {
        try
        {
            var candidate = Normalize(path);
            var root = Normalize(directory);
            return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void RejectReparsePoint(string path)
    {
        var full = Normalize(path);
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("不能把符号链接、目录联接或挂载点直接设为监控根目录。请选择它实际指向的目录。");
    }

    public static List<string> NormalizeAndCollapse(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length)
            .ToList();
        return normalized.Where(path => !normalized.Any(parent => parent.Length < path.Length && IsUnderOrEqual(path, parent))).ToList();
    }
}
