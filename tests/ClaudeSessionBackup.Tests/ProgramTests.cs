using System.Diagnostics;
using Xunit;

namespace ClaudeSessionBackup.Tests;

/// <summary>
/// CLI contract tests. Tested through the built executable because InternalsVisibleTo is set only
/// for Core, not for the Cli assembly. Tests that require the exe are skipped gracefully when the
/// exe has not been built yet.
/// </summary>
public class ProgramTests
{
    private static string? FindCliExe()
    {
        // Walk up from the test assembly's location to find the solution root, then navigate
        // to the Cli binary output. Works for both Debug and Release configurations.
        var dir = Path.GetDirectoryName(typeof(ProgramTests).Assembly.Location) ?? "";
        for (var i = 0; i < 8; i++)
        {
            var slnx = Path.Combine(dir, "ClaudeSessionBackup.slnx");
            if (File.Exists(slnx))
            {
                // Found repo root; look in both Release and Debug output.
                foreach (var cfg in new[] { "Release", "Debug" })
                {
                    var candidate = Path.Combine(dir, "src", "ClaudeSessionBackup.Cli",
                        "bin", cfg, "net8.0-windows", "ClaudeSessionBackup.Cli.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                return null;
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        return null;
    }

    private static async Task<int> RunCliAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start process: " + exe);

        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }

    // -------------------------------------------------------------------------
    // --help
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Cli_Help_ExitsZero()
    {
        // --help must print usage and exit 0.
        // Contract: ExitOk = 0
        var exe = FindCliExe();
        if (exe is null)
        {
            // Exe not yet built - this test cannot run. The integrator must build first.
            // We don't skip silently; we assert to surface the gap.
            Assert.True(exe is not null, "ClaudeSessionBackup.Cli.exe not found. Build the solution first.");
            return;
        }

        var code = await RunCliAsync(exe, "--help");
        Assert.Equal(0, code);  // ExitOk = 0
    }

    // -------------------------------------------------------------------------
    // Unknown command
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Cli_UnknownCommand_ExitsUsage()
    {
        // An unrecognised command must exit with code 3 (ExitUsage).
        // Contract: "Exit codes: 0 ok, 1 ok with warnings, 2 failures, 3 usage/config error"
        var exe = FindCliExe();
        if (exe is null)
        {
            Assert.True(exe is not null, "ClaudeSessionBackup.Cli.exe not found. Build the solution first.");
            return;
        }

        var code = await RunCliAsync(exe, "not-a-real-command");
        Assert.Equal(3, code);  // ExitUsage = 3
    }

    // -------------------------------------------------------------------------
    // No arguments
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Cli_NoArgs_ExitsUsage()
    {
        // Running with no arguments must exit with code 3 (ExitUsage).
        var exe = FindCliExe();
        if (exe is null)
        {
            Assert.True(exe is not null, "ClaudeSessionBackup.Cli.exe not found. Build the solution first.");
            return;
        }

        var code = await RunCliAsync(exe);
        Assert.Equal(3, code);  // ExitUsage = 3
    }

    // -------------------------------------------------------------------------
    // Contract gap 8: CLI exit codes 2 and 4 (staged against temp destinations)
    //
    // Exit code 1 (warnings) is pinned at the engine level in EngineTests.cs
    // because the CLI uses live Claude stores as source, which cannot be
    // substituted in the test environment.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Cli_ExitCode4_Refused_FreshLock()
    {
        // Contract: exit 4 = refused (another run holds the lock).
        // Writing a fresh .lock file to --destination must make the engine refuse
        // immediately (before any store work) and exit 4.
        var exe = FindCliExe();
        if (exe is null)
        {
            Assert.True(exe is not null, "ClaudeSessionBackup.Cli.exe not found. Build the solution first.");
            return;
        }

        var dest = Path.Combine(Path.GetTempPath(), "csb_cli4_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dest);
            // A fresh .lock (younger than 3 h) causes the engine to refuse.
            File.WriteAllText(Path.Combine(dest, ".lock"), Environment.ProcessId.ToString());

            var code = await RunCliAsync(exe, "backup",
                "--destination", dest,
                "--no-snapshot", "--no-catalog");

            Assert.Equal(4, code); // ExitRefused = 4
        }
        finally
        {
            try { Directory.Delete(dest, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Cli_ExitCode2_Failure_DestinationBlockedByFile()
    {
        // Contract: exit 2 = failures.
        // If "live" already exists as a FILE under --destination, Directory.CreateDirectory
        // throws IOException, which propagates through RunAsync to Program.Main's outer
        // catch-all → ExitFailed = 2.
        var exe = FindCliExe();
        if (exe is null)
        {
            Assert.True(exe is not null, "ClaudeSessionBackup.Cli.exe not found. Build the solution first.");
            return;
        }

        var dest = Path.Combine(Path.GetTempPath(), "csb_cli2_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dest);
            // Block the "live" sub-directory by placing a file with that name.
            File.WriteAllText(Path.Combine(dest, "live"), "blocked");

            var code = await RunCliAsync(exe, "backup",
                "--destination", dest,
                "--no-snapshot", "--no-catalog");

            Assert.Equal(2, code); // ExitFailed = 2
        }
        finally
        {
            try { Directory.Delete(dest, recursive: true); } catch { }
        }
    }
}
