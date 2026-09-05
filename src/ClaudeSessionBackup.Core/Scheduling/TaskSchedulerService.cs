using Microsoft.Win32.TaskScheduler;
using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Scheduling;

/// <summary>
/// Windows Task Scheduler facade (TaskScheduler NuGet package, root folder, interactive user, least privilege).
/// </summary>
/// <remarks>
/// Contract: <c>Register</c> creates or replaces a task named <see cref="ScheduleOptions.TaskName"/> whose action is
/// <c>"&lt;cliExecutablePath&gt;" backup --destination "&lt;Destination&gt;" --quiet [--include-subagents]</c> with the CLI's folder as
/// working directory; triggers: daily at StartAt (HH:mm, validated - throw ArgumentException on a bad value rather than
/// registering a midnight task) and, when AtLogon, a logon trigger for the current user delayed by LogonDelay.
/// Principal: current user, InteractiveToken, RunLevel LUA. Settings: StartWhenAvailable, ExecutionTimeLimit 2 h,
/// MultipleInstances IgnoreNew, allowed on batteries. <c>Query</c> never throws (returns Exists=false on any failure);
/// <c>Unregister</c> is a no-op when the task is absent.
/// </remarks>
public interface ITaskSchedulerService
{
    ScheduledTaskInfo Query(string taskName);
    void Register(ScheduleOptions options, string cliExecutablePath);
    void Unregister(string taskName);
}

/// <summary>
/// Thin façade over the Windows Task Scheduler COM API (via the TaskScheduler NuGet package).
/// All public methods operate in the root folder ("\") of the local Task Scheduler service.
/// </summary>
/// <remarks>
/// The methods are intentionally synchronous: Task Scheduler COM is apartment-threaded and the
/// operations complete in milliseconds. Wrapping them in Task.Run would add noise without benefit.
/// </remarks>
public sealed partial class TaskSchedulerService : ITaskSchedulerService
{
    // ──────────────────────────────────────────────────────────────────────────────
    // Public API
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a snapshot of the named task's scheduling state.
    /// Never throws: COM unavailable or task absent → Exists=false.
    /// The GUI polls this on a timer; a transient COM failure must not crash the UI.
    /// </summary>
    public ScheduledTaskInfo Query(string taskName)
    {
        try
        {
            using var ts = new TaskService();
            var task = ts.FindTask(taskName, false);
            if (task is null)
                return new ScheduledTaskInfo(false, null, null, null, null, null);

            DateTime? nextRun = task.NextRunTime == DateTime.MinValue ? null : task.NextRunTime;
            DateTime? lastRun = task.LastRunTime == DateTime.MinValue ? null : task.LastRunTime;

            // Format the last result code in a human-readable way
            string? lastResult = task.LastTaskResult == 0
                ? "Success (0)"
                : $"0x{task.LastTaskResult:X8}";

            string? state = task.State.ToString();

            // Extract the action command string for display
            string? action = null;
            var execAction = task.Definition.Actions.OfType<ExecAction>().FirstOrDefault();
            if (execAction is not null)
                action = $"{execAction.Path} {execAction.Arguments}".Trim();

            return new ScheduledTaskInfo(true, nextRun, lastRun, lastResult, state, action);
        }
        catch
        {
            // Task Scheduler service unreachable, COM error, or access denied.
            // Return a safe "absent" value rather than surfacing COM exceptions to the UI.
            return new ScheduledTaskInfo(false, null, null, null, null, null);
        }
    }

    /// <summary>
    /// Creates (or replaces) a Windows Task Scheduler task that runs the backup CLI
    /// on the schedule described by <paramref name="options"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why InteractiveToken / LUA?</strong><br/>
    /// Claude session stores live inside the interactive user's profile. A task that
    /// runs without an interactive session (S4U logon) could silently miss cloud-only
    /// placeholder files or encounter locked files, producing a partial backup without
    /// any error. InteractiveToken ensures the task runs in exactly the same context
    /// as the user and sees the same file system state.
    /// </para>
    /// <para>
    /// RunLevel.LUA (Least Privilege) rather than Highest: copying session files to the
    /// backup destination requires no elevation, and demanding it would pop a UAC prompt
    /// or fail silently on non-administrator accounts.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="ScheduleOptions.StartAt"/> is not parseable as "HH:mm".
    /// Failing fast here is preferable to silently registering a task that fires at midnight.
    /// </exception>
    public void Register(ScheduleOptions options, string cliExecutablePath)
    {
        // Validate StartAt before touching the scheduler at all
        if (!TimeOnly.TryParseExact(options.StartAt, "HH:mm", out var startTime))
        {
            throw new ArgumentException(
                $"ScheduleOptions.StartAt value \"{options.StartAt}\" is not a valid HH:mm time. " +
                "Fix the value before registering the task.",
                nameof(options));
        }

        string workingDir = Path.GetDirectoryName(cliExecutablePath)
            ?? throw new ArgumentException(
                $"Cannot determine working directory from path \"{cliExecutablePath}\".",
                nameof(cliExecutablePath));

        // Build the CLI arguments: backup --destination "<dest>" --quiet [--include-subagents]
        var args = $"backup --destination \"{options.Destination}\" --quiet";
        if (options.IncludeSubagents)
            args += " --include-subagents";

        using var ts = new TaskService();
        using TaskDefinition td = ts.NewTask();

        // ── Description ───────────────────────────────────────────────────────────
        td.RegistrationInfo.Description =
            "Daily backup of Claude Code and Cowork session stores (transcripts, sidebar index, config). " +
            "Runs only while the owning user is interactively logged on.";

        // ── Principal ─────────────────────────────────────────────────────────────
        //
        // InteractiveToken: task runs in the user's interactive session so it sees
        // the same file system as Claude (important for symlinks, junctions, OneDrive
        // cloud-only placeholders if any session files end up there).
        // LUA: least privilege, no elevation pop-up.
        td.Principal.UserId    = $@"{Environment.UserDomainName}\{Environment.UserName}";
        td.Principal.LogonType = TaskLogonType.InteractiveToken;
        td.Principal.RunLevel  = TaskRunLevel.LUA;

        // ── Settings ──────────────────────────────────────────────────────────────
        //
        // StartWhenAvailable: catches up on missed daily triggers (e.g. machine was
        //   asleep or the user was not logged on at 21:00).
        // IgnoreNew: a slow backup run won't spawn a second overlapping copy.
        // ExecutionTimeLimit 2 h: a stuck or very large backup doesn't block forever.
        // Allow batteries: laptop users shouldn't miss their backup just because they
        //   unplugged; session data is small compared to a full sync job.
        td.Settings.DisallowStartIfOnBatteries = false;
        td.Settings.StopIfGoingOnBatteries     = false;
        td.Settings.StartWhenAvailable         = true;
        td.Settings.MultipleInstances          = TaskInstancesPolicy.IgnoreNew;
        td.Settings.ExecutionTimeLimit         = TimeSpan.FromHours(2);

        // ── Daily trigger ─────────────────────────────────────────────────────────
        var today        = DateTime.Today;
        var triggerStart = new DateTime(today.Year, today.Month, today.Day, startTime.Hour, startTime.Minute, 0);

        td.Triggers.Add(new DailyTrigger { StartBoundary = triggerStart });

        // ── Optional logon trigger ────────────────────────────────────────────────
        //
        // Catches the case where the machine was not on at the daily trigger time and
        // the user logs on later that day. The delay avoids hammering the disk during
        // login, when many other startup tasks compete for I/O.
        if (options.AtLogon)
        {
            td.Triggers.Add(new LogonTrigger
            {
                UserId = td.Principal.UserId,
                Delay  = options.LogonDelay,
            });
        }

        // ── Action ────────────────────────────────────────────────────────────────
        td.Actions.Add(new ExecAction(
            path:             $"\"{cliExecutablePath}\"",
            arguments:        args,
            workingDirectory: workingDir));

        // Replace any existing task of the same name (idempotent registration)
        ts.RootFolder.RegisterTaskDefinition(
            path:       options.TaskName,
            definition: td,
            createType: TaskCreation.CreateOrUpdate,
            userId:     null,
            password:   null,
            logonType:  TaskLogonType.InteractiveToken);
    }

    /// <summary>
    /// Removes the named task from the root Task Scheduler folder.
    /// No-ops silently if the task does not exist.
    /// </summary>
    public void Unregister(string taskName)
    {
        try
        {
            using var ts = new TaskService();
            var task = ts.FindTask(taskName, false);
            if (task is not null)
                ts.RootFolder.DeleteTask(taskName, exceptionOnNotExists: false);
        }
        catch
        {
            // Swallow COM errors on unregister - if the task isn't there, we don't care
        }
    }
}
