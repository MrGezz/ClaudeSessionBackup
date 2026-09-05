using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using ClaudeSessionBackup.App.Services;
using ClaudeSessionBackup.App.ViewModels;
using ClaudeSessionBackup.App.Views;
using ClaudeSessionBackup.Core.Config;

namespace ClaudeSessionBackup.App;

public partial class App : Application
{
    /// <summary>
    /// Single-instance guard.
    /// </summary>
    /// <remarks>
    /// Not a UX nicety. Two copies of the app can each start a run against the
    /// same destination, and two writers copying into one tree interleave
    /// partial files and race the shrink guard - the second instance can see a
    /// file the first has not finished writing and quarantine it. The mutex is
    /// Local, not Global: per-user is the right scope because the scheduled
    /// task and the app both run as the interactive user.
    /// </remarks>
    private static Mutex? _instanceMutex;

    /// <summary>
    /// Whether THIS process actually acquired <see cref="_instanceMutex"/>.
    /// </summary>
    /// <remarks>
    /// The second instance still constructs a Mutex object - it just does not
    /// own the underlying handle, because initiallyOwned is ignored when the
    /// named mutex already exists. Calling ReleaseMutex() from a non-owner
    /// throws ApplicationException, which in OnExit is unhandled.
    /// </remarks>
    private static bool _ownsInstanceMutex;

    /// <summary>
    /// Signalled by a second launch to ask the running instance to show itself.
    /// </summary>
    private static EventWaitHandle? _activateSignal;

    private static RegisteredWaitHandle? _activateRegistration;

    private const string ActivateEventName = "Local\\ClaudeSessionBackup.App.Activate";

    private ThemeManager? _theme;

    /// <summary>An error dialog is on screen right now.</summary>
    /// <remarks>
    /// MUST be honoured before showing another. A modal MessageBox runs its own
    /// message loop, so the dispatcher keeps pumping while it is up: a fault
    /// that recurs throws again INSIDE the dialog and opens another on top. That
    /// nests without limit and ends as a stack overflow. Observed for real in
    /// the sibling Sync-ACC project.
    /// </remarks>
    private static bool _showingError;

    /// <summary>Faults that arrived while a dialog was already up.</summary>
    private static int _suppressedErrors;

    /// <summary>
    /// True when the process was started with <c>--demo</c>. The app runs on
    /// entirely fictional data: no settings are loaded or saved, no real stores
    /// are touched, and every string visible on screen is invented.
    /// </summary>
    internal static bool IsDemo { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        IsDemo = e.Args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));

        // Demo mode uses a separate mutex name so it can coexist with the real app.
        var mutexName = IsDemo
            ? "Local\\ClaudeSessionBackup.App.Demo"
            : "Local\\ClaudeSessionBackup.App.SingleInstance";

        _instanceMutex = new Mutex(initiallyOwned: true, mutexName, out var isFirst);
        _ownsInstanceMutex = isFirst;
        if (!isFirst)
        {
            if (!IsDemo && TrySignalRunningInstance())
            {
                Shutdown();
                return;
            }

            System.Windows.MessageBox.Show(
                "Claude Session Backup is already running, but it did not respond." + Environment.NewLine + Environment.NewLine +
                "Look for it in the taskbar. If it is nowhere to be found, end " +
                "'ClaudeSessionBackup' in Task Manager and start it again.",
                "Claude Session Backup",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (!IsDemo)
            StartListeningForOtherInstances();

        // A dispatcher exception must not take the process down mid-copy.
        DispatcherUnhandledException += OnUnhandledException;

        // In demo mode: use hard-coded defaults, never read or write %APPDATA%.
        var settings = IsDemo ? new AppSettings { Destination = @"D:\Backups\Claude" } : SettingsStore.Load();
        var traySettings = IsDemo ? new TraySettings() : TraySettingsStore.Load();

        // Everything below builds the UI, and any of it can throw a
        // XamlParseException at LOAD time. If one does, the process must EXIT:
        // WPF keeps pumping messages even when no window was ever shown, so an
        // unhandled startup fault leaves an invisible process alive - still
        // holding the single-instance mutex.
        try
        {
            _theme = new ThemeManager();
            _theme.Apply(settings.DarkTheme);

            var vm = new MainViewModel(settings, traySettings, _theme, IsDemo);
            var window = new MainWindow { DataContext = vm };
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
            Shutdown();
        }
    }

    private static bool TrySignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
            {
                using (existing)
                {
                    existing.Set();
                    return true;
                }
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Older build running, or the event was never created.
        }
        catch (UnauthorizedAccessException)
        {
            // Another desktop session owns it.
        }

        return false;
    }

    private void StartListeningForOtherInstances()
    {
        try
        {
            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateRegistration = ThreadPool.RegisterWaitForSingleObject(
                _activateSignal,
                (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is Views.MainWindow main)
                    {
                        main.ActivateFromOtherInstance();
                    }
                }),
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception)
        {
            // A missing activation channel costs a nicety, not the app.
        }
    }

    /// <summary>
    /// Expands an exception chain into something a person can act on.
    /// </summary>
    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        var depth = 0;

        for (Exception? e = ex; e is not null; e = e.InnerException, depth++)
        {
            sb.Append(new string(' ', depth * 2))
              .Append(e.GetType().Name)
              .Append(": ")
              .AppendLine(e.Message);

            if (e is XamlParseException xpe && xpe.LineNumber > 0)
            {
                sb.Append(new string(' ', depth * 2))
                  .AppendLine($"  in {xpe.BaseUri} line {xpe.LineNumber}, position {xpe.LinePosition}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Reports a fault that happened before the main window existed. Written to
    /// %APPDATA%\ClaudeSessionBackup\startup-error.log (the STARTUP-ERROR LOG
    /// that the launch gate checks for).
    /// </summary>
    private static void ReportStartupFailure(Exception ex)
    {
        var detail = Describe(ex);
        var path = "(not written)";

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClaudeSessionBackup");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "startup-error.log");
            File.AppendAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  startup failed{Environment.NewLine}" +
                ex + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
            // Never let the error reporter become the error.
        }

        System.Windows.MessageBox.Show(
            "Claude Session Backup could not start." + Environment.NewLine + Environment.NewLine +
            detail + Environment.NewLine + Environment.NewLine +
            "Details written to:" + Environment.NewLine + path,
            "Claude Session Backup",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (MainWindow is null)
        {
            e.Handled = true;
            ReportStartupFailure(e.Exception);
            Shutdown();
            return;
        }

        e.Handled = true;

        if (_showingError)
        {
            _suppressedErrors++;
            return;
        }

        _showingError = true;
        try
        {
            var extra = _suppressedErrors > 0
                ? Environment.NewLine + Environment.NewLine +
                  $"({_suppressedErrors} further error(s) were suppressed while this was open.)"
                : string.Empty;

            System.Windows.MessageBox.Show(
                "Claude Session Backup hit an unexpected error." + Environment.NewLine + Environment.NewLine +
                Describe(e.Exception) + Environment.NewLine + Environment.NewLine +
                "Any run in progress has been left to finish or cancel cleanly." + extra,
                "Claude Session Backup",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
            _suppressedErrors = 0;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceMutex)
        {
            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Abandoned or already released; shutting down either way.
            }

            _ownsInstanceMutex = false;
        }

        _instanceMutex?.Dispose();

        _activateRegistration?.Unregister(null);
        _activateSignal?.Dispose();

        base.OnExit(e);
    }
}
