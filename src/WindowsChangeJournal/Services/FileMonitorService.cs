using System.Collections.Concurrent;
using System.Threading.Channels;
using WindowsChangeJournal.Models;

namespace WindowsChangeJournal.Services;

public sealed class FileMonitorService : IDisposable
{
    private sealed record FileSnapshot(string Path, long? Size, string? EntityId, bool IsDirectory);
    private sealed record PendingDelete(Guid Id, FileSnapshot Snapshot, DateTimeOffset SeenAt, long Epoch);
    private sealed record ChangeState(int Version, int Count, DateTimeOffset FirstSeen, FileSnapshot Snapshot);

    private readonly EventRepository _repository;
    private readonly List<FileSystemWatcher> _watchers = [];
    private List<string> _resolvedRoots = [];
    private readonly ConcurrentDictionary<string, FileSnapshot> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, PendingDelete> _deletes = [];
    private readonly ConcurrentDictionary<string, ChangeState> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pauseReasons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _offlineRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<FileEventRecord> _queue;
    private readonly Task _writerTask;
    private readonly System.Threading.Timer _policyTimer;
    private readonly System.Threading.Timer _availabilityTimer;
    private System.Threading.Timer? _manualResumeTimer;
    private AppConfig _config = new();
    private DateTimeOffset? _pauseStarted;
    private string _pauseReasonAtStart = "";
    private DateTimeOffset? _excludedClearSince;
    private long _epoch;
    private int _overflowReported;
    private bool _disposed;

    public event EventHandler<FileEventRecord>? EventRecorded;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler? StateChanged;

    public FileMonitorService(EventRepository repository)
    {
        _repository = repository;
        _queue = Channel.CreateBounded<FileEventRecord>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        _writerTask = Task.Run(WriterLoopAsync);
        _policyTimer = new System.Threading.Timer(_ => CheckExcludedApplications(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _availabilityTimer = new System.Threading.Timer(_ => CheckRootAvailability(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool IsPaused { get { lock (_stateGate) return _pauseReasons.Count > 0; } }
    public string PauseDescription { get { lock (_stateGate) return _pauseReasons.Count == 0 ? "记录中" : string.Join("、", _pauseReasons); } }
    public int ActiveWatcherCount { get { lock (_stateGate) return _watchers.Count; } }
    public int ConfiguredRootCount { get { lock (_stateGate) return _resolvedRoots.Count; } }
    public IReadOnlyList<string> ResolvedRoots { get { lock (_stateGate) return [.. _resolvedRoots]; } }
    public bool IsGlobalMode => _config.MonitorAllFixedDrives;
    public long Epoch => Interlocked.Read(ref _epoch);
    public int PendingEventCount => _queue.Reader.Count;

    public void Start(AppConfig config)
    {
        ThrowIfDisposed();
        StopWatchers();
        _config = config;
        var roots = MonitoringScopeService.Resolve(config);
        lock (_stateGate)
        {
            _resolvedRoots = roots;
            _offlineRoots.Clear();
        }
        foreach (var rawPath in roots)
        {
            try
            {
                var path = PathGuard.Normalize(rawPath);
                if (!Directory.Exists(path))
                {
                    MarkRootOffline(path, "目录不存在或磁盘未连接");
                    continue;
                }
                PathGuard.RejectReparsePoint(path);
                if (IsExcluded(path)) continue;

                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = config.IncludeSubdirectories,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    InternalBufferSize = 32 * 1024
                };
                watcher.Created += OnCreated;
                watcher.Deleted += OnDeleted;
                watcher.Renamed += OnRenamed;
                watcher.Changed += OnChanged;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                lock (_stateGate) _watchers.Add(watcher);
                if (!config.MonitorAllFixedDrives)
                    _ = Task.Run(() => BuildBaseline(path, config.IncludeSubdirectories));
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"无法监控 {rawPath}：{ex.Message}");
                _ = _repository.AddGapAsync(DateTimeOffset.UtcNow, null, "权限或监控初始化失败", rawPath);
            }
        }
        _policyTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        _availabilityTimer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        PublishStatus();
    }

    public void PauseManually(TimeSpan? duration = null)
    {
        SetPauseReason("手动暂停", true);
        _manualResumeTimer?.Dispose();
        _manualResumeTimer = duration is null ? null : new System.Threading.Timer(_ => ResumeManual(), null, duration.Value, Timeout.InfiniteTimeSpan);
    }

    public void ResumeManual()
    {
        _manualResumeTimer?.Dispose();
        _manualResumeTimer = null;
        SetPauseReason("手动暂停", false);
    }

    public void SetSessionLocked(bool locked) => SetPauseReason("锁屏暂停", locked);
    public void SetResourcePaused(bool paused) => SetPauseReason("资源保护暂停", paused);

    private void SetPauseReason(string reason, bool active)
    {
        DateTimeOffset? startedToClose = null;
        string closedReason = "";
        var changed = false;
        lock (_stateGate)
        {
            var wasPaused = _pauseReasons.Count > 0;
            changed = active ? _pauseReasons.Add(reason) : _pauseReasons.Remove(reason);
            var isPaused = _pauseReasons.Count > 0;
            if (!wasPaused && isPaused)
            {
                _pauseStarted = DateTimeOffset.UtcNow;
                _pauseReasonAtStart = string.Join("、", _pauseReasons);
                Interlocked.Increment(ref _epoch);
                ClearCorrelationState();
            }
            else if (wasPaused && !isPaused)
            {
                startedToClose = _pauseStarted;
                closedReason = _pauseReasonAtStart;
                _pauseStarted = null;
                _pauseReasonAtStart = "";
                Interlocked.Increment(ref _epoch);
                ClearCorrelationState();
            }
        }
        if (!changed) return;
        if (startedToClose is not null)
        {
            _ = _repository.AddGapAsync(startedToClose.Value, DateTimeOffset.UtcNow, closedReason, "全部监控范围");
            if (!_config.MonitorAllFixedDrives)
            {
                foreach (var root in ResolvedRoots.Where(Directory.Exists))
                    _ = Task.Run(() => BuildBaseline(root, _config.IncludeSubdirectories));
            }
        }
        PublishStatus();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CheckExcludedApplications()
    {
        if (_disposed) return;
        try
        {
            var excluded = _config.ExcludedProcesses;
            var match = string.Equals(_config.ExcludedProcessMode, "Running", StringComparison.OrdinalIgnoreCase)
                ? ForegroundProcessService.IsAnyRunning(excluded)
                : ForegroundProcessService.GetForegroundProcessName() is string foreground && excluded.Contains(foreground, StringComparer.OrdinalIgnoreCase);
            if (match)
            {
                _excludedClearSince = null;
                SetPauseReason("排除应用暂停", true);
            }
            else if (HasPauseReason("排除应用暂停"))
            {
                _excludedClearSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - _excludedClearSince >= TimeSpan.FromSeconds(3))
                {
                    _excludedClearSince = null;
                    SetPauseReason("排除应用暂停", false);
                }
            }
        }
        catch { }
    }

    private bool HasPauseReason(string reason) { lock (_stateGate) return _pauseReasons.Contains(reason); }

    private void CheckRootAvailability()
    {
        if (_disposed) return;
        if (_config.MonitorAllFixedDrives)
        {
            var current = MonitoringScopeService.Resolve(_config);
            if (!current.SequenceEqual(ResolvedRoots, StringComparer.OrdinalIgnoreCase))
            {
                StatusChanged?.Invoke(this, "固定磁盘范围已变化，正在更新全局监控。");
                Start(_config);
                return;
            }
        }
        foreach (var root in ResolvedRoots)
        {
            bool exists;
            try { exists = Directory.Exists(root); } catch { exists = false; }
            if (!exists) MarkRootOffline(root, "目录离线");
            else
            {
                var restored = false;
                lock (_stateGate) restored = _offlineRoots.Remove(root);
                if (restored)
                {
                    StatusChanged?.Invoke(this, $"目录已恢复，正在重新建立监控：{root}");
                    Start(_config);
                    return;
                }
            }
        }
    }

    private void MarkRootOffline(string root, string reason)
    {
        var added = false;
        lock (_stateGate) added = _offlineRoots.Add(root);
        if (!added) return;
        StatusChanged?.Invoke(this, $"{reason}：{root}");
        _ = _repository.AddGapAsync(DateTimeOffset.UtcNow, null, reason, root);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BuildBaseline(string root, bool recursive)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            var count = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", options))
            {
                if (_disposed || IsPaused) break;
                if (IsExcluded(path) || !EventOriginService.ShouldRecord(_config, path)) continue;
                var identity = FileIdentityService.TryRead(path);
                if (identity is not null) _known[path] = new FileSnapshot(path, identity.Size, identity.Key, identity.IsDirectory);
                if (++count >= 1_000_000)
                {
                    StatusChanged?.Invoke(this, $"{root} 的基线超过 100 万项，已停止继续预读；后续事件仍会记录。");
                    break;
                }
            }
        }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"建立目录基线失败：{ex.Message}"); }
    }

    private bool ShouldIgnore(string path, string? relatedPath = null) =>
        IsPaused
        || IsExcluded(path)
        || (!string.IsNullOrWhiteSpace(relatedPath) && IsExcluded(relatedPath))
        || !EventOriginService.ShouldRecord(_config, path, relatedPath);
    private bool IsExcluded(string path) =>
        _config.ExcludedFolders.Any(folder => PathGuard.IsUnderOrEqual(path, folder))
        || _config.DisabledProgramRoots.Any(folder => PathGuard.IsUnderOrEqual(path, folder));

    private async void OnCreated(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (IsPaused || IsExcluded(e.FullPath)) return;
            var now = DateTimeOffset.UtcNow;
            var snapshot = Snapshot(e.FullPath);
            await Task.Delay(180, _lifetime.Token);

            var match = _deletes.Values
                .Where(d => d.Epoch == Epoch && now - d.SeenAt <= TimeSpan.FromSeconds(3))
                .Select(d => new { Delete = d, Confirmed = snapshot.EntityId is not null && snapshot.EntityId == d.Snapshot.EntityId,
                    Candidate = snapshot.Size is not null && snapshot.Size == d.Snapshot.Size && string.Equals(Path.GetFileName(snapshot.Path), Path.GetFileName(d.Snapshot.Path), StringComparison.OrdinalIgnoreCase) })
                .Where(x => x.Confirmed || x.Candidate)
                .OrderByDescending(x => x.Confirmed)
                .ThenByDescending(x => x.Delete.SeenAt)
                .FirstOrDefault();
            var oldPath = match?.Delete.Snapshot.Path;
            if (ShouldIgnore(e.FullPath, oldPath)) return;
            if (match is not null) _deletes.TryRemove(match.Delete.Id, out _);
            _known[e.FullPath] = snapshot;

            Enqueue(new FileEventRecord
            {
                TimestampUtc = now,
                ObservedUtc = DateTimeOffset.UtcNow,
                EventType = match is null ? "Created" : "Moved",
                Path = e.FullPath,
                OldPath = oldPath,
                Size = snapshot.Size,
                IsDirectory = snapshot.IsDirectory,
                EntityId = snapshot.EntityId ?? match?.Delete.Snapshot.EntityId,
                Source = EventOriginService.SourceFor(e.FullPath, oldPath),
                Confidence = match is null ? "Observed" : match.Confirmed ? "Confirmed" : "Candidate",
                ForegroundProcess = ForegroundProcessService.GetForegroundProcessName(),
                Epoch = Epoch
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"创建事件处理失败：{ex.Message}"); }
    }

    private async void OnDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (ShouldIgnore(e.FullPath)) return;
            _known.TryRemove(e.FullPath, out var snapshot);
            snapshot ??= new FileSnapshot(e.FullPath, null, null, false);
            var pending = new PendingDelete(Guid.NewGuid(), snapshot, DateTimeOffset.UtcNow, Epoch);
            _deletes[pending.Id] = pending;
            await Task.Delay(TimeSpan.FromSeconds(3.2), _lifetime.Token);
            if (_deletes.TryRemove(pending.Id, out _) && !ShouldIgnore(e.FullPath) && pending.Epoch == Epoch)
                Enqueue(new FileEventRecord
                {
                    TimestampUtc = pending.SeenAt,
                    ObservedUtc = DateTimeOffset.UtcNow,
                    EventType = "Deleted",
                    Path = e.FullPath,
                    Size = snapshot.Size,
                    IsDirectory = snapshot.IsDirectory,
                    EntityId = snapshot.EntityId,
                    Source = EventOriginService.SourceFor(e.FullPath),
                    Confidence = "Observed",
                    ForegroundProcess = ForegroundProcessService.GetForegroundProcessName(),
                    Epoch = pending.Epoch
                });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"删除事件处理失败：{ex.Message}"); }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            if (ShouldIgnore(e.FullPath, e.OldFullPath)) return;
            _known.TryRemove(e.OldFullPath, out var previous);
            var current = Snapshot(e.FullPath);
            if (current.EntityId is null && previous is not null) current = current with { EntityId = previous.EntityId, Size = previous.Size, IsDirectory = previous.IsDirectory };
            _known[e.FullPath] = current;
            Enqueue(new FileEventRecord
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                ObservedUtc = DateTimeOffset.UtcNow,
                EventType = "Renamed",
                Path = e.FullPath,
                OldPath = e.OldFullPath,
                Size = current.Size,
                IsDirectory = current.IsDirectory,
                EntityId = current.EntityId,
                Source = EventOriginService.SourceFor(e.FullPath, e.OldFullPath),
                Confidence = "Confirmed",
                ForegroundProcess = ForegroundProcessService.GetForegroundProcessName(),
                Epoch = Epoch
            });
        }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"重命名事件处理失败：{ex.Message}"); }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!_config.RecordContentChanges || ShouldIgnore(e.FullPath) || Directory.Exists(e.FullPath)) return;
        try
        {
            var snapshot = Snapshot(e.FullPath);
            _known[e.FullPath] = snapshot;
            var state = _changes.AddOrUpdate(e.FullPath,
                _ => new ChangeState(1, 1, DateTimeOffset.UtcNow, snapshot),
                (_, old) => new ChangeState(old.Version + 1, old.Count + 1, old.FirstSeen, snapshot));
            _ = FlushChangeAsync(e.FullPath, state.Version);
        }
        catch { }
    }

    private async Task FlushChangeAsync(string path, int version)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
            if (!_changes.TryGetValue(path, out var current) || current.Version != version || !_changes.TryRemove(path, out current)) return;
            if (ShouldIgnore(path)) return;
            Enqueue(new FileEventRecord
            {
                TimestampUtc = current.FirstSeen,
                ObservedUtc = DateTimeOffset.UtcNow,
                EventType = "Changed",
                Path = path,
                Size = current.Snapshot.Size,
                IsDirectory = false,
                EntityId = current.Snapshot.EntityId,
                Source = EventOriginService.SourceFor(path),
                Confidence = "Observed",
                ForegroundProcess = ForegroundProcessService.GetForegroundProcessName(),
                Epoch = Epoch,
                ChangeCount = current.Count
            });
        }
        catch (OperationCanceledException) { }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var root = (sender as FileSystemWatcher)?.Path ?? "未知范围";
        var reason = e.GetException() is InternalBufferOverflowException ? "目录通知缓冲区溢出" : "目录监控错误";
        StatusChanged?.Invoke(this, $"{reason}：{root}");
        _ = _repository.AddGapAsync(DateTimeOffset.UtcNow, null, reason, root);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static FileSnapshot Snapshot(string path)
    {
        var identity = FileIdentityService.TryRead(path);
        if (identity is not null) return new FileSnapshot(path, identity.Size, identity.Key, identity.IsDirectory);
        var isDirectory = Directory.Exists(path);
        long? size = null;
        try { if (!isDirectory && File.Exists(path)) size = new FileInfo(path).Length; } catch { }
        return new FileSnapshot(path, size, null, isDirectory);
    }

    private void Enqueue(FileEventRecord item)
    {
        if (_disposed) return;
        if (_queue.Writer.TryWrite(item)) return;
        if (Interlocked.Exchange(ref _overflowReported, 1) == 0)
        {
            StatusChanged?.Invoke(this, "事件队列已满，部分记录缺失；缺口已登记。");
            _ = _repository.AddGapAsync(DateTimeOffset.UtcNow, null, "内部事件队列已满", "全部监控范围");
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task WriterLoopAsync()
    {
        var batch = new List<FileEventRecord>(200);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_lifetime.Token))
            {
                batch.Clear();
                while (batch.Count < 200 && _queue.Reader.TryRead(out var item)) batch.Add(item);
                if (batch.Count == 0) continue;
                await _repository.AddBatchAsync(batch);
                foreach (var item in batch) EventRecorded?.Invoke(this, item);
                if (_queue.Reader.Count < 5_000) Interlocked.Exchange(ref _overflowReported, 0);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"数据库写入暂停：{ex.Message}"); }
    }

    private void PublishStatus()
    {
        var scope = IsGlobalMode ? "个固定磁盘" : "个监控根";
        var text = IsPaused ? PauseDescription : $"正在监控 {ActiveWatcherCount}/{ConfiguredRootCount} {scope}";
        StatusChanged?.Invoke(this, text);
    }

    private void ClearCorrelationState()
    {
        _deletes.Clear();
        _changes.Clear();
        _known.Clear();
    }

    private void StopWatchers()
    {
        lock (_stateGate)
        {
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
        }
        ClearCorrelationState();
    }

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(FileMonitorService)); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWatchers();
        _policyTimer.Dispose();
        _availabilityTimer.Dispose();
        _manualResumeTimer?.Dispose();
        _queue.Writer.TryComplete();
        try
        {
            if (!_writerTask.Wait(TimeSpan.FromSeconds(2)))
            {
                _lifetime.Cancel();
                _writerTask.Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch { }
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
