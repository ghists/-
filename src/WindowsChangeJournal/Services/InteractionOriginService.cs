namespace WindowsChangeJournal.Services;

public static class InteractionOriginService
{
    private static readonly HashSet<string> FileManagers = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "totalcmd",
        "totalcmd64",
        "dopus",
        "xyplorer",
        "freecommander",
        "freecommanderxe"
    };

    public static string GetDisplayName(string? foregroundProcess)
        => IsLikelyHumanOperation(foregroundProcess)
            ? "人为操作（推定）"
            : "软件操作（推定）";

    public static bool IsLikelyHumanOperation(string? foregroundProcess)
        => !string.IsNullOrWhiteSpace(foregroundProcess) && FileManagers.Contains(foregroundProcess);
}
