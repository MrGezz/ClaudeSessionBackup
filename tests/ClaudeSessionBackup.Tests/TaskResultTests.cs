using ClaudeSessionBackup.Core.Model;
using ClaudeSessionBackup.Core.Scheduling;
using Xunit;

namespace ClaudeSessionBackup.Tests;

/// <summary>
/// The scheduled task's last result must be readable and failures must be flagged, and a
/// task must never be registered against an exe that does not exist. Task Scheduler shows
/// "Ready" either way and fails every trigger with 0x80070002 - which is how the first task
/// the owner registered (from a development build, 2026-09-05) failed unnoticed until
/// 2026-09-06.
/// </summary>
public class TaskResultTests
{
    [Theory]
    [InlineData(0, false, "Success")]
    [InlineData(1, false, "warnings")]
    [InlineData(2, true, "exit 2")]
    [InlineData(4, true, "REFUSED")]
    [InlineData(0x41301, false, "running")]
    [InlineData(unchecked((int)0x80070002), true, "0x80070002")]
    [InlineData(unchecked((int)0x800704DD), false, "not logged on")]
    [InlineData(unchecked((int)0xDEADBEEF), true, "0xDEADBEEF")]
    public void DescribeLastResult_flags_failures_and_names_the_common_codes(int code, bool failed, string fragment)
    {
        var (text, isFailed) = TaskSchedulerService.DescribeLastResult(code, DateTime.Now);
        Assert.Equal(failed, isFailed);
        Assert.Contains(fragment, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeLastResult_reads_never_run_when_the_task_never_ran()
    {
        var (text, isFailed) = TaskSchedulerService.DescribeLastResult(0x41303, null);
        Assert.False(isFailed);
        Assert.Equal("never run", text);
    }

    [Fact]
    public void Register_refuses_an_exe_that_does_not_exist_before_touching_the_scheduler()
    {
        var svc = new TaskSchedulerService();
        var missing = Path.Combine(Path.GetTempPath(), "csb-tests", Guid.NewGuid().ToString("N"), "ClaudeSessionBackup.Cli.exe");
        var options = new ScheduleOptions
        {
            StartAt = "21:00",
            AtLogon = false,
            Destination = Path.GetTempPath(),
            IncludeSubagents = false,
        };

        var ex = Assert.Throws<FileNotFoundException>(() => svc.Register(options, missing));
        Assert.Contains("Setup.exe", ex.Message);
    }
}