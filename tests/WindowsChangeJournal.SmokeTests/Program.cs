using Microsoft.Data.Sqlite;
using WindowsChangeJournal.Models;
using WindowsChangeJournal.Services;

var testRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "smoke-data"));
if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
Directory.CreateDirectory(testRoot);
var a = Directory.CreateDirectory(Path.Combine(testRoot, "a")).FullName;
var b = Directory.CreateDirectory(Path.Combine(testRoot, "b")).FullName;
var nested = Directory.CreateDirectory(Path.Combine(a, "nested")).FullName;
var database = Path.Combine(testRoot, "events.db");

FileMonitorService? monitor = null;
try
{
    Assert(new AppConfig().MonitorAllFixedDrives, "Global fixed-drive monitoring is not the default.");
    Assert(!new AppConfig().RecordWindowsSystemEvents, "Windows system events must be disabled by default.");
    Assert(!new AppConfig().RecordDefenderProtectionHistory, "Defender protection-history events must be disabled by default.");

    var windowsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "system.tmp");
    var defenderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Quarantine", "entry");
    Assert(EventOriginService.Classify(windowsPath) == EventOrigin.WindowsSystem, "Windows system path classification failed.");
    Assert(EventOriginService.Classify(defenderPath) == EventOrigin.MicrosoftDefender, "Microsoft Defender path classification failed.");
    Assert(EventOriginService.Classify(Path.Combine(a, "user.txt")) == EventOrigin.UserOrApplication, "User/application path classification failed.");
    Assert(!EventOriginService.ShouldRecord(new AppConfig { RecordWindowsSystemEvents = true }, defenderPath), "Defender switch must remain independent from the Windows system switch.");
    Assert(EventOriginService.ShouldRecord(new AppConfig { RecordDefenderProtectionHistory = true }, defenderPath), "Defender switch did not enable Defender events.");
    Assert(!EventOriginService.ShouldRecord(new AppConfig { RecordDefenderProtectionHistory = true }, windowsPath), "Windows system switch must remain independent from the Defender switch.");
    var tdmRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TDM");
    var tdmEvent = Path.Combine(tdmRoot, "tdm_track.dat");
    var association = ProgramAssociationService.TryIdentify(tdmEvent);
    Assert(association is not null && association.Name == "TDM" && string.Equals(association.RootPath, tdmRoot, StringComparison.OrdinalIgnoreCase), "Program association for AppData path failed.");
    Assert(new FileEventRecord { Path = tdmEvent }.OriginDisplay == "TDM（推定）", "Timeline source did not display the inferred program name.");
    var codexEvent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "OpenAI.Codex_2p2nqsd0c76g0", "LocalCache", "cache.tmp");
    Assert(new FileEventRecord { Path = codexEvent }.OriginDisplay == "Codex（推定）", "Packaged application name inference failed.");
    var sourceCandidates = new[]
    {
        new FileEventRecord { Path = codexEvent },
        new FileEventRecord { Path = tdmEvent },
        new FileEventRecord { Path = windowsPath }
    };
    var codexOnly = SourceFilterService.Apply(sourceCandidates, ["Codex（推定）"], 100);
    Assert(codexOnly.Count == 1 && codexOnly[0].Path == codexEvent, "Multi-select source filtering returned an unexpected source.");
    Assert(SourceFilterService.Apply(sourceCandidates, [], 100).Count == 0, "An empty source selection must return no events.");
    var programConfig = new AppConfig();
    ProgramAssociationService.SetMonitoringEnabled(programConfig, tdmRoot, false);
    Assert(!ProgramAssociationService.IsMonitoringEnabled(programConfig, tdmRoot), "Per-program monitoring switch did not disable the program root.");
    var savedProgramConfig = ConfigService.Deserialize(ConfigService.Serialize(programConfig));
    Assert(!ProgramAssociationService.IsMonitoringEnabled(savedProgramConfig, tdmRoot), "Per-program monitoring switch was not preserved in configuration.");
    ProgramAssociationService.SetMonitoringEnabled(programConfig, tdmRoot, true);
    Assert(ProgramAssociationService.IsMonitoringEnabled(programConfig, tdmRoot), "Per-program monitoring switch did not re-enable the program root.");
    programConfig.ExcludedFolders = [tdmRoot];
    Assert(ProgramAssociationService.IsBlockedByGeneralExclusion(programConfig, tdmRoot), "General folder exclusion must take precedence over the per-program switch.");
    var collapsed = PathGuard.NormalizeAndCollapse([a, nested, a]);
    Assert(collapsed.Count == 1 && string.Equals(collapsed[0], a, StringComparison.OrdinalIgnoreCase), "Nested watch roots were not collapsed.");
    Assert(PathGuard.IsUnderOrEqual(nested, a), "Path containment test failed.");

    var repository = new EventRepository(database);
    await repository.InitializeAsync();
    await repository.BeginRecorderSessionAsync();
    monitor = new FileMonitorService(repository);
    monitor.Start(new AppConfig { MonitorAllFixedDrives = false, WatchedFolders = [a, b], IncludeSubdirectories = true, RecordContentChanges = true });
    await Task.Delay(400);

    var original = Path.Combine(nested, "sample.txt");
    var renamed = Path.Combine(nested, "renamed.txt");
    var moved = Path.Combine(b, "renamed.txt");
    await File.WriteAllTextAsync(original, "smoke-test");
    await Task.Delay(450);
    await File.AppendAllTextAsync(original, "-changed");
    await Task.Delay(2400);
    File.Move(original, renamed);
    await Task.Delay(450);
    File.Move(renamed, moved);
    await Task.Delay(650);
    File.Delete(moved);
    await Task.Delay(3600);

    monitor.PauseManually();
    var hiddenDuringPause = Path.Combine(a, "paused.txt");
    await File.WriteAllTextAsync(hiddenDuringPause, "must-not-be-recorded");
    await Task.Delay(500);
    monitor.ResumeManual();
    await Task.Delay(150);
    var visibleAfterResume = Path.Combine(a, "resumed.txt");
    await File.WriteAllTextAsync(visibleAfterResume, "record-me");
    await Task.Delay(700);

    var events = await repository.SearchAsync(new SearchFilter { Query = ".txt", Limit = 200 });
    var types = events.Select(e => e.EventType).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var required in new[] { "Created", "Changed", "Renamed", "Moved", "Deleted" })
        Assert(types.Contains(required), $"Missing expected event: {required}. Found: {string.Join(", ", types)}");
    Assert(events.All(e => !e.Path.EndsWith("paused.txt", StringComparison.OrdinalIgnoreCase)), "An event from a paused interval was persisted.");
    Assert(events.Any(e => e.Path.EndsWith("resumed.txt", StringComparison.OrdinalIgnoreCase)), "Recording did not resume.");
    Assert(events.Where(e => e.EventType == "Renamed").Any(e => !string.IsNullOrWhiteSpace(e.EntityId)), "Stable file identity was not stored for rename.");

    var changed = await repository.SearchAsync(new SearchFilter { EventType = "Changed", Directory = a, Limit = 50 });
    Assert(changed.Count > 0, "Combined type and directory search returned no change events.");
    var selectedTypes = await repository.SearchAsync(new SearchFilter { EventTypes = ["Changed", "Renamed"], Limit = 200 });
    Assert(selectedTypes.Count > 0 && selectedTypes.All(item => item.EventType is "Changed" or "Renamed"), "Multi-select event type search returned unexpected events.");
    var noTypes = await repository.SearchAsync(new SearchFilter { EventTypes = [], Limit = 200 });
    Assert(noTypes.Count == 0, "An empty event type selection must return no events.");
    await repository.EndRecorderSessionAsync();
    await Task.Delay(1100);
    await repository.BeginRecorderSessionAsync();
    var gaps = await repository.GetRecentGapsAsync();
    Assert(gaps.Any(g => g.Reason.Contains("手动暂停", StringComparison.Ordinal)), "Manual pause gap was not recorded.");
    Assert(gaps.Any(g => g.Reason == "程序未运行"), "Recorder downtime gap was not recorded.");
    var stats = await repository.GetStatsAsync();
    Assert(stats.EventCount >= events.Count && stats.DatabaseBytes > 0, "Repository health statistics are invalid.");

    Console.WriteLine($"PASS: {events.Count} events; {string.Join(", ", types.Order())}; {gaps.Count} gap(s); {stats.DatabaseBytes} bytes");
}
finally
{
    monitor?.Dispose();
    SqliteConnection.ClearAllPools();
    try { Directory.Delete(testRoot, true); } catch { }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
