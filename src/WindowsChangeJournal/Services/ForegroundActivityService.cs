namespace WindowsChangeJournal.Services;

public sealed class ForegroundActivityService : IDisposable
{
    private readonly EventRepository _repository;
    private readonly Func<bool> _isPaused;
    private readonly System.Threading.Timer _timer;
    private ForegroundProcessInfo? _current;
    private DateTimeOffset _currentStarted;
    private int _busy;

    public ForegroundActivityService(EventRepository repository, Func<bool> isPaused)
    {
        _repository = repository;
        _isPaused = isPaused;
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var next = _isPaused() ? null : ForegroundProcessService.GetForegroundProcess();
            if (_current is not null && (next is null || next.ProcessId != _current.ProcessId))
                await _repository.AddAppSessionAsync(_current.Name, _currentStarted, now, _current.StartedUtc);
            if (next is not null && (_current is null || next.ProcessId != _current.ProcessId)) _currentStarted = now;
            _current = next;
        }
        catch { }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose()
    {
        _timer.Dispose();
        if (_current is not null)
            try { _repository.AddAppSessionAsync(_current.Name, _currentStarted, DateTimeOffset.UtcNow, _current.StartedUtc).GetAwaiter().GetResult(); } catch { }
    }
}
