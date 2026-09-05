using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Engine;

/// <summary>
/// Writes last_run.json (the manifest) and appends the one-line ledger entry to backup.log.
/// JSON is camelCase, matching the ps1 convention (snake_case keys in the ps1 get
/// serialised by ConvertTo-Json as-is; our C# contract uses PascalCase properties
/// with explicit camelCase names).
/// </summary>
internal static class ManifestWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Write the manifest to last_run.json and append the ledger line to backup.log.
    /// </summary>
    public static void Write(BackupOptions options, RunManifest manifest)
    {
        // last_run.json
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var tmpManifest = options.ManifestFile + ".tmp";
        File.WriteAllText(tmpManifest, json);
        File.Move(tmpManifest, options.ManifestFile, overwrite: true);

        // One-line ledger append to backup.log
        var snapshotNote = manifest.SnapshotPath != null
            ? $", snapshot {Path.GetFileName(manifest.SnapshotPath)}"
            : "";
        var machineTag = manifest.Machine != null ? $" @{manifest.Machine}" : "";
        var ledger = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {manifest.Mode,-6} {manifest.Stores.Count} stores, " +
                     $"{manifest.Warnings.Count} warnings, {manifest.Seconds:N0}s{snapshotNote}{machineTag}";

        File.AppendAllText(options.LedgerFile, ledger + Environment.NewLine);
    }
}
