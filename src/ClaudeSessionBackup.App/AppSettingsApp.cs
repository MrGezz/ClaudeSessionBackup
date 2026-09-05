using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeSessionBackup.App;

/// <summary>
/// App-only settings: tray behaviour. Stored beside the Core settings.json as
/// tray-settings.json. These are meaningful only to the WPF shell; the CLI
/// never reads them.
/// </summary>
/// <remarks>
/// A separate file is necessary because <see cref="ClaudeSessionBackup.Core.Config.AppSettings"/>
/// lives in the Core assembly and C# partial classes cannot span assemblies. The tray
/// settings are a UI concern anyway - keeping them out of the cross-project contract
/// means the CLI does not need to know about them.
/// </remarks>
public sealed class TraySettings
{
    /// <summary>Minimising the window hides it to the notification area instead of the taskbar.</summary>
    [JsonPropertyName("minimizeToTray")] public bool MinimizeToTray { get; set; } = true;

    /// <summary>The window Close button hides to the notification area instead of exiting.</summary>
    [JsonPropertyName("closeToTray")] public bool CloseToTray { get; set; } = true;
}

public static class TraySettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClaudeSessionBackup",
            "tray-settings.json");

    /// <summary>Never throws: a missing or unreadable file yields defaults (the app must always start).</summary>
    public static TraySettings Load(string? path = null)
    {
        var p = path ?? DefaultPath;
        try
        {
            if (!File.Exists(p)) return new TraySettings();
            return JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(p), Json) ?? new TraySettings();
        }
        catch
        {
            return new TraySettings();
        }
    }

    public static void Save(TraySettings settings, string? path = null)
    {
        var p = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, p, overwrite: true);
    }
}
