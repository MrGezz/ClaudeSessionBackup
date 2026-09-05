using ClaudeSessionBackup.Core.Engine;

namespace ClaudeSessionBackup.Core.Model;

/// <summary>
/// Partial extension of <see cref="RunManifest"/> that carries the store-discovery results.
/// Added in the "store discovery" feature (2026-09-05): uncovered Claude data roots found
/// on this machine that the backup does not protect.
/// </summary>
public sealed partial record RunManifest
{
    /// <summary>
    /// Claude data roots found on this machine that are not inside any covered store's Source.
    /// Empty when discovery found nothing uncovered (the expected steady state).
    /// Written to last_run.json so the app and the CLI can surface the list on the next start.
    /// </summary>
    public IReadOnlyList<DiscoveredRoot> Discovered { get; init; } = Array.Empty<DiscoveredRoot>();
}
