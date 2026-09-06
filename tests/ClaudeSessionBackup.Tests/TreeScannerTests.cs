using System.Diagnostics;
using ClaudeSessionBackup.Core.Engine;
using ClaudeSessionBackup.Core.Model;
using Xunit;

namespace ClaudeSessionBackup.Tests;

/// <summary>
/// The tree walk must never follow a junction or symlink, must not loop when a junction
/// points at an ancestor, and must report what it skipped at the right level. On
/// 2026-09-06 a real backup died on a junction the Claude harness had created inside a
/// session's subagents\workflows folder ("The path cannot be traversed because it contains
/// an untrusted mount point"); following it would also have copied that folder twice.
/// Junctions are made with mklink /J, which needs no privilege.
/// </summary>
public class TreeScannerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "csb_tree_" + Guid.NewGuid().ToString("N"));

    public TreeScannerTests()
    {
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        // Directory.Delete removes a junction itself, never its target.
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    private static bool TryJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return false;
        p.WaitForExit(10_000);
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public void Junction_inside_the_root_is_not_followed_and_reported_as_info()
    {
        var root = Path.Combine(_tmp, "root");
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "a.jsonl"), "{}");
        Assert.True(TryJunction(Path.Combine(root, "link"), real), "mklink /J failed");

        var skipped = new List<(string Path, string Reason, LogLevel Level)>();
        var files = TreeScanner.EnumerateFilesExcluding(root, Array.Empty<string>(), (p, r, l) => skipped.Add((p, r, l)))
                               .Select(f => f.FullName)
                               .ToList();

        var only = Assert.Single(files);
        Assert.EndsWith(Path.Combine("real", "a.jsonl"), only);
        var s = Assert.Single(skipped);
        Assert.EndsWith("link", s.Path);
        Assert.Equal(LogLevel.Info, s.Level);
        Assert.Contains("real path", s.Reason);
    }

    [Fact]
    public void Junction_to_an_ancestor_does_not_loop()
    {
        var root = Path.Combine(_tmp, "root2");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "x");
        Assert.True(TryJunction(Path.Combine(root, "loop"), root), "mklink /J failed");

        var stats = TreeScanner.ScanTree(root, Array.Empty<string>());

        Assert.Equal(1, stats.Files);
    }

    [Fact]
    public void Junction_outside_the_root_is_a_warning_and_its_files_are_not_counted()
    {
        var root = Path.Combine(_tmp, "root3");
        var elsewhere = Path.Combine(_tmp, "elsewhere");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "foreign.txt"), "x");
        Assert.True(TryJunction(Path.Combine(root, "link"), elsewhere), "mklink /J failed");

        var levels = new List<LogLevel>();
        var stats = TreeScanner.ScanTree(root, Array.Empty<string>(), (_, _, l) => levels.Add(l));

        Assert.Equal(0, stats.Files);
        Assert.Equal(new[] { LogLevel.Warn }, levels);
    }

    [Fact]
    public void Copy_does_not_duplicate_a_junctioned_folder()
    {
        var root = Path.Combine(_tmp, "src");
        var real = Path.Combine(root, "real");
        var dest = Path.Combine(_tmp, "dest");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "a.jsonl"), "{}");
        Assert.True(TryJunction(Path.Combine(root, "link"), real), "mklink /J failed");

        var result = StoreCopier.CopyTree(root, dest, Array.Empty<string>(), Array.Empty<string>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None, null);

        Assert.Equal(1, result.Copied);
        Assert.True(File.Exists(Path.Combine(dest, "real", "a.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(dest, "link")));
    }
}
