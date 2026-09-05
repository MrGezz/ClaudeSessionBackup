using System.Diagnostics;
using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Engine;

/// <summary>
/// Manages the .lock file at the destination root. If the lock exists and is younger
/// than 3 hours, the run is refused. Always removed in a finally block.
/// </summary>
internal sealed class LockManager : IDisposable
{
    private readonly string _lockFile;
    private bool _acquired;

    public LockManager(string lockFile)
    {
        _lockFile = lockFile;
    }

    /// <summary>
    /// Try to acquire the lock. Returns true if acquired, false if another run holds it.
    /// </summary>
    public bool TryAcquire(IProgress<LogLine>? progress)
    {
        if (File.Exists(_lockFile))
        {
            var lockAge = DateTime.Now - File.GetLastWriteTime(_lockFile);
            if (lockAge.TotalHours < 3)
            {
                progress?.Report(new LogLine(DateTime.Now, LogLevel.Warn,
                    $"another run appears active (lock {_lockFile} is fresh) - exiting"));
                return false;
            }
            // Stale lock - take it over
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_lockFile)!);
        File.WriteAllText(_lockFile, Process.GetCurrentProcess().Id.ToString());
        _acquired = true;
        return true;
    }

    public void Dispose()
    {
        if (_acquired)
        {
            try { if (File.Exists(_lockFile)) File.Delete(_lockFile); } catch { /* best effort */ }
            _acquired = false;
        }
    }
}
