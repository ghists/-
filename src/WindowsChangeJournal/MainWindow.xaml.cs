using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsChangeJournal.Models;
using WindowsChangeJournal.Services;
using Forms = System.Windows.Forms;

namespace WindowsChangeJournal;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<FileEventRecord> _events = [];
    private readonly ObservableCollection<GapRecord> _gaps = [];
    private readonly ConfigService _configService = new();
    private readonly EventRepository _repository = new(AppPaths.DatabasePath);
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _healthTimer;
    private FileMonitorService? _monitor;
    private ForegroundActivityService? _foregroundActivity;
    private Forms.NotifyIcon? _trayIcon;
    private AppConfig _config = new();
    private bool _allowExit;
    private bool _historyMode;
    private bool _refreshInProgress;
    private bool _refreshPending;
    private bool _reloadingEvents;
    private FileEventRecord? _observedEvent;
    private string? _detailProgramRoot;
    private bool _programToggleSaving;
    private bool _disposed;
    private DateTimeOffset _lastHeartbeat;

    public MainWindow()
    {
        InitializeComponent();
        EventsGrid.ItemsSource = _events;
        GapsGrid.ItemsSource = _gaps;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RefreshAsync(); };
        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _healthTimer.Tick += async (_, _) => await RefreshHealthAsync(false);
        Loaded += async (_, _) => await InitializeAsync();
        System.Windows.Application.Current.SessionEnding += (_, _) => _allowExit = true;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && _config.MinimizeToTray)
            {
                Hide();
                WindowState = WindowState.Normal;
            }
        };
    }

    private async Task InitializeAsync()
    {
        try
        {
            SetupTrayIcon();
            await _repository.InitializeAsync();
            await _repository.BeginRecorderSessionAsync();
            _lastHeartbeat = DateTimeOffset.UtcNow;
            try { _config = await _configService.LoadAsync(); }
            catch (Exception ex)
            {
                _config = new AppConfig();
                MessageBox.Show(ex.Message, "配置需要处理", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _monitor = new FileMonitorService(_repository);
            _monitor.EventRecorded += Monitor_EventRecorded;
            _monitor.StatusChanged += Monitor_StatusChanged;
            _monitor.StateChanged += Monitor_StateChanged;
            _monitor.Start(_config);
            _foregroundActivity = new ForegroundActivityService(_repository, () => _monitor?.IsPaused != false);

            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            PopulateDirectoryFilter();
            await _repository.RunMaintenanceAsync(_config.RetentionDays, _config.MaxStorageMb);
            await RefreshAsync();
            _healthTimer.Start();

            if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase))) Hide();
            else if (!_config.MonitorAllFixedDrives && _config.WatchedFolders.Count == 0)
            {
                StatusText.Text = "尚未选择监控目录";
                _ = Dispatcher.BeginInvoke(() => Manage_Click(this, new RoutedEventArgs()));
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "初始化失败";
            MessageBox.Show(ex.ToString(), "初始化失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetupTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开时间记录仪", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("暂停 15 分钟", null, (_, _) => Dispatcher.Invoke(() => _monitor?.PauseManually(TimeSpan.FromMinutes(15))));
        menu.Items.Add("暂停 1 小时", null, (_, _) => Dispatcher.Invoke(() => _monitor?.PauseManually(TimeSpan.FromHours(1))));
        menu.Items.Add("恢复记录", null, (_, _) => Dispatcher.Invoke(() => _monitor?.ResumeManual()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "时间记录仪",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? System.Drawing.SystemIcons.Information,
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void ExitApplication()
    {
        _allowExit = true;
        Close();
    }

    private void Monitor_EventRecorded(object? sender, FileEventRecord e) => Dispatcher.Invoke(() =>
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    });

    private void Monitor_StatusChanged(object? sender, string text) => Dispatcher.Invoke(() => StatusText.Text = text);
    private void Monitor_StateChanged(object? sender, EventArgs e) => Dispatcher.Invoke(async () => await RefreshHealthAsync(true));

    private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e) => Dispatcher.Invoke(() =>
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _monitor?.SetSessionLocked(true);
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _monitor?.SetSessionLocked(false);
    });

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.Invoke(() =>
    {
        if (e.Mode == PowerModes.Suspend) _monitor?.SetResourcePaused(true);
        else if (e.Mode == PowerModes.Resume) _monitor?.SetResourcePaused(false);
    });

    private async Task RefreshAsync()
    {
        if (_historyMode) return;
        if (_refreshInProgress)
        {
            _refreshPending = true;
            return;
        }

        _refreshInProgress = true;
        try
        {
            do
            {
                _refreshPending = false;
                var items = await _repository.SearchAsync(BuildFilter());
                if (_historyMode) return;
                UpdateSourceFilterMenu(items.Select(item => item.OriginDisplay));
                var visibleItems = SourceFilterService.Apply(items, GetSelectedSources(), 1000);

                // Capture as late as possible: the user may select a row while the query is running.
                var selected = EventsGrid.SelectedItem as FileEventRecord ?? _observedEvent;
                ReplaceEvents(visibleItems, selected);
                await RefreshHealthAsync(true);
            }
            while (_refreshPending && !_historyMode);
        }
        catch (Exception ex) { StatusText.Text = $"查询失败：{ex.Message}"; }
        finally { _refreshInProgress = false; }
    }

    private void ReplaceEvents(IEnumerable<FileEventRecord> items, FileEventRecord? preserveSelection)
    {
        _reloadingEvents = true;
        try
        {
            var incoming = items.ToList();
            var existingById = _events.ToDictionary(item => item.Id);
            var desired = incoming
                .Select(item => existingById.TryGetValue(item.Id, out var existing) ? existing : item)
                .ToList();

            if (preserveSelection is null)
            {
                SynchronizeEvents(desired);
                _observedEvent = null;
                ClearEventDetails();
                return;
            }

            var preserved = desired.FirstOrDefault(item => item.Id == preserveSelection.Id);
            if (preserved is null)
            {
                preserved = existingById.TryGetValue(preserveSelection.Id, out var existing)
                    ? existing
                    : preserveSelection;
                var oldIndex = _events.IndexOf(preserved);
                desired.Insert(Math.Clamp(oldIndex, 0, desired.Count), preserved);
            }

            // Update in place instead of clearing the collection. DataGrid therefore keeps the
            // same selected object and its blue highlight while new rows arrive.
            SynchronizeEvents(desired);
            _observedEvent = preserved;
            if (!ReferenceEquals(EventsGrid.SelectedItem, preserved)) EventsGrid.SelectedItem = preserved;
            ShowEventDetails(preserved);
        }
        finally { _reloadingEvents = false; }
    }

    private void SynchronizeEvents(IReadOnlyList<FileEventRecord> desired)
    {
        for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            if (targetIndex < _events.Count && ReferenceEquals(_events[targetIndex], desired[targetIndex])) continue;

            var existingIndex = -1;
            for (var index = targetIndex + 1; index < _events.Count; index++)
            {
                if (!ReferenceEquals(_events[index], desired[targetIndex])) continue;
                existingIndex = index;
                break;
            }

            if (existingIndex >= 0) _events.Move(existingIndex, targetIndex);
            else _events.Insert(targetIndex, desired[targetIndex]);
        }

        while (_events.Count > desired.Count) _events.RemoveAt(_events.Count - 1);
    }

    private SearchFilter BuildFilter()
    {
        DateTimeOffset? from = TimeRangeBox.SelectedIndex switch
        {
            1 => DateTimeOffset.UtcNow.AddMinutes(-10),
            2 => new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today)).ToUniversalTime(),
            3 => DateTimeOffset.UtcNow.AddDays(-7),
            4 => DateTimeOffset.UtcNow.AddDays(-30),
            _ => null
        };
        return new SearchFilter
        {
            Query = SearchBox.Text,
            EventTypes = GetSelectedEventTypes(),
            Directory = DirectoryBox.SelectedIndex > 0 ? DirectoryBox.SelectedItem?.ToString() ?? "" : "",
            FromUtc = from,
            // Source names are inferred from paths after loading, so fetch a larger candidate
            // set and apply the selected source checkboxes in memory.
            Limit = 5000
        };
    }

    private List<string> GetSelectedEventTypes() => EventTypeButton.ContextMenu.Items
        .OfType<MenuItem>()
        .Where(item => item.Tag is string tag && tag != "All" && item.IsChecked)
        .Select(item => (string)item.Tag)
        .ToList();

    private void EventTypeButton_Click(object sender, RoutedEventArgs e)
    {
        if (EventTypeButton.ContextMenu is null) return;
        EventTypeButton.ContextMenu.PlacementTarget = EventTypeButton;
        EventTypeButton.ContextMenu.IsOpen = true;
    }

    private void EventTypeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem selected || selected.Tag is not string selectedTag) return;
        var items = EventTypeButton.ContextMenu.Items.OfType<MenuItem>().ToList();
        var eventItems = items.Where(item => item.Tag is string tag && tag != "All").ToList();
        var allItem = items.First(item => string.Equals(item.Tag as string, "All", StringComparison.Ordinal));
        if (selectedTag == "All")
        {
            foreach (var item in eventItems) item.IsChecked = selected.IsChecked;
        }
        else allItem.IsChecked = eventItems.All(item => item.IsChecked);

        var checkedItems = eventItems.Where(item => item.IsChecked).ToList();
        EventTypeButton.Content = checkedItems.Count switch
        {
            0 => "未选择事件 ▾",
            5 => "全部事件 ▾",
            1 => $"{checkedItems[0].Header} ▾",
            _ => $"已选 {checkedItems.Count} 类事件 ▾"
        };
        Filter_Changed(sender, e);
    }

    private void SourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (SourceButton.ContextMenu is null) return;
        SourceButton.ContextMenu.PlacementTarget = SourceButton;
        SourceButton.ContextMenu.IsOpen = true;
    }

    private void SourceItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem selected || selected.Tag is not string selectedTag) return;
        var sourceItems = GetSourceMenuItems();
        var allItem = SourceButton.ContextMenu.Items.OfType<MenuItem>()
            .First(item => string.Equals(item.Tag as string, "All", StringComparison.Ordinal));
        if (selectedTag == "All")
        {
            foreach (var item in sourceItems) item.IsChecked = selected.IsChecked;
        }
        else allItem.IsChecked = sourceItems.Count > 0 && sourceItems.All(item => item.IsChecked);

        UpdateSourceButtonLabel(sourceItems);
        Filter_Changed(sender, e);
    }

    private void UpdateSourceFilterMenu(IEnumerable<string> discoveredSources)
    {
        var existingItems = GetSourceMenuItems();
        var knownSources = existingItems
            .Select(item => item.Tag as string)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allItem = SourceButton.ContextMenu.Items.OfType<MenuItem>()
            .First(item => string.Equals(item.Tag as string, "All", StringComparison.Ordinal));

        foreach (var source in discoveredSources.Where(source => !string.IsNullOrWhiteSpace(source)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCulture))
        {
            if (!knownSources.Add(source)) continue;
            var item = new MenuItem
            {
                Header = source,
                Tag = source,
                IsCheckable = true,
                IsChecked = allItem.IsChecked,
                StaysOpenOnClick = true
            };
            item.Click += SourceItem_Click;
            SourceButton.ContextMenu.Items.Add(item);
        }

        var updatedItems = GetSourceMenuItems();
        allItem.IsChecked = updatedItems.Count > 0 && updatedItems.All(item => item.IsChecked);
        UpdateSourceButtonLabel(updatedItems);
    }

    private List<MenuItem> GetSourceMenuItems() => SourceButton.ContextMenu.Items
        .OfType<MenuItem>()
        .Where(item => item.Tag is string tag && tag != "All")
        .ToList();

    private List<string> GetSelectedSources() => GetSourceMenuItems()
        .Where(item => item.IsChecked)
        .Select(item => (string)item.Tag)
        .ToList();

    private void UpdateSourceButtonLabel(IReadOnlyCollection<MenuItem> sourceItems)
    {
        var checkedItems = sourceItems.Where(item => item.IsChecked).ToList();
        SourceButton.Content = checkedItems.Count switch
        {
            0 => "未选择来源 ▾",
            _ when checkedItems.Count == sourceItems.Count => "全部来源 ▾",
            1 => $"{checkedItems[0].Header} ▾",
            _ => $"已选 {checkedItems.Count} 个来源 ▾"
        };
    }

    private async Task RefreshHealthAsync(bool reloadGaps)
    {
        if (_monitor is null) return;
        try
        {
            var stats = await _repository.GetStatsAsync();
            if (DateTimeOffset.UtcNow - _lastHeartbeat >= TimeSpan.FromSeconds(30))
            {
                await _repository.HeartbeatAsync();
                _lastHeartbeat = DateTimeOffset.UtcNow;
            }
            var state = _monitor.ConfiguredRootCount == 0 ? "没有可用范围"
                : _monitor.IsPaused ? _monitor.PauseDescription
                : _monitor.ActiveWatcherCount < _monitor.ConfiguredRootCount ? "部分目录不可用"
                : "记录中";
            StatusCardText.Text = state;
            StatusCard.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_monitor.IsPaused ? "#FFF7E6" : "#FFFFFF"));
            PauseButton.Content = _monitor.IsPaused ? "恢复记录" : "暂停记录";
            RootsCardText.Text = _config.MonitorAllFixedDrives
                ? $"{_monitor.ActiveWatcherCount}/{_monitor.ConfiguredRootCount} 个固定磁盘"
                : $"{_monitor.ActiveWatcherCount}/{_monitor.ConfiguredRootCount} 个目录";
            ScopeModeBadge.Text = _config.MonitorAllFixedDrives ? "全局记录" : "自定义范围";
            EventsCardText.Text = $"{stats.EventCount:N0} 条事件";
            StorageCardText.Text = $"{stats.DatabaseBytes / 1024d / 1024d:N1} MB / {_config.MaxStorageMb} MB";
            EarliestText.Text = stats.EarliestUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "暂无记录";
            QueueText.Text = _monitor.PendingEventCount.ToString("N0");
            using var process = Process.GetCurrentProcess();
            MemoryText.Text = $"{process.PrivateMemorySize64 / 1024d / 1024d:N1} MB";
            if (_trayIcon is not null) _trayIcon.Text = _monitor.IsPaused ? "时间记录仪 - 已暂停" : "时间记录仪 - 记录中";
            if (reloadGaps)
            {
                var gaps = await _repository.GetRecentGapsAsync();
                _gaps.Clear();
                foreach (var gap in gaps) _gaps.Add(gap);
            }
        }
        catch { }
    }

    private void PopulateDirectoryFilter()
    {
        DirectoryBox.Items.Clear();
        DirectoryBox.Items.Add("全部目录");
        foreach (var folder in _monitor?.ResolvedRoots ?? _config.WatchedFolders) DirectoryBox.Items.Add(folder);
        DirectoryBox.SelectedIndex = 0;
    }

    private void Filter_Changed(object sender, EventArgs e)
    {
        if (!IsLoaded) return;
        _historyMode = false;
        _observedEvent = null;
        EventsGrid.SelectedItem = null;
        ClearEventDetails();
        BackToSearchButton.Visibility = Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void Manage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_config) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _config = dialog.Result;
            await _configService.SaveAsync(_config);
            try { StartupService.Apply(_config.StartWithWindows); }
            catch (Exception ex) { MessageBox.Show($"设置已保存，但自启动设置失败：{ex.Message}", "自启动", MessageBoxButton.OK, MessageBoxImage.Warning); }
            _monitor?.Start(_config);
            PopulateDirectoryFilter();
            await _repository.RunMaintenanceAsync(_config.RetentionDays, _config.MaxStorageMb);
            await RefreshAsync();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "保存设置失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor is null) return;
        if (_monitor.IsPaused && _monitor.PauseDescription.Contains("手动暂停")) _monitor.ResumeManual();
        else _monitor.PauseManually();
    }

    private void PauseOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.ContextMenu is not null)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
        }
    }
    private void Pause15_Click(object sender, RoutedEventArgs e) => _monitor?.PauseManually(TimeSpan.FromMinutes(15));
    private void Pause60_Click(object sender, RoutedEventArgs e) => _monitor?.PauseManually(TimeSpan.FromHours(1));
    private void Resume_Click(object sender, RoutedEventArgs e) => _monitor?.ResumeManual();

    private void EventsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventsGrid.SelectedItem is not FileEventRecord item)
        {
            if (!_reloadingEvents)
            {
                _observedEvent = null;
                ClearEventDetails();
            }
            return;
        }
        _observedEvent = item;
        ShowEventDetails(item);
    }

    private void ShowEventDetails(FileEventRecord item)
    {
        DetailHint.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailName.Text = item.DisplayName;
        DetailType.Text = $"{item.TypeDisplay} · {item.ConfidenceDisplay} · {item.LocalTime:yyyy-MM-dd HH:mm:ss}";
        var fileExists = File.Exists(item.Path);
        var directoryExists = Directory.Exists(item.Path);
        DetailCurrent.Text = fileExists || directoryExists ? "刚验证存在" : "当前路径不存在或不可用";
        DetailPath.Text = item.Detail;
        var foreground = string.IsNullOrWhiteSpace(item.ForegroundProcess) ? "未取得前台应用线索" : $"当时前台应用：{item.ForegroundProcess}";
        DetailEvidence.Text = $"分类：{item.OriginDisplay}；依据：Windows 目录通知；可信度：{item.ConfidenceDisplay}；{foreground}。来源分类按路径推定，前台应用不等于实际写入进程。";
        ShowProgramMonitoring(item);
    }

    private void ShowProgramMonitoring(FileEventRecord item)
    {
        _detailProgramRoot = null;
        ProgramMonitoringBox.IsChecked = false;
        ProgramMonitoringBox.IsEnabled = false;

        if (EventOriginService.Classify(item.Path, item.OldPath) != EventOrigin.UserOrApplication)
        {
            AssociatedProgramName.Text = "这是系统来源事件";
            AssociatedProgramPath.Text = "";
            ProgramMonitoringNote.Text = "请使用“监控设置”中的 Windows 系统或 Microsoft Defender 独立开关。";
            return;
        }

        var association = ProgramAssociationService.TryIdentify(item.Path, item.OldPath);
        if (association is null)
        {
            AssociatedProgramName.Text = "无法从此路径识别关联程序";
            AssociatedProgramPath.Text = "";
            ProgramMonitoringNote.Text = "只有位于 AppData、Program Files 或 ProgramData 等程序目录中的事件才能单独设置。";
            return;
        }

        _detailProgramRoot = association.RootPath;
        AssociatedProgramName.Text = $"{association.Name}（按路径推定）";
        AssociatedProgramPath.Text = association.RootPath;
        var blockedByGeneralExclusion = ProgramAssociationService.IsBlockedByGeneralExclusion(_config, association.RootPath);
        ProgramMonitoringBox.IsChecked = ProgramAssociationService.IsMonitoringEnabled(_config, association.RootPath);
        ProgramMonitoringBox.IsEnabled = !blockedByGeneralExclusion && !_programToggleSaving;
        ProgramMonitoringNote.Text = blockedByGeneralExclusion
            ? "该目录已在监控设置中被整体排除，需要先移除对应排除目录。"
            : "只影响今后产生的新事件，不删除已经保存的历史记录。";
    }

    private async void ProgramMonitoring_Click(object sender, RoutedEventArgs e)
    {
        if (_programToggleSaving || _detailProgramRoot is null) return;
        var selected = CurrentObservedEvent;
        var root = _detailProgramRoot;
        var enabled = ProgramMonitoringBox.IsChecked == true;
        List<string> previous = [.. _config.DisabledProgramRoots];
        _programToggleSaving = true;
        ProgramMonitoringBox.IsEnabled = false;
        try
        {
            ProgramAssociationService.SetMonitoringEnabled(_config, root, enabled);
            await _configService.SaveAsync(_config);
            _monitor?.Start(_config);
            StatusText.Text = enabled ? "已开始记录此程序相关变化" : "已停止记录此程序相关变化";
            await RefreshHealthAsync(true);
        }
        catch (Exception ex)
        {
            _config.DisabledProgramRoots = previous;
            MessageBox.Show(ex.Message, "程序监控设置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _programToggleSaving = false;
            if (selected is not null) ShowEventDetails(selected);
        }
    }

    private void ClearEventDetails()
    {
        DetailHint.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Collapsed;
        _detailProgramRoot = null;
    }

    private FileEventRecord? CurrentObservedEvent => EventsGrid.SelectedItem as FileEventRecord ?? _observedEvent;

    private void EventsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => LocateSelected();
    private void Locate_Click(object sender, RoutedEventArgs e) => LocateSelected();

    private void LocateSelected()
    {
        if (CurrentObservedEvent is not FileEventRecord item) return;
        try
        {
            if (File.Exists(item.Path) || Directory.Exists(item.Path))
            {
                var args = File.Exists(item.Path) ? $"/select,\"{item.Path}\"" : $"\"{item.Path}\"";
                Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
                return;
            }
            var parent = Path.GetDirectoryName(item.Path);
            if (parent is not null && Directory.Exists(parent))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{parent}\"") { UseShellExecute = true });
            else MessageBox.Show("当前路径不存在，且父目录也不可用。历史记录仍会保留。", "无法定位", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法定位", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentObservedEvent is not FileEventRecord item) return;
        try
        {
            var history = await _repository.GetHistoryAsync(item);
            _historyMode = true;
            ReplaceEvents(history, item);
            BackToSearchButton.Visibility = Visibility.Visible;
            StatusText.Text = $"正在查看 {item.DisplayName} 的 {history.Count} 条历史";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法读取历史", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void BackToSearch_Click(object sender, RoutedEventArgs e)
    {
        _historyMode = false;
        BackToSearchButton.Visibility = Visibility.Collapsed;
        await RefreshAsync();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_events.Count == 0) { MessageBox.Show("当前没有可导出的记录。", "导出", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var include = MessageBox.Show("是否在导出文件中包含完整路径？\n\n选择“否”会保留文件名并隐藏目录。", "导出隐私设置", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (include == MessageBoxResult.Cancel) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv|JSON 文件 (*.json)|*.json", FileName = $"事件记录-{DateTime.Now:yyyyMMdd-HHmm}.csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)) await ExportService.ExportJsonAsync(dialog.FileName, _events, include == MessageBoxResult.Yes);
            else await ExportService.ExportCsvAsync(dialog.FileName, _events, include == MessageBoxResult.Yes);
            StatusText.Text = $"已导出 {_events.Count} 条记录";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDirectory}\"") { UseShellExecute = true });
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowExit && _config.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _trayIcon?.ShowBalloonTip(1800, "时间记录仪仍在运行", "文件变化继续在后台记录，可从托盘重新打开。", Forms.ToolTipIcon.Info);
            return;
        }
        DisposeServices();
    }

    private void DisposeServices()
    {
        if (_disposed) return;
        _disposed = true;
        _healthTimer.Stop();
        _searchTimer.Stop();
        SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        _foregroundActivity?.Dispose();
        _monitor?.Dispose();
        try { Task.Run(() => _repository.EndRecorderSessionAsync()).Wait(TimeSpan.FromSeconds(2)); } catch { }
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
    }
}
