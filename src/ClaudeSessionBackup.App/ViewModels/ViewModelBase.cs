using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClaudeSessionBackup.App.ViewModels;

/// <summary>
/// Minimal INotifyPropertyChanged base.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taking a dependency on CommunityToolkit.Mvvm. This
/// app has a handful of view models and no need for source generators, and
/// every package added here is one more thing a colleague must restore before
/// they can build a tool whose entire job is to run unattended on a
/// workstation.
/// </remarks>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Assigns and raises only when the value actually changed.
    /// </summary>
    /// <remarks>
    /// The equality check is load-bearing here, not an optimisation: the run
    /// monitor updates several times a second from engine progress, and
    /// raising PropertyChanged for unchanged values would re-run every binding
    /// and converter on every tick while a 40,000-file walk is in progress.
    /// </remarks>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
