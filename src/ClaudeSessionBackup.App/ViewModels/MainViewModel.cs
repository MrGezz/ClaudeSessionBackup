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

namespace ClaudeSessionBackup.App.ViewModels;

public enum AppPage { Dashboard, Catalog, Restore, Schedule, Settings }

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

    /// <summary>Store results from the last run manifest, shown in the Dashboard table.</summary>
    public ObservableCollection<StoreResult> StoreResults { get; } = new();

    /// <summary>Known stores with their descriptions, for the static table.</summary>
    public ObservableCollection<StoreDefinition> StoreDefs { get; } = new();

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

    public ICommand BrowseDestinationCommand { get; }
    public ICommand SaveSettingsCommand { get; }

    // ----------------------------------------------------------------- ctor

    public MainViewModel(AppSettings settings, ThemeManager theme)
    {
        _settings = settings;
        _theme = theme;
        _dispatcher = Dispatcher.CurrentDispatcher;

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

        BackupCommand = new AsyncRelayCommand(RunBackupAsync, () => !IsRunning);
        VerifyCommand = new AsyncRelayCommand(RunVerifyAsync, () => !IsRunning);
        CancelCommand = new RelayCommand(CancelRun, () => IsRunning);

        RefreshCatalogCommand = new AsyncRelayCommand(RefreshCatalogAsync);
        OpenTranscriptFolderCommand = new RelayCommand(OpenTranscriptFolder);

        PlanRebuildCommand = new AsyncRelayCommand(PlanRebuildAsync);
        ApplyRebuildCommand = new AsyncRelayCommand(ApplyRebuildAsync);

        InstallTaskCommand = new RelayCommand(InstallTask);
        UninstallTaskCommand = new RelayCommand(UninstallTask);
        RefreshTaskStatusCommand = new RelayCommand(RefreshTaskStatus);

        BrowseDestinationCommand = new RelayCommand(BrowseDestination);
        SaveSettingsCommand = new RelayCommand(SaveSettings);

        // Populate the static store definitions table
        var defaultOpts = _settings.ToBackupOptions();
        foreach (var store in KnownStores.Default(defaultOpts))
        {
            StoreDefs.Add(store);
        }

        // Load last-run summary and scheduled-task status
        LoadLastRunSummary();
        RefreshTaskStatus();
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
        StoreResults.Clear();
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

            foreach (var sr in manifest.Stores)
            {
                StoreResults.Add(sr);
            }

            StatusText = manifest.HasFailures
                ? $"Completed with failures ({manifest.Seconds:N1}s)"
                : $"Completed ({manifest.Seconds:N1}s)";

            LastRunSummary = $"Last run: {manifest.Stamp} - {manifest.Mode} - " +
                $"{manifest.Stores.Sum(s => s.Copied)} copied, " +
                $"{manifest.Stores.Sum(s => s.Failed)} failed, " +
                $"{manifest.Warnings.Count} warnings";
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
                    LastRunSummary = $"Last run: {manifest.Stamp} - {manifest.Mode} - " +
                        $"{manifest.Stores.Sum(s => s.Copied)} copied, " +
                        $"{manifest.Stores.Sum(s => s.Failed)} failed, " +
                        $"{manifest.Warnings.Count} warnings ({manifest.Seconds:N1}s)";

                    foreach (var sr in manifest.Stores)
                    {
                        StoreResults.Add(sr);
                    }
                }
            }
        }
        catch
        {
            // Never let a corrupt manifest prevent startup.
            LastRunSummary = "(no previous run data)";
        }
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
        _settings.TaskTime = TaskTime;
        _settings.TaskAtLogon = TaskAtLogon;

        try
        {
            SettingsStore.Save(_settings);
            StatusText = "Settings saved";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }
}
