namespace ClaudeSessionBackup.Core.Engine;

/// <summary>Shared formatting helpers used by multiple engine components.</summary>
internal static class Formatting
{
    /// <summary>Formats a byte count as a human-readable string (e.g. "1.2 MB").</summary>
    internal static string FormatBytes(long n)
    {
        double d = n;
        foreach (var u in new[] { "B", "KB", "MB", "GB", "TB" })
        {
            if (d < 1024 || u == "TB")
                return u == "B" ? $"{d:N0} {u}" : $"{d:N1} {u}";
            d /= 1024;
        }
        return $"{d:N1} TB";
    }
}
