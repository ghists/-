using WindowsChangeJournal.Models;

namespace WindowsChangeJournal.Services;

public static class SourceFilterService
{
    public static List<FileEventRecord> Apply(
        IEnumerable<FileEventRecord> items,
        IEnumerable<string> selectedSources,
        int limit)
    {
        var selected = selectedSources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0 || limit <= 0) return [];

        return items
            .Where(item => selected.Contains(item.OriginDisplay))
            .Take(limit)
            .ToList();
    }
}
