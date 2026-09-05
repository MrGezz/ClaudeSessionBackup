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
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Brings the window back for a second launch of the app.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="App"/> when another instance signals this one. It is
    /// what makes single-instance safe: without it, launching the app again
    /// while it sits minimised would report "already running" and point at a
    /// taskbar with nothing in it.
    /// </remarks>
    internal void ActivateFromOtherInstance()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Guards against closing mid-copy. A partial write to the destination is
    /// the single most destructive thing this tool can do: the truncated file
    /// survives across runs and the next run sees a size/time match and skips
    /// it, so the damage sticks until someone notices.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
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
            }
            else
            {
                // Cancel the CTS so the engine's cooperative cancellation path
                // runs and releases the lock file via LockManager.Dispose.
                vm.CancelRun();
            }
        }
    }
}
