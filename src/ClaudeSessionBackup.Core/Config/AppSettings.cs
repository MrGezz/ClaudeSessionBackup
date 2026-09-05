using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Config;

/// <summary>User preferences shared by the app and the CLI. Stored at %APPDATA%\ClaudeSessionBackup\settings.json.</summary>
public sealed partial class AppSettings
{
    [JsonPropertyName("destination")] public string Destination { get; set; } = ClaudePaths.DefaultDestination;
    [JsonPropertyName("includeSubagents")] public bool IncludeSubagents { get; set; }
    [JsonPropertyName("keepSnapshots")] public int KeepSnapshots { get; set; } = 60;
    [JsonPropertyName("darkTheme")] public bool DarkTheme { get; set; } = true;
    [JsonPropertyName("taskTime")] public string TaskTime { get; set; } = "21:00";
    [JsonPropertyName("taskAtLogon")] public bool TaskAtLogon { get; set; } = true;

    public BackupOptions ToBackupOptions(bool verify = false) => new()
    {
        Destination = Destination,
        IncludeSubagents = IncludeSubagents,
        KeepSnapshots = KeepSnapshots,
        Verify = verify,
    };
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeSessionBackup", "settings.json");

    /// <summary>Never throws: a missing or unreadable file yields defaults (the app must always start).</summary>
    public static AppSettings Load(string? path = null)
    {
        var p = path ?? DefaultPath;
        try
        {
            if (!File.Exists(p)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p), Json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        var p = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, p, overwrite: true);
    }
}
