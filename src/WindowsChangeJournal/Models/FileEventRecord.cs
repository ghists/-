using WindowsChangeJournal.Services;

namespace WindowsChangeJournal.Models;

public sealed class FileEventRecord
{
    public long Id { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public DateTimeOffset ObservedUtc { get; set; }
    public string EventType { get; set; } = "";
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    public long? Size { get; set; }
    public bool IsDirectory { get; set; }
    public string? EntityId { get; set; }
    public string? Source { get; set; }
    public string Confidence { get; set; } = "Observed";
    public string? ForegroundProcess { get; set; }
    public long Epoch { get; set; }
    public int ChangeCount { get; set; } = 1;

    public DateTimeOffset LocalTime => TimestampUtc.ToLocalTime();
    public string DisplayName => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
    public string TypeDisplay => EventType switch
    {
        "Created" => "创建",
        "Changed" => ChangeCount > 1 ? $"修改 ×{ChangeCount}" : "修改",
        "Deleted" => "移除或删除",
        "Renamed" => "重命名",
        "Moved" => Confidence == "Confirmed" ? "移动" : "可能移动",
        _ => EventType
    };
    public string Detail => EventType is "Moved" or "Renamed" ? $"{OldPath}  →  {Path}" : Path;
    public string ConfidenceDisplay => Confidence switch
    {
        "Confirmed" => "已确认",
        "Candidate" => "可能",
        _ => "已观察"
    };
    public string OriginDisplay
    {
        get
        {
            if (Source == "WindowsSystem") return "Windows 系统";
            if (Source == "MicrosoftDefenderProtectionHistory") return "Microsoft Defender";
            var origin = EventOriginService.Classify(Path, OldPath);
            if (origin == EventOrigin.WindowsSystem) return "Windows 系统";
            if (origin == EventOrigin.MicrosoftDefender) return "Microsoft Defender";
            var association = ProgramAssociationService.TryIdentify(Path, OldPath);
            return association is null ? "用户/软件（推定）" : $"{association.Name}（推定）";
        }
    }
}
