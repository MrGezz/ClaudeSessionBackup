using System.ComponentModel;
using System.Windows;
using ClaudeSessionBackup.App.ViewModels;
using Wpf.Ui.Controls;

namespace ClaudeSessionBackup.App.Views;

/// <summary>
/// The application shell. No business logic here: everything binds to
/// <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>
    /// The notification-area icon, created on first use and only when the user
    /// has asked for it.
    /// </summary>
    /// <remarks>
    /// System.Windows.Forms.NotifyIcon rather than a WPF control: WPF-UI 4.x
    /// removed the tray controls that 3.x shipped, and this one is already in
    /// the Windows Desktop runtime. It is fully qualified because the implicit
    /// System.Windows.Forms using is switched off in the csproj - see the
    /// comment there for why.
    /// </remarks>
    private System.Windows.Forms.NotifyIcon? _tray;

    /// <summary>
    /// Whether the first hide has explained itself yet.
    /// </summary>
    /// <remarks>
    /// A window that vanishes with no explanation reads as a crash. The balloon
    /// is shown once per session, not every time, because a notification on
    /// every minimise is its own kind of annoying.
    /// </remarks>
    private bool _explainedTray;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += OnStateChanged;
    }

    // ------------------------------------------------------------------ tray

    private bool WantsMinimizeToTray =>
        DataContext is MainViewModel vm && vm.MinimizeToTray;

    private bool WantsCloseToTray =>
        DataContext is MainViewModel vm && vm.CloseToTray;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized || !WantsMinimizeToTray)
        {
            return;
        }

        HideToTray();
    }

    private void HideToTray()
    {
        EnsureTray();
        Hide();

        if (!_explainedTray)
        {
            _explainedTray = true;
            _tray?.ShowBalloonTip(
                3000,
                "Claude Session Backup",
                "Still running in the notification area. Double-click to open.",
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void EnsureTray()
    {
        if (_tray is not null)
        {
            _tray.Visible = true;
            return;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());

        // Backup now / Verify: drive the same commands the Dashboard buttons do
        menu.Items.Add("Backup now", null, (_, _) =>
        {
            RestoreFromTray();
            if (DataContext is MainViewModel vm && !vm.IsRunning)
                vm.BackupCommand.Execute(null);
        });
        menu.Items.Add("Verify", null, (_, _) =>
        {
            RestoreFromTray();
            if (DataContext is MainViewModel vm && !vm.IsRunning)
                vm.VerifyCommand.Execute(null);
        });

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        // Exit: route through RealClose so the mid-backup guard in OnClosing still fires.
        // A tray exit must not be a way to skip the "a backup is still running" question.
        menu.Items.Add("Exit", null, (_, _) =>
        {
            RestoreFromTray();
            RealClose();
        });

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Claude Session Backup",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    /// <summary>
    /// Reads the app icon out of the assembly and picks its 16px frame.
    /// </summary>
    /// <remarks>
    /// Asking for 16x16 explicitly matters: app.ico carries 16/32/48/64/128/256,
    /// each rendered from an 8x supersampled master precisely so the small sizes
    /// stay legible. Letting the loader pick would hand back the 256 and scale it
    /// down, which is the mush that separate 16px artwork exists to avoid.
    /// </remarks>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
        using var stream = Application.GetResourceStream(uri)!.Stream;
        return new System.Drawing.Icon(stream, new System.Drawing.Size(16, 16));
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Brings the window back for a second launch of the app.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="App"/> when another instance signals this one. It is
    /// what makes hiding to the tray safe: without it, launching the app again
    /// while it sits hidden would report "already running" and point at a taskbar
    /// with nothing in it.
    /// </remarks>
    internal void ActivateFromOtherInstance() => RestoreFromTray();

    // --------------------------------------------------------------- closing

    /// <summary>
    /// Whether the current close is a REAL close (from the tray Exit menu item or
    /// triggered when CloseToTray is off). Set to true to bypass the hide-to-tray
    /// path in <see cref="OnClosing"/>.
    /// </summary>
    private bool _isRealClose;

    /// <summary>
    /// Initiate a real application close (not a hide-to-tray). Called from the
    /// tray Exit menu item.
    /// </summary>
    private void RealClose()
    {
        _isRealClose = true;
        Close();
    }

    /// <summary>
    /// Guards against closing mid-copy AND implements close-to-tray. A partial
    /// write to the destination is the single most destructive thing this tool can
    /// do: the truncated file survives across runs and the next run sees a
    /// size/time match and skips it, so the damage sticks until someone notices.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Close-to-tray: if the user clicked the window X and CloseToTray is on,
        // hide to the tray instead of actually closing - unless we are in a
        // RealClose path (the tray Exit menu).
        if (!_isRealClose && WantsCloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // Reset so a cancelled close does not leave the flag set
        _isRealClose = false;

        if (DataContext is MainViewModel vm && vm.IsRunning)
        {
            // Fully qualified: WPF-UI shadows System.Windows.MessageBox with its
            // own async version, and the implicit usings bring both into scope.
            var answer = System.Windows.MessageBox.Show(
                "A backup is still running.\n\n" +
                "Closing now may leave partially-written files on the destination. " +
                "Cancel the run first, or close anyway?",
                "Claude Session Backup",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            // Cancel the CTS so the engine's cooperative cancellation path
            // runs and releases the lock file via LockManager.Dispose.
            vm.CancelRun();
        }

        // Dispose explicitly. A NotifyIcon that is not disposed leaves a ghost in
        // the notification area that only disappears when the user happens to
        // hover over it - the icon outliving the process it represents.
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
    }
}
