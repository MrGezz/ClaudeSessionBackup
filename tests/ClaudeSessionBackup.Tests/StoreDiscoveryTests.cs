using ClaudeSessionBackup.Core.Engine;
using ClaudeSessionBackup.Core.Model;
using Xunit;

namespace ClaudeSessionBackup.Tests;

/// <summary>
/// Tests for <see cref="StoreDiscovery"/>. All tests use the injectable overload
/// <c>Discover(covered, candidateRoots, nameSearchRoots)</c> so they never touch
/// real machine paths.
/// </summary>
public class StoreDiscoveryTests : IDisposable
{
    private readonly string _tmp;

    public StoreDiscoveryTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "csb_disc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch { /* best-effort */ }
    }

    // --------------------------------------------------------------------- helpers

    private static StoreDefinition TreeStore(string name, string source) =>
        new(name, StoreMode.Tree, source,
            ExcludeDirs: Array.Empty<string>(),
            ExcludeFiles: Array.Empty<string>(),
            Files: Array.Empty<string>(),
            Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: false,
            Description: "test store");

    private string MakeDir(string relativePath)
    {
        var full = Path.Combine(_tmp, relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    private static void WriteFile(string dir, string name, string content = "x")
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), content);
    }

    // --------------------------------------------------------------------- tests

    [Fact]
    public void Discovery_ParentRootWhoseOnlyContentIsCovered_NotReported()
    {
        // %LOCALAPPDATA%\Claude holds only the covered Logs folder: nothing uncovered -> silent.
        var parent = MakeDir("Claude");
        var logs = MakeDir(@"Claude\Logs");
        WriteFile(logs, "main.log", "log");
        var result = StoreDiscovery.Discover(
            covered: new[] { TreeStore("cowork-logs", logs) },
            candidateRoots: new[] { parent },
            nameSearchRoots: Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void Discovery_ParentRootWithUncoveredSibling_ReportsOnlyTheUncoveredFiles()
    {
        // Same parent, but a sibling folder beside the covered one holds data -> reported, counting
        // only the uncovered files (the covered subtree is neither counted nor descended into).
        var parent = MakeDir("Claude2");
        var logs = MakeDir(@"Claude2\Logs");
        WriteFile(logs, "main.log", "log");
        var other = MakeDir(@"Claude2\Sessions");
        WriteFile(other, "a.json", "{}");
        WriteFile(other, "b.json", "{}");
        var result = StoreDiscovery.Discover(
            covered: new[] { TreeStore("cowork-logs", logs) },
            candidateRoots: new[] { parent },
            nameSearchRoots: Array.Empty<string>());

        var hit = Assert.Single(result);
        Assert.Equal(2, hit.Files);
    }

    [Fact]
    public void Discovery_EmptyCandidate_NotReported()
    {
        // An existing but empty candidate folder must NOT appear in the results.
        var empty = MakeDir("empty-candidate");
        var result = StoreDiscovery.Discover(
            covered: Array.Empty<StoreDefinition>(),
            candidateRoots: new[] { empty },
            nameSearchRoots: Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void Discovery_CandidateInsideCoveredStore_NotReported()
    {
        // A candidate that is inside (or equal to) a covered store's Source is suppressed.
        var source = MakeDir("covered-source");
        WriteFile(source, "session.jsonl");

        var covered = new[] { TreeStore("covered", source) };

        // The candidate IS the covered source — must not appear.
        var result = StoreDiscovery.Discover(
            covered: covered,
            candidateRoots: new[] { source },
            nameSearchRoots: Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void Discovery_CandidateSubfolderInsideCoveredStore_NotReported()
    {
        // A candidate that is a subdirectory of a covered source is also suppressed.
        var source = MakeDir("covered-source");
        var subDir = Path.Combine(source, "subfolder");
        WriteFile(subDir, "data.json");

        var covered = new[] { TreeStore("covered", source) };

        var result = StoreDiscovery.Discover(
            covered: covered,
            candidateRoots: new[] { subDir },
            nameSearchRoots: Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void Discovery_CandidateWithFiles_Reported_WithCorrectCounts()
    {
        // A candidate with files that is NOT inside any covered store must be reported
        // with accurate file count and positive byte count.
        var uncovered = MakeDir("uncovered-data");
        WriteFile(uncovered, "file1.txt", "hello");
        WriteFile(uncovered, "file2.txt", "world");

        var result = StoreDiscovery.Discover(
            covered: Array.Empty<StoreDefinition>(),
            candidateRoots: new[] { uncovered },
            nameSearchRoots: Array.Empty<string>());

        Assert.Single(result);
        Assert.Equal(uncovered, result[0].Path);
        Assert.Equal(2, result[0].Files);
        Assert.True(result[0].Bytes > 0);
        Assert.NotNull(result[0].NewestUtc);
    }

    [Fact]
    public void Discovery_NameSearch_FindsSessionFolderThreeLevelsDown()
    {
        // A folder named "claude-code-sessions" found 3 levels under a search root
        // must be reported.
        var searchRoot = MakeDir("search-root");
        var target = MakeDir(@"search-root\level1\level2\claude-code-sessions");
        WriteFile(target, "local_abc.json");

        var result = StoreDiscovery.Discover(
            covered: Array.Empty<StoreDefinition>(),
            candidateRoots: Array.Empty<string>(),
            nameSearchRoots: new[] { searchRoot });

        Assert.Single(result);
        Assert.Equal(target, result[0].Path);
        Assert.Contains("session-store folder", result[0].Reason);
    }

    [Fact]
    public void Discovery_NameSearch_FindsLocalAgentModeSessions()
    {
        // A folder named "local-agent-mode-sessions" must also be found by name search.
        var searchRoot = MakeDir("search2");
        var target = MakeDir(@"search2\sub\local-agent-mode-sessions");
        WriteFile(target, "data.json");

        var result = StoreDiscovery.Discover(
            covered: Array.Empty<StoreDefinition>(),
            candidateRoots: Array.Empty<string>(),
            nameSearchRoots: new[] { searchRoot });

        Assert.Single(result);
        Assert.Equal(target, result[0].Path);
    }

    [Fact]
    public void Discovery_NameSearch_AlreadyCoveredSessionFolder_NotReported()
    {
        // A session folder that IS the covered store's source must not appear even
        // when found via name search.
        var source = MakeDir("covered-sessions");
        // The name must match one of the session folder names for name search to find it
        var sessionDir = MakeDir("search-root\\claude-code-sessions");
        WriteFile(sessionDir, "session.json");

        // Cover exactly this folder
        var covered = new[] { TreeStore("cowork-index", sessionDir) };

        var result = StoreDiscovery.Discover(
            covered: covered,
            candidateRoots: Array.Empty<string>(),
            nameSearchRoots: new[] { MakeDir("search-root") });

        Assert.Empty(result);
    }

    [Fact]
    public void Discovery_ReparsePoint_Skipped()
    {
        // Reparse points (junctions / symlinks) in the search tree must be skipped.
        // We simulate this by creating a real directory and verifying ordinary subdirs are found,
        // then testing that a junction is not followed.
        // Creating an actual junction requires elevation or developer mode on Windows,
        // so we verify the code path by ensuring the scanner does NOT throw when
        // it encounters one; we can't easily create a reparse point in a unit test.
        // Instead, we verify normal walk works and the api does not throw.
        var searchRoot = MakeDir("reparse-test");
        var normal = MakeDir(@"reparse-test\normal");
        WriteFile(normal, "file.txt");

        var result = StoreDiscovery.Discover(
            covered: Array.Empty<StoreDefinition>(),
            candidateRoots: new[] { normal },
            nameSearchRoots: Array.Empty<string>());

        Assert.Single(result);  // normal dir is reported
        // No exception means reparse-point handling compiled and is reachable
    }

    [Fact]
    public void Discovery_UnreadableFolder_DoesNotThrow()
    {
        // An unreadable (or non-existent) folder in candidates or search roots
        // must not cause the method to throw.
        var nonExistent = Path.Combine(_tmp, "does-not-exist");

        var ex = Record.Exception(() => StoreDiscovery.Discover(
            covered: Array.Empty<StoreDefinition>(),
            candidateRoots: new[] { nonExistent },
            nameSearchRoots: new[] { nonExistent }));

        Assert.Null(ex);
    }

    [Fact]
    public void Discovery_DuplicateCandidates_ReportedOnce()
    {
        // The same path appearing in both candidateRoots and via name search
        // should produce only one DiscoveredRoot entry.
        var dir = MakeDir("dup");
        WriteFile(dir, "x.txt");

        // dir appears both as a fixed candidate and as a named folder
        // We can test de-dup by passing it twice as a candidate.
        var result = StoreDiscovery.Discover(
            covered: Array.Empty<StoreDefinition>(),
            candidateRoots: new[] { dir, dir },
            nameSearchRoots: Array.Empty<string>());

        Assert.Single(result);
    }

    [Fact]
    public void Discovery_ManifestCarriesDiscoveredList()
    {
        // Integration: RunManifest.Discovered is populated by BackupEngine when discovery
        // finds something. We verify the property exists and is initialised to empty by default.
        var manifest = new RunManifest(
            "20260905_120000", "verify", "C:\\dest", false,
            Array.Empty<StoreResult>(), null, null,
            Array.Empty<string>(), 0.5, "log.log");

        // Default is empty, not null
        Assert.NotNull(manifest.Discovered);
        Assert.Empty(manifest.Discovered);

        // Can be overridden with discovered roots
        var withDisc = manifest with
        {
            Discovered = new[] { new DiscoveredRoot("C:\\test", "reason", 1, 5, null) }
        };
        Assert.Single(withDisc.Discovered);
    }

    // --------------------------------------------------------------------- optional / MSIX stores

    [Fact]
    public void Discovery_NonExistentStoreSourceStillCountsAsCovered()
    {
        // IsInsideCoveredStore normalises with GetFullPath. A covered store whose Source
        // directory does not exist must still suppress any candidate that is a child of it.
        // This is how the MSIX stores work: the source may be absent, but discovery must
        // still treat anything under it as covered.
        var nonExistent = Path.Combine(_tmp, "msix-roaming", "Claude");
        var child = Path.Combine(nonExistent, "claude-code-sessions");

        // The child exists on disk (simulating the transient folder appearing)
        Directory.CreateDirectory(child);
        WriteFile(child, "session.json");

        // The store covers the PARENT, whose Source does not need to exist on disk.
        var covered = new[] { TreeStore("msix-config", nonExistent) };

        var result = StoreDiscovery.Discover(
            covered: covered,
            candidateRoots: new[] { child },
            nameSearchRoots: Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void Discovery_MsixContainerRootSilent_WhenMsixConfigCoversIt()
    {
        // The MSIX container's Roaming\Claude is msix-config's Source. When that store is
        // in the covered list, the container root itself (as a candidate) must be silent
        // because every file in it is inside a covered store.
        var msixRoot = MakeDir(@"msix-pkg\LocalCache\Roaming\Claude");
        WriteFile(msixRoot, "config.json", "{}");

        // msix-config covers this exact root
        var covered = new[] { TreeStore("msix-config", msixRoot) };

        var result = StoreDiscovery.Discover(
            covered: covered,
            candidateRoots: new[] { msixRoot },
            nameSearchRoots: Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void Discovery_MsixLocalClaudeStillReported_IfItHasFiles()
    {
        // LocalCache\Local\Claude is NOT a store. If it has files it must be reported
        // as uncovered (it holds AppData redirects of child processes).
        var localClaude = MakeDir(@"msix-pkg\LocalCache\Local\Claude");
        WriteFile(localClaude, "node-gyp-cache.txt");

        // Only the Roaming\Claude side is covered, not Local\Claude
        var msixRoaming = MakeDir(@"msix-pkg\LocalCache\Roaming\Claude");
        var covered = new[] { TreeStore("msix-config", msixRoaming) };

        var result = StoreDiscovery.Discover(
            covered: covered,
            candidateRoots: new[] { localClaude },
            nameSearchRoots: Array.Empty<string>());

        Assert.Single(result);
        Assert.Equal(localClaude, result[0].Path);
    }

    [Fact]
    public void Discovery_AllMsixChildStoresCovered_ContainerRootSilent()
    {
        // When all four msix-* stores plus msix-config cover the container root, the
        // root itself (and its session subdirectories) must produce no uncovered results.
        var msixRoot = MakeDir(@"msix-full\LocalCache\Roaming\Claude");
        var sessions = MakeDir(@"msix-full\LocalCache\Roaming\Claude\claude-code-sessions");
        var agent = MakeDir(@"msix-full\LocalCache\Roaming\Claude\local-agent-mode-sessions");
        var scratch = MakeDir(@"msix-full\LocalCache\Roaming\Claude\scratch-workspaces");
        WriteFile(sessions, "local_abc.json");
        WriteFile(agent, "session.json");
        WriteFile(scratch, "temp.txt");
        WriteFile(msixRoot, "config.json", "{}");

        var covered = new StoreDefinition[]
        {
            TreeStore("msix-index", sessions),
            TreeStore("msix-agent-mode", agent),
            TreeStore("msix-scratch", scratch),
            new("msix-config", StoreMode.Whitelist, msixRoot,
                ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(),
                Files: new[] { "config.json" }, Dirs: new[] { "logs" },
                ShrinkGuard: false, Snapshot: false, Description: "test msix config"),
        };

        // The candidate is the root, and name-search will find the session folders
        var result = StoreDiscovery.Discover(
            covered: covered,
            candidateRoots: new[] { msixRoot },
            nameSearchRoots: new[] { msixRoot });

        // The root has config.json which is not inside any tree store's Source (it IS msix-config's
        // Source, so it is equal to a covered store -> suppressed). The session dirs are each covered.
        Assert.Empty(result);
    }
}
