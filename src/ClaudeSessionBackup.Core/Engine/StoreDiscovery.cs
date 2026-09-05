using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Engine;

/// <summary>One uncovered root found by store discovery.</summary>
/// <param name="Path">Absolute path of the uncovered folder.</param>
/// <param name="Reason">Human-readable reason, e.g. "MSIX container data folder".</param>
/// <param name="Files">File count (capped at <see cref="StoreDiscovery.FileCap"/>).</param>
/// <param name="Bytes">Total size in bytes (based on files counted).</param>
/// <param name="NewestUtc">LastWriteTimeUtc of the newest file, or null when no files were found.</param>
public sealed record DiscoveredRoot(
    string Path,
    string Reason,
    int Files,
    long Bytes,
    DateTime? NewestUtc);

/// <summary>
/// Scans the machine for Claude data roots that are not inside any covered store.
/// The result is a list of folders that the backup does not protect; the engine logs
/// a WARN per root so the user is aware that coverage may be incomplete.
/// </summary>
/// <remarks>
/// Pure: no side effects, no writes. Never throws for I/O problems - any unreadable
/// folder or reparse point is silently skipped. File counting is capped so a huge
/// MSIX container with thousands of irrelevant files does not stall a run.
/// </remarks>
public static class StoreDiscovery
{
    /// <summary>Maximum files counted per candidate root before stopping (prevents stalls).</summary>
    public const int FileCap = 50_000;

    // Names searched up to 6 levels under each search root
    private static readonly IReadOnlyList<string> SessionFolderNames = new[]
    {
        "claude-code-sessions",
        "local-agent-mode-sessions",
        "scratch-workspaces",
    };

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Scan the real machine for uncovered Claude data roots.
    /// Candidate roots and name-search roots are derived from environment variables.
    /// </summary>
    public static IReadOnlyList<DiscoveredRoot> Discover(IReadOnlyList<StoreDefinition> covered)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Fixed candidate roots
        var candidates = new List<string>
        {
            Path.Combine(localAppData, "Claude"),
            Path.Combine(localAppData, "AnthropicClaude"),
            Path.Combine(localAppData, "Programs", "Claude"),
        };

        // MSIX package containers: %LOCALAPPDATA%\Packages\Claude_*\LocalCache\Local\Claude
        //                      and %LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude
        var packagesDir = Path.Combine(localAppData, "Packages");
        var packageRoots = FindMsixPackageRoots(packagesDir);
        candidates.AddRange(packageRoots);

        // Name-search roots: %APPDATA%, %LOCALAPPDATA%, and each Packages\Claude_* container
        var msixContainers = FindMsixContainers(packagesDir);
        var nameSearchRoots = new List<string> { appData, localAppData };
        nameSearchRoots.AddRange(msixContainers);

        return Discover(covered, candidates, nameSearchRoots);
    }

    /// <summary>
    /// Testable overload: caller supplies explicit candidate roots and name-search roots.
    /// </summary>
    /// <param name="covered">Stores already in the backup configuration.</param>
    /// <param name="candidateRoots">Fixed paths to check (e.g. %LOCALAPPDATA%\Claude).</param>
    /// <param name="nameSearchRoots">Roots to walk up to 6 levels deep looking for session folder names.</param>
    public static IReadOnlyList<DiscoveredRoot> Discover(
        IReadOnlyList<StoreDefinition> covered,
        IReadOnlyList<string> candidateRoots,
        IReadOnlyList<string> nameSearchRoots)
    {
        var results = new List<DiscoveredRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // --- Fixed candidate roots ---
        foreach (var candidate in candidateRoots)
        {
            TryAddCandidate(candidate, ReasonFor(candidate), covered, results, seen);
        }

        // --- Name-search: find session-store folders up to 6 levels deep ---
        foreach (var searchRoot in nameSearchRoots)
        {
            if (!DirectoryExistsSafe(searchRoot)) continue;
            SearchForNamedFolders(searchRoot, depth: 0, maxDepth: 6, covered, results, seen);
        }

        return results;
    }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    private static void TryAddCandidate(
        string path,
        string reason,
        IReadOnlyList<StoreDefinition> covered,
        List<DiscoveredRoot> results,
        HashSet<string> seen)
    {
        if (!DirectoryExistsSafe(path)) return;

        var norm = Normalize(path);
        if (!seen.Add(norm)) return;  // already reported

        if (IsInsideCoveredStore(norm, covered)) return;  // already protected

        // Count only what the backup does NOT already cover: a parent root whose only content is a
        // covered store (e.g. %LOCALAPPDATA%\Claude holding just the covered Logs folder) reports 0
        // and stays silent; a parent with anything else beside the covered part is still reported.
        var stats = CountFiles(path, covered);
        if (stats.Files == 0) return;  // empty, or entirely covered - not interesting

        results.Add(new DiscoveredRoot(path, reason, stats.Files, stats.Bytes, stats.NewestUtc));
    }

    private static void SearchForNamedFolders(
        string dir,
        int depth,
        int maxDepth,
        IReadOnlyList<StoreDefinition> covered,
        List<DiscoveredRoot> results,
        HashSet<string> seen)
    {
        if (depth > maxDepth) return;

        string[] entries;
        try
        {
            entries = Directory.GetDirectories(dir);
        }
        catch { return; }

        foreach (var entry in entries)
        {
            // Skip reparse points (junctions, symlinks)
            try
            {
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
            }
            catch { continue; }

            var name = System.IO.Path.GetFileName(entry);

            if (SessionFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                TryAddCandidate(entry, "session-store folder outside the covered stores", covered, results, seen);
                // Do not recurse INTO a session folder we found; its children are data
            }
            else
            {
                SearchForNamedFolders(entry, depth + 1, maxDepth, covered, results, seen);
            }
        }
    }

    /// <summary>Find %LOCALAPPDATA%\Packages\Claude_* container directories.</summary>
    private static IReadOnlyList<string> FindMsixContainers(string packagesDir)
    {
        if (!DirectoryExistsSafe(packagesDir)) return Array.Empty<string>();

        var containers = new List<string>();
        try
        {
            foreach (var dir in Directory.GetDirectories(packagesDir, "Claude_*"))
            {
                try
                {
                    var attrs = File.GetAttributes(dir);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }

                containers.Add(dir);
            }
        }
        catch { /* skip entire packages dir if unreadable */ }

        return containers;
    }

    /// <summary>
    /// Find the two data folders inside each MSIX container:
    /// LocalCache\Local\Claude and LocalCache\Roaming\Claude.
    /// </summary>
    private static IReadOnlyList<string> FindMsixPackageRoots(string packagesDir)
    {
        if (!DirectoryExistsSafe(packagesDir)) return Array.Empty<string>();

        var roots = new List<string>();
        try
        {
            foreach (var containerDir in Directory.GetDirectories(packagesDir, "Claude_*"))
            {
                try
                {
                    var attrs = File.GetAttributes(containerDir);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }

                roots.Add(Path.Combine(containerDir, "LocalCache", "Local", "Claude"));
                roots.Add(Path.Combine(containerDir, "LocalCache", "Roaming", "Claude"));
            }
        }
        catch { /* skip if unreadable */ }

        return roots;
    }

    private static string ReasonFor(string path)
    {
        // Check if path is inside a Packages\Claude_* container
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packagesDir = Path.Combine(localAppData, "Packages");
        if (path.StartsWith(packagesDir, StringComparison.OrdinalIgnoreCase))
            return "MSIX container data folder";
        return "unexpected Claude data root";
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> is equal to or a subdirectory of
    /// any covered store's Source (case-insensitive prefix match).
    /// </summary>
    private static bool IsInsideCoveredStore(string path, IReadOnlyList<StoreDefinition> covered)
    {
        foreach (var store in covered)
        {
            var storeNorm = Normalize(store.Source);

            // path == storeNorm, or path starts with storeNorm + separator
            if (string.Equals(path, storeNorm, StringComparison.OrdinalIgnoreCase))
                return true;

            if (path.StartsWith(storeNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;

            // also the reverse: the covered store's source is inside the candidate (candidate is a parent)
            // — in that case we do NOT suppress, because the parent holds more than what we cover
        }

        return false;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool DirectoryExistsSafe(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private sealed record FileScanResult(int Files, long Bytes, DateTime? NewestUtc);

    private static FileScanResult CountFiles(string root, IReadOnlyList<StoreDefinition> covered)
    {
        int count = 0;
        long bytes = 0;
        DateTime? newest = null;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            if (count >= FileCap) break;

            var dir = stack.Pop();

            // A subtree that is itself a covered store (or inside one) is already backed up:
            // neither counted nor descended into.
            string dirNorm;
            try { dirNorm = Normalize(dir); }
            catch { continue; }
            if (!string.Equals(dirNorm, Normalize(root), StringComparison.OrdinalIgnoreCase) && IsInsideCoveredStore(dirNorm, covered))
                continue;

            string[] fileEntries;
            try { fileEntries = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var filePath in fileEntries)
            {
                if (count >= FileCap) break;
                try
                {
                    var fi = new FileInfo(filePath);
                    count++;
                    bytes += fi.Length;
                    var mtime = fi.LastWriteTimeUtc;
                    if (newest is null || mtime > newest.Value)
                        newest = mtime;
                }
                catch { /* skip unreadable files */ }
            }

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var subDir in subDirs)
            {
                try
                {
                    var attrs = File.GetAttributes(subDir);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }

                stack.Push(subDir);
            }
        }

        return new FileScanResult(count, bytes, newest);
    }
}
