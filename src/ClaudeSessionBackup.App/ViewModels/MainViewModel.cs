using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System.Windows.Threading;
using ClaudeSessionBackup.App.Services;
using ClaudeSessionBackup.Core.Catalog;
using ClaudeSessionBackup.Core.Config;
using ClaudeSessionBackup.Core.Engine;
using ClaudeSessionBackup.Core.Model;
using ClaudeSessionBackup.Core.Rebuild;
using ClaudeSessionBackup.Core.Scheduling;
using ClaudeSessionBackup.Core.Transcripts;

namespace ClaudeSessionBackup.App.ViewModels;

public enum AppPage { Dashboard, Catalog, Restore, Schedule, Settings, Transcript }

/// <summary>
/// The root view model. Owns the settings, the engine instances, and the
/// per-page state. Pages bind directly to this; the nav rail two-way-binds
/// <see cref="CurrentPage"/>.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly ThemeManager _theme;
    private readonly Dispatcher _dispatcher;

    // ----------------------------------------------------------------- core
    private readonly IBackupEngine _engine;
    private readonly ICatalogBuilder _catalogBuilder;
    private readonly IIndexRebuilder _rebuilder;
    private readonly ITaskSchedulerService _scheduler;

    // -------------------------------------------------------------- settings
    private AppSettings _settings;
    private TraySettings _traySettings;

    // --------------------------------------------------------------- paging
    private AppPage _currentPage = AppPage.Dashboard;
    public AppPage CurrentPage
    {
        get => _currentPage;
        set => Set(ref _currentPage, value);
    }

    // ---------------------------------------------------------------- theme
    public bool IsDark => _theme.IsDark;
    public ICommand ToggleThemeCommand { get; }

    // ------------------------------------------------------------- dashboard
    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (Set(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsNotRunning));
            }
        }
    }
    public bool IsNotRunning => !_isRunning;

    private double _elapsedSec;
    public double ElapsedSec
    {
        get => _elapsedSec;
        set => Set(ref _elapsedSec, value);
    }

    private string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    /// <summary>Log lines from the current or last run, shown in the Dashboard log pane.</summary>
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>
    /// Dashboard table rows: one per known store, updated in place from run manifests.
    /// Replaces the earlier StoreDefs (static) + StoreResults (raw) pair so the grid
    /// can show live/backup counts and a status pill without a second collection.
    /// </summary>
    public ObservableCollection<StoreRowViewModel> StoreRows { get; } = new();

    private string _lastRunSummary = "";
    public string LastRunSummary
    {
        get => _lastRunSummary;
        set => Set(ref _lastRunSummary, value);
    }

    private string _scheduledTaskStatus = "";
    public string ScheduledTaskStatus
    {
        get => _scheduledTaskStatus;
        set => Set(ref _scheduledTaskStatus, value);
    }

    // ----------------------------------------------- store discovery warning
    private string _uncoveredDataText = "";
    /// <summary>
    /// Non-empty when the last run found Claude data roots not covered by any store.
    /// Shown as a WARN-tone card on the Dashboard.
    /// </summary>
    public string UncoveredDataText
    {
        get => _uncoveredDataText;
        set
        {
            if (Set(ref _uncoveredDataText, value))
                OnPropertyChanged(nameof(HasUncoveredData));
        }
    }

    /// <summary>True when <see cref="UncoveredDataText"/> is non-empty.</summary>
    public bool HasUncoveredData => !string.IsNullOrEmpty(_uncoveredDataText);

    public ICommand BackupCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand CancelCommand { get; }
    private CancellationTokenSource? _runCts;

    // --------------------------------------------------------------- catalog
    private SessionCatalog? _catalog;
    public ObservableCollection<SessionEntry> CatalogSessions { get; } = new();

    private string _catalogSearchText = "";
    public string CatalogSearchText
    {
        get => _catalogSearchText;
        set
        {
            if (Set(ref _catalogSearchText, value))
            {
                ApplyCatalogFilter();
            }
        }
    }

    private List<SessionEntry> _allSessions = new();

    public ICommand RefreshCatalogCommand { get; }
    public ICommand OpenTranscriptFolderCommand { get; }
    public ICommand OpenTranscriptCommand { get; }

    // ------------------------------------------------------------ transcript
    public TranscriptViewModel TranscriptVM { get; }

    private bool _isTranscriptLoaded;
    public bool IsTranscriptLoaded
    {
        get => _isTranscriptLoaded;
        set => Set(ref _isTranscriptLoaded, value);
    }

    // --------------------------------------------------------------- restore
    public ObservableCollection<SessionEntry> LostSessions { get; } = new();
    public ObservableCollection<SessionEntry> DanglingSessions { get; } = new();

    private RebuildPlan? _rebuildPlan;
    public RebuildPlan? RebuildPlan
    {
        get => _rebuildPlan;
        set => Set(ref _rebuildPlan, value);
    }

    private string _rebuildStatus = "";
    public string RebuildStatus
    {
        get => _rebuildStatus;
        set => Set(ref _rebuildStatus, value);
    }

    public ICommand PlanRebuildCommand { get; }
    public ICommand ApplyRebuildCommand { get; }

    // -------------------------------------------------------------- schedule
    private string _taskTime = "21:00";
    public string TaskTime
    {
        get => _taskTime;
        set => Set(ref _taskTime, value);
    }

    private bool _taskAtLogon = true;
    public bool TaskAtLogon
    {
        get => _taskAtLogon;
        set => Set(ref _taskAtLogon, value);
    }

    private string _taskStatus = "";
    public string TaskStatus
    {
        get => _taskStatus;
        set => Set(ref _taskStatus, value);
    }

    public ICommand InstallTaskCommand { get; }
    public ICommand UninstallTaskCommand { get; }
    public ICommand RefreshTaskStatusCommand { get; }

    // -------------------------------------------------------------- settings
    public string Destination
    {
        get => _settings.Destination;
        set
        {
            if (_settings.Destination != value)
            {
                _settings.Destination = value;
                OnPropertyChanged();

                // Destination changed: previous run data no longer applies.
                foreach (var row in StoreRows) row.Reset();
                LastRunSummary = "";
            }
        }
    }

    public bool IncludeSubagents
    {
        get => _settings.IncludeSubagents;
        set
        {
            if (_settings.IncludeSubagents != value)
            {
                _settings.IncludeSubagents = value;
                OnPropertyChanged();
            }
        }
    }

    public int KeepSnapshots
    {
        get => _settings.KeepSnapshots;
        set
        {
            if (_settings.KeepSnapshots != value)
            {
                _settings.KeepSnapshots = value;
                OnPropertyChanged();
            }
        }
    }

    public bool MinimizeToTray
    {
        get => _traySettings.MinimizeToTray;
        set
        {
            if (_traySettings.MinimizeToTray != value)
            {
                _traySettings.MinimizeToTray = value;
                OnPropertyChanged();
            }
        }
    }

    public bool CloseToTray
    {
        get => _traySettings.CloseToTray;
        set
        {
            if (_traySettings.CloseToTray != value)
            {
                _traySettings.CloseToTray = value;
                OnPropertyChanged();
            }
        }
    }

    public ICommand BrowseDestinationCommand { get; }
    public ICommand SaveSettingsCommand { get; }

    // ---------------------------------------------------------------- demo

    /// <summary>
    /// True when started with <c>--demo</c>. Every piece of data on screen is
    /// fictional; nothing under %APPDATA% is read or written.
    /// </summary>
    public bool IsDemo { get; }

    /// <summary>Window title: includes "DEMO" when in demo mode.</summary>
    public string WindowTitle => IsDemo ? "Claude Session Backup  -  DEMO" : "Claude Session Backup";

    // ----------------------------------------------------------------- ctor

    public MainViewModel(AppSettings settings, TraySettings traySettings, ThemeManager theme, bool isDemo = false)
    {
        _settings = settings;
        _traySettings = traySettings;
        _theme = theme;
        _dispatcher = Dispatcher.CurrentDispatcher;
        IsDemo = isDemo;

        // Core services - stubs throw NotImplementedException, which is fine:
        // the UI renders and navigates without touching the engine.
        _engine = new BackupEngine();
        _catalogBuilder = new CatalogBuilder();
        _rebuilder = new IndexRebuilder();
        _scheduler = new TaskSchedulerService();

        _taskTime = settings.TaskTime;
        _taskAtLogon = settings.TaskAtLogon;

        ToggleThemeCommand = new RelayCommand(() =>
        {
            _theme.Toggle();
            _settings.DarkTheme = _theme.IsDark;
            OnPropertyChanged(nameof(IsDark));
        });

        // In demo mode: Backup/Verify/Apply/Install are disabled; the data is pre-populated.
        BackupCommand = new AsyncRelayCommand(RunBackupAsync, () => !IsRunning && !IsDemo);
        VerifyCommand = new AsyncRelayCommand(RunVerifyAsync, () => !IsRunning && !IsDemo);
        CancelCommand = new RelayCommand(CancelRun, () => IsRunning);

        RefreshCatalogCommand = new AsyncRelayCommand(RefreshCatalogAsync);
        OpenTranscriptFolderCommand = new RelayCommand(OpenTranscriptFolder);

        // Transcript viewer: uses the Core reader/exporter stubs (another agent fills them).
        var transcriptReader = new TranscriptReader() as ITranscriptReader;
        var transcriptExporter = new TranscriptExporter() as ITranscriptExporter;
        var markdownRenderer = new MarkdownRenderer();
        TranscriptVM = new TranscriptViewModel(transcriptReader, transcriptExporter, markdownRenderer, settings);
        TranscriptVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranscriptViewModel.IsLoaded))
            {
                IsTranscriptLoaded = TranscriptVM.IsLoaded;
            }
        };
        OpenTranscriptCommand = new AsyncRelayCommand(OpenTranscriptAsync);

        PlanRebuildCommand = new AsyncRelayCommand(PlanRebuildAsync);
        ApplyRebuildCommand = new AsyncRelayCommand(ApplyRebuildAsync, () => !IsDemo);

        InstallTaskCommand = new RelayCommand(InstallTask, () => !IsDemo);
        UninstallTaskCommand = new RelayCommand(UninstallTask, () => !IsDemo);
        RefreshTaskStatusCommand = new RelayCommand(RefreshTaskStatus);

        BrowseDestinationCommand = new RelayCommand(BrowseDestination);
        SaveSettingsCommand = new RelayCommand(SaveSettings, () => !IsDemo);

        // Build one StoreRowViewModel per known store. The row stays in place;
        // ApplyManifest updates its live/backup/status properties.
        RebuildStoreRows();

        if (isDemo)
        {
            PopulateDemo();
        }
        else
        {
            // Load last-run summary (and apply manifest to rows) and task status
            LoadLastRunSummary();
            RefreshTaskStatus();
        }
    }

    // --------------------------------------------------------- demo population

    /// <summary>
    /// Fill every page with fictional data from <see cref="DemoDataSource"/>.
    /// Nothing real is read; no files are touched.
    /// </summary>
    private void PopulateDemo()
    {
        // Dashboard: manifest -> store rows, log lines, summary
        var manifest = DemoDataSource.BuildManifest();
        ApplyManifest(manifest);
        LastRunSummary = DemoDataSource.LastRunSummary;
        foreach (var line in DemoDataSource.LogLines)
            LogLines.Add(line);
        StatusText = "Completed (8.4s)";

        // Catalog
        _allSessions = DemoDataSource.CatalogSessions();
        ApplyCatalogFilter();

        // Restore: pick up LOST and DANGLING from the same list
        foreach (var s in _allSessions)
        {
            if (s.IsLost) LostSessions.Add(s);
            else if (s.IsDangling) DanglingSessions.Add(s);
        }

        // Rebuild plan
        RebuildPlan = DemoDataSource.BuildRebuildPlan();
        RebuildStatus = $"Plan: {RebuildPlan.ToWrite.Count} records to write, {RebuildPlan.Skipped.Count} skipped";

        // Transcript: load the demo transcript directly into TranscriptVM
        var demoTranscript = DemoDataSource.BuildTranscript();
        TranscriptVM.LoadDemoTranscript(demoTranscript);
        IsTranscriptLoaded = true;

        // Schedule
        TaskStatus = "Not installed";
        ScheduledTaskStatus = "No scheduled task";
    }

    // -------------------------------------------------------------- dashboard

    private async Task RunBackupAsync()
    {
        await RunEngineAsync(verify: false).ConfigureAwait(true);
    }

    private async Task RunVerifyAsync()
    {
        await RunEngineAsync(verify: true).ConfigureAwait(true);
    }

    private async Task RunEngineAsync(bool verify)
    {
        IsRunning = true;
        StatusText = verify ? "Verifying..." : "Backing up...";
        LogLines.Clear();
        _runCts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Timer to update elapsed display
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => ElapsedSec = sw.Elapsed.TotalSeconds;
        timer.Start();

        var progress = new Progress<LogLine>(line =>
        {
            _dispatcher.BeginInvoke(() =>
            {
                LogLines.Add(line.ToString());

                // Auto-scroll: keep the last few hundred lines to avoid unbounded
                // memory when a large run produces thousands of lines.
                while (LogLines.Count > 2000)
                {
                    LogLines.RemoveAt(0);
                }
            });
        });

        try
        {
            var options = _settings.ToBackupOptions(verify);
            var manifest = await Task.Run(
                () => _engine.RunAsync(options, progress, _runCts.Token),
                _runCts.Token).ConfigureAwait(true);

            ApplyManifest(manifest);

            StatusText = manifest.HasFailures
                ? $"Completed with failures ({manifest.Seconds:N1}s)"
                : $"Completed ({manifest.Seconds:N1}s)";

            LastRunSummary = FormatLastRunSummary(manifest);
        }
        catch (NotImplementedException)
        {
            StatusText = "Engine not yet implemented";
            LogLines.Add("[INFO] The backup engine is a stub - not yet implemented.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            LogLines.Add($"[ERROR] {ex.Message}");
        }
        finally
        {
            timer.Stop();
            ElapsedSec = sw.Elapsed.TotalSeconds;
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    /// <summary>
    /// Request cooperative cancellation of the running backup/verify.
    /// Called from the Cancel button and from <see cref="Views.MainWindow.OnClosing"/>
    /// so the engine releases the lock file before the process exits.
    /// </summary>
    public void CancelRun()
    {
        _runCts?.Cancel();
        StatusText = "Cancelling...";
    }

    /// <summary>
    /// (Re)build <see cref="StoreRows"/> from the current settings. Called once at
    /// construction and never again unless the store list itself could change (it
    /// cannot today, but the include-subagents toggle changes the definition list).
    /// </summary>
    private void RebuildStoreRows()
    {
        StoreRows.Clear();
        var opts = _settings.ToBackupOptions();
        foreach (var def in KnownStores.Default(opts))
        {
            StoreRows.Add(new StoreRowViewModel(def));
        }
    }

    /// <summary>
    /// Push per-store results from a <see cref="RunManifest"/> into the
    /// matching <see cref="StoreRows"/> entries. Backup vs verify is inferred
    /// from the manifest's Mode field.
    /// </summary>
    private void ApplyManifest(RunManifest manifest)
    {
        var wasVerify = string.Equals(manifest.Mode, "verify", StringComparison.OrdinalIgnoreCase);
        foreach (var sr in manifest.Stores)
        {
            var row = StoreRows.FirstOrDefault(r =>
                string.Equals(r.Name, sr.Name, StringComparison.OrdinalIgnoreCase));
            row?.ApplyResult(sr, wasVerify);
        }

        // Update store-discovery warning card
        if (manifest.Discovered is { Count: > 0 } disc)
        {
            var paths = string.Join("\n  ", disc.Select(d => d.Path));
            UncoveredDataText = $"Uncovered Claude data: {disc.Count} location(s)\n  {paths}";
        }
        else
        {
            UncoveredDataText = "";
        }
    }

    private void LoadLastRunSummary()
    {
        try
        {
            var manifestPath = Path.Combine(_settings.Destination, "last_run.json");
            if (File.Exists(manifestPath))
            {
                var json = File.ReadAllText(manifestPath);
                var readOpts = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
                };
                var manifest = JsonSerializer.Deserialize<RunManifest>(json, readOpts);
                if (manifest is not null)
                {
                    LastRunSummary = FormatLastRunSummary(manifest);
                    ApplyManifest(manifest);
                }
            }
        }
        catch
        {
            // Never let a corrupt manifest prevent startup.
            LastRunSummary = "(no previous run data)";
        }
    }

    /// <summary>
    /// Format the last-run summary text shown on the Dashboard.
    /// Includes the machine name when available so the user can tell which machine
    /// produced the manifest (relevant when a destination is shared).
    /// </summary>
    private static string FormatLastRunSummary(RunManifest manifest)
    {
        var machineText = !string.IsNullOrEmpty(manifest.Machine)
            ? $" on {manifest.Machine}"
            : "";
        return $"Last run: {manifest.Stamp}{machineText} - {manifest.Mode} - " +
            $"{manifest.Stores.Sum(s => s.Copied)} copied, " +
            $"{manifest.Stores.Sum(s => s.Failed)} failed, " +
            $"{manifest.Warnings.Count} warnings";
    }

    // ---------------------------------------------------------------- catalog

    private async Task RefreshCatalogAsync()
    {
        StatusText = "Building catalog...";
        try
        {
            var options = new CatalogOptions
            {
                OutDir = Path.Combine(_settings.Destination, "catalog"),
                IncludeSubagents = _settings.IncludeSubagents,
                BackupProjectsDir = Path.Combine(_settings.Destination, "live", "code-transcripts"),
                BackupIndexDir = Path.Combine(_settings.Destination, "live", "cowork-index"),
            };

            _catalog = await Task.Run(
                () => _catalogBuilder.BuildAsync(options, null, CancellationToken.None),
                CancellationToken.None).ConfigureAwait(true);

            _allSessions = _catalog.Sessions;
            ApplyCatalogFilter();
            UpdateRestoreLists();
            StatusText = $"Catalog: {_catalog.Counts.Sessions} sessions";
        }
        catch (NotImplementedException)
        {
            StatusText = "Catalog builder not yet implemented";

            // Try loading an existing catalog file instead
            TryLoadExistingCatalog();
        }
        catch (Exception ex)
        {
            StatusText = $"Catalog error: {ex.Message}";
        }
    }

    private void TryLoadExistingCatalog()
    {
        try
        {
            var catalogPath = Path.Combine(_settings.Destination, "catalog", "sessions_catalog.json");
            if (!File.Exists(catalogPath)) return;

            var json = File.ReadAllText(catalogPath);
            _catalog = JsonSerializer.Deserialize<SessionCatalog>(json);
            if (_catalog is not null)
            {
                _allSessions = _catalog.Sessions;
                ApplyCatalogFilter();
                UpdateRestoreLists();
                StatusText = $"Catalog loaded: {_catalog.Counts.Sessions} sessions (from file)";
            }
        }
        catch
        {
            // Catalog file may not exist or be corrupt.
        }
    }

    private void ApplyCatalogFilter()
    {
        CatalogSessions.Clear();
        var search = _catalogSearchText.Trim();
        foreach (var s in _allSessions)
        {
            if (string.IsNullOrEmpty(search) ||
                s.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                s.ProjectDir.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                s.CliSessionId.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                CatalogSessions.Add(s);
            }
        }
    }

    private void UpdateRestoreLists()
    {
        LostSessions.Clear();
        DanglingSessions.Clear();

        if (_catalog is null) return;

        foreach (var s in _catalog.Sessions)
        {
            if (s.IsLost)
            {
                LostSessions.Add(s);
            }
            else if (s.IsDangling)
            {
                DanglingSessions.Add(s);
            }
        }
    }

    private void OpenTranscriptFolder(object? param)
    {
        if (param is not SessionEntry session || session.TranscriptRel is null)
        {
            return;
        }

        // The transcript lives under the live projects dir
        var fullPath = Path.Combine(ClaudePaths.Projects, session.TranscriptRel.Replace('/', '\\'));
        var folder = Path.GetDirectoryName(fullPath);
        if (folder is not null && Directory.Exists(folder))
        {
            System.Diagnostics.Process.Start("explorer.exe", folder);
        }
    }

    // ------------------------------------------------------------ transcript

    private async Task OpenTranscriptAsync(object? param)
    {
        if (param is not SessionEntry session || session.TranscriptRel is null)
        {
            return;
        }

        await TranscriptVM.LoadTranscriptAsync(session).ConfigureAwait(true);
        CurrentPage = AppPage.Transcript;
    }

    // --------------------------------------------------------------- restore

    private async Task PlanRebuildAsync()
    {
        StatusText = "Planning sidebar rebuild...";
        RebuildStatus = "";
        try
        {
            var catalogPath = Path.Combine(_settings.Destination, "catalog", "sessions_catalog.json");
            if (!File.Exists(catalogPath))
            {
                RebuildStatus = "No catalog file found. Run a backup or catalog build first.";
                StatusText = "Ready";
                return;
            }

            var options = new RebuildOptions
            {
                CatalogPath = catalogPath,
                BackupIndexDir = Path.Combine(_settings.Destination, "live", "cowork-index"),
                IncludeSubagents = _settings.IncludeSubagents,
            };

            var plan = await Task.Run(() => _rebuilder.Plan(options, null)).ConfigureAwait(true);
            RebuildPlan = plan;
            RebuildStatus = $"Plan: {plan.ToWrite.Count} records to write, {plan.Skipped.Count} skipped";
            StatusText = "Rebuild plan ready";
        }
        catch (NotImplementedException)
        {
            RebuildStatus = "Index rebuilder not yet implemented";
            StatusText = "Ready";
        }
        catch (Exception ex)
        {
            RebuildStatus = $"Plan error: {ex.Message}";
            StatusText = "Ready";
        }
    }

    private async Task ApplyRebuildAsync()
    {
        if (RebuildPlan is null)
        {
            RebuildStatus = "Run Plan first.";
            return;
        }

        StatusText = "Applying sidebar rebuild...";
        try
        {
            // Check if desktop app is running
            var desktopProcs = _rebuilder.DesktopAppProcesses();
            if (desktopProcs.Count > 0)
            {
                RebuildStatus = "REFUSED: Claude Desktop is running. Close it first (window AND tray icon), " +
                    "then try again. The desktop app holds the sidebar index in memory and would overwrite " +
                    "anything written here.";
                StatusText = "Ready";
                return;
            }

            var options = new RebuildOptions
            {
                CatalogPath = RebuildPlan.CatalogPath,
                BackupIndexDir = Path.Combine(_settings.Destination, "live", "cowork-index"),
                Commit = true,
                IncludeSubagents = _settings.IncludeSubagents,
            };

            var result = await Task.Run(() => _rebuilder.Apply(RebuildPlan, options, null)).ConfigureAwait(true);

            if (result.Refused)
            {
                RebuildStatus = $"REFUSED: {result.RefusalReason}";
            }
            else
            {
                RebuildStatus = $"Applied: {result.WrittenFiles.Count} files written" +
                    (result.IndexBackupDir is not null ? $" (backup at {result.IndexBackupDir})" : "");
            }

            StatusText = "Ready";
        }
        catch (NotImplementedException)
        {
            RebuildStatus = "Index rebuilder not yet implemented";
            StatusText = "Ready";
        }
        catch (Exception ex)
        {
            RebuildStatus = $"Apply error: {ex.Message}";
            StatusText = "Ready";
        }
    }

    // -------------------------------------------------------------- schedule

    private void RefreshTaskStatus()
    {
        try
        {
            var info = _scheduler.Query("ClaudeSessionBackup");
            if (info.Exists)
            {
                TaskStatus = $"Installed - next run: {info.NextRun:yyyy-MM-dd HH:mm}, " +
                    $"last: {info.LastRun:yyyy-MM-dd HH:mm} ({info.LastResult}), state: {info.State}";
                ScheduledTaskStatus = $"Task: {info.State}, next: {info.NextRun:HH:mm}";
            }
            else
            {
                TaskStatus = "Not installed";
                ScheduledTaskStatus = "No scheduled task";
            }
        }
        catch (NotImplementedException)
        {
            TaskStatus = "Scheduler not yet implemented";
            ScheduledTaskStatus = "";
        }
        catch (Exception ex)
        {
            TaskStatus = $"Error: {ex.Message}";
            ScheduledTaskStatus = "";
        }
    }

    private void InstallTask()
    {
        try
        {
            var cliPath = ResolveCliPath();
            var options = new ScheduleOptions
            {
                StartAt = TaskTime,
                AtLogon = TaskAtLogon,
                Destination = _settings.Destination,
                IncludeSubagents = _settings.IncludeSubagents,
            };

            _scheduler.Register(options, cliPath);
            RefreshTaskStatus();
            StatusText = "Scheduled task installed";
        }
        catch (NotImplementedException)
        {
            StatusText = "Scheduler not yet implemented";
        }
        catch (Exception ex)
        {
            StatusText = $"Install failed: {ex.Message}";
        }
    }

    private void UninstallTask()
    {
        try
        {
            _scheduler.Unregister("ClaudeSessionBackup");
            RefreshTaskStatus();
            StatusText = "Scheduled task removed";
        }
        catch (NotImplementedException)
        {
            StatusText = "Scheduler not yet implemented";
        }
        catch (Exception ex)
        {
            StatusText = $"Uninstall failed: {ex.Message}";
        }
    }

    /// <summary>
    /// The CLI exe is expected next to the app exe: both are published into the
    /// same output folder by the solution build.
    /// </summary>
    private static string ResolveCliPath()
    {
        var appDir = AppContext.BaseDirectory;
        var cliExe = Path.Combine(appDir, "ClaudeSessionBackup.Cli.exe");
        return File.Exists(cliExe) ? cliExe : cliExe; // Return path even if missing, scheduler will report it
    }

    // -------------------------------------------------------------- settings

    private void BrowseDestination()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select backup destination folder",
            InitialDirectory = _settings.Destination,
        };

        if (dlg.ShowDialog() == true)
        {
            Destination = dlg.FolderName;
        }
    }

    private void SaveSettings()
    {
        if (IsDemo)
        {
            StatusText = "Demo mode: settings not saved";
            return;
        }

        _settings.TaskTime = TaskTime;
        _settings.TaskAtLogon = TaskAtLogon;
        // MinimizeToTray and CloseToTray are already written through to _traySettings
        // by their property setters, so no explicit copy is needed here.

        try
        {
            SettingsStore.Save(_settings);
            TraySettingsStore.Save(_traySettings);
            StatusText = "Settings saved";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }
}
