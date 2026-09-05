using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Engine;

/// <summary>
/// Writes log lines to the timestamped run log file AND forwards them to the
/// IProgress callback. Accumulates warnings for the manifest.
/// </summary>
internal sealed class RunLogWriter
{
    private readonly string _logPath;
    private readonly IProgress<LogLine>? _progress;
    private readonly List<string> _warnings = new();
    private readonly object _lock = new();

    public IReadOnlyList<string> Warnings => _warnings;
    public string LogPath => _logPath;

    public RunLogWriter(string logPath, IProgress<LogLine>? progress)
    {
        _logPath = logPath;
        _progress = progress;
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        var line = new LogLine(DateTime.Now, level, message);
        var text = line.ToString();

        lock (_lock)
        {
            try { File.AppendAllText(_logPath, text + Environment.NewLine); }
            catch { /* best effort - the log file is not critical */ }

            if (level is LogLevel.Warn or LogLevel.Error)
                _warnings.Add(text);
        }

        _progress?.Report(line);
    }
}
