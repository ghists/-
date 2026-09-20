namespace WindowsChangeJournal.Models;

public sealed class SearchFilter
{
    public string Query { get; set; } = "";
    public string EventType { get; set; } = "";
    public List<string>? EventTypes { get; set; }
    public string Directory { get; set; } = "";
    public DateTimeOffset? FromUtc { get; set; }
    public DateTimeOffset? ToUtc { get; set; }
    public int Limit { get; set; } = 500;
}

public sealed class GapRecord
{
    public long Id { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }
    public string Reason { get; set; } = "";
    public string Scope { get; set; } = "";
    public string State { get; set; } = "Unrepaired";
    public string StateDisplay => State == "Unrepaired" ? "未补齐" : State;
}

public sealed class RepositoryStats
{
    public long EventCount { get; set; }
    public DateTimeOffset? EarliestUtc { get; set; }
    public DateTimeOffset? LatestUtc { get; set; }
    public long DatabaseBytes { get; set; }
    public int GapCount { get; set; }
}
