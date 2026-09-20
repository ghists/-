using System.Diagnostics;
using System.Runtime.InteropServices;
namespace WindowsChangeJournal.Services;

public sealed record ForegroundProcessInfo(string Name, int ProcessId, DateTimeOffset? StartedUtc);

public static class ForegroundProcessService
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    public static string? GetForegroundProcessName()
        => GetForegroundProcess()?.Name;

    public static ForegroundProcessInfo? GetForegroundProcess()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == nint.Zero) return null;
            GetWindowThreadProcessId(h, out var id);
            using var process = Process.GetProcessById((int)id);
            DateTimeOffset? started = null;
            try { started = process.StartTime.ToUniversalTime(); } catch { }
            return new ForegroundProcessInfo(process.ProcessName, process.Id, started);
        }
        catch { return null; }
    }

    public static bool IsAnyRunning(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0) return false;
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                    if (names.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }
}
