using Microsoft.Win32;
using System.Windows;
using WindowsChangeJournal.Models;
using WindowsChangeJournal.Services;
using Forms = System.Windows.Forms;

namespace WindowsChangeJournal;

public partial class SettingsWindow : Window
{
    public AppConfig Result { get; private set; }

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        Result = Clone(config);
        ApplyToControls();
    }

    private static AppConfig Clone(AppConfig source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        MonitorAllFixedDrives = source.MonitorAllFixedDrives,
        RecordWindowsSystemEvents = source.RecordWindowsSystemEvents,
        RecordDefenderProtectionHistory = source.RecordDefenderProtectionHistory,
        WatchedFolders = [.. source.WatchedFolders],
        ExcludedFolders = [.. source.ExcludedFolders],
        DisabledProgramRoots = [.. source.DisabledProgramRoots],
        ExcludedProcesses = [.. source.ExcludedProcesses],
        IncludeSubdirectories = source.IncludeSubdirectories,
        RecordContentChanges = source.RecordContentChanges,
        ExcludedProcessMode = source.ExcludedProcessMode,
        RetentionDays = source.RetentionDays,
        MaxStorageMb = source.MaxStorageMb,
        StartWithWindows = source.StartWithWindows,
        MinimizeToTray = source.MinimizeToTray
    };

    private void ApplyToControls()
    {
        WatchList.ItemsSource = null;
        WatchList.ItemsSource = Result.WatchedFolders;
        ExcludeList.ItemsSource = null;
        ExcludeList.ItemsSource = Result.ExcludedFolders;
        ProcessBox.Text = string.Join(Environment.NewLine, Result.ExcludedProcesses);
        ProcessModeBox.SelectedIndex = string.Equals(Result.ExcludedProcessMode, "Running", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        GlobalModeBox.IsChecked = Result.MonitorAllFixedDrives;
        SystemEventsBox.IsChecked = Result.RecordWindowsSystemEvents;
        DefenderEventsBox.IsChecked = Result.RecordDefenderProtectionHistory;
        RecursiveBox.IsChecked = Result.IncludeSubdirectories;
        ContentChangesBox.IsChecked = Result.RecordContentChanges;
        RetentionBox.Text = Result.RetentionDays.ToString();
        StorageBox.Text = Result.MaxStorageMb.ToString();
        StartupBox.IsChecked = Result.StartWithWindows;
        TrayBox.IsChecked = Result.MinimizeToTray;
    }

    private static string? PickFolder()
    {
        using var dialog = new Forms.FolderBrowserDialog { ShowNewFolderButton = false, Description = "选择一个要监控或排除的目录" };
        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private void AddWatch_Click(object sender, RoutedEventArgs e) => AddWatch(PickFolder());
    private void AddDesktop_Click(object sender, RoutedEventArgs e) => AddWatch(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    private void AddDocuments_Click(object sender, RoutedEventArgs e) => AddWatch(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    private void AddDownloads_Click(object sender, RoutedEventArgs e) => AddWatch(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

    private void AddWatch(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            path = PathGuard.Normalize(path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("所选目录当前不可用。");
            PathGuard.RejectReparsePoint(path);
            Result.WatchedFolders.Add(path);
            Result.WatchedFolders = PathGuard.NormalizeAndCollapse(Result.WatchedFolders);
            ApplyToControls();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法添加目录", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void RemoveWatch_Click(object sender, RoutedEventArgs e)
    {
        if (WatchList.SelectedItem is string path) Result.WatchedFolders.Remove(path);
        ApplyToControls();
    }

    private void AddExclude_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder();
        if (path is null) return;
        try
        {
            path = PathGuard.Normalize(path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("所选目录当前不可用。");
            PathGuard.RejectReparsePoint(path);
            Result.ExcludedFolders.Add(path);
            Result.ExcludedFolders = PathGuard.NormalizeAndCollapse(Result.ExcludedFolders);
            ApplyToControls();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法添加排除目录", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void RemoveExclude_Click(object sender, RoutedEventArgs e)
    {
        if (ExcludeList.SelectedItem is string path) Result.ExcludedFolders.Remove(path);
        ApplyToControls();
    }

    private AppConfig BuildFromControls()
    {
        if (!int.TryParse(RetentionBox.Text, out var retention)) throw new InvalidOperationException("历史保留天数必须是整数。");
        if (!int.TryParse(StorageBox.Text, out var storage)) throw new InvalidOperationException("数据空间上限必须是整数 MB。");
        var result = Clone(Result);
        result.MonitorAllFixedDrives = GlobalModeBox.IsChecked == true;
        result.RecordWindowsSystemEvents = SystemEventsBox.IsChecked == true;
        result.RecordDefenderProtectionHistory = DefenderEventsBox.IsChecked == true;
        result.ExcludedProcesses = ProcessBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFileNameWithoutExtension).Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        result.ExcludedProcessMode = ProcessModeBox.SelectedIndex == 1 ? "Running" : "Foreground";
        result.IncludeSubdirectories = RecursiveBox.IsChecked == true;
        result.RecordContentChanges = ContentChangesBox.IsChecked == true;
        result.RetentionDays = retention;
        result.MaxStorageMb = storage;
        result.StartWithWindows = StartupBox.IsChecked == true;
        result.MinimizeToTray = TrayBox.IsChecked == true;
        ConfigService.Serialize(result);
        return result;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Result = BuildFromControls();
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "设置有误", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "时间记录仪设置 (*.json)|*.json|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Result = ConfigService.Deserialize(await File.ReadAllTextAsync(dialog.FileName));
            ApplyToControls();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法导入", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "时间记录仪设置 (*.json)|*.json", FileName = "时间记录仪设置.json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { await File.WriteAllTextAsync(dialog.FileName, ConfigService.Serialize(BuildFromControls())); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法导出", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
