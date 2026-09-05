using System.Globalization;
using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.App.ViewModels;

/// <summary>
/// One row in the Dashboard store table. Built from a <see cref="StoreDefinition"/>,
/// then updated from a <see cref="StoreResult"/> when a run manifest is loaded or a
/// run completes. Every display property is pre-formatted so the XAML never needs a
/// multi-binding or converter for these cells.
/// </summary>
public sealed class StoreRowViewModel : ViewModelBase
{
    // ----------------------------------------------------------------- static info
    public string Name { get; }
    public string Description { get; }
    public StoreMode Mode { get; }
    public bool ShrinkGuard { get; }

    /// <summary>
    /// The source may legitimately be absent. An optional store that is SourceMissing
    /// shows a muted "absent" pill rather than the danger-tone "missing" used for
    /// non-optional stores.
    /// </summary>
    public bool Optional { get; }

    /// <summary>Tooltip text: "Tree - shrink guard on" or "Whitelist".</summary>
    public string ToolTipText =>
        $"{Mode}{(ShrinkGuard ? " – shrink guard on" : "")}";

    // ----------------------------------------------------------------- live data
    private int _liveFiles;
    public int LiveFiles { get => _liveFiles; set => Set(ref _liveFiles, value); }

    private long _liveBytes;
    public long LiveBytes { get => _liveBytes; set => Set(ref _liveBytes, value); }

    private int _backupFiles;
    public int BackupFiles { get => _backupFiles; set => Set(ref _backupFiles, value); }

    private long _backupBytes;
    public long BackupBytes { get => _backupBytes; set => Set(ref _backupBytes, value); }

    private int _copied;
    public int Copied { get => _copied; set => Set(ref _copied, value); }

    private int _unchanged;
    public int Unchanged { get => _unchanged; set => Set(ref _unchanged, value); }

    private int _failed;
    public int Failed { get => _failed; set => Set(ref _failed, value); }

    private int _heldBack;
    public int HeldBack { get => _heldBack; set => Set(ref _heldBack, value); }

    private StoreStatus? _status;
    public StoreStatus? Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsAbsent));
            }
        }
    }

    // Track whether this was populated from a verify run vs backup run,
    // so BackupText formats differently.
    private bool _wasVerify;

    // ----------------------------------------------------------- display strings

    /// <summary>"1,502 files – 1.3 GB", "-" for absent optional stores, or "–" before any run.</summary>
    public string LiveText =>
        _status is null ? "–"
        : IsAbsent ? "-"
        : $"{_liveFiles:N0} files – {FormatBytes(_liveBytes)}";

    /// <summary>
    /// Verify runs: "1,502 files – 1.3 GB".
    /// Backup runs: "+14 copied, 1,452 same" plus optional ", N failed" / ", N held back".
    /// Before any run: "–".
    /// </summary>
    public string BackupText
    {
        get
        {
            if (_status is null)
                return "–";

            if (IsAbsent)
                return "-";

            if (_wasVerify)
                return $"{_backupFiles:N0} files – {FormatBytes(_backupBytes)}";

            // Backup run: show copy summary
            var parts = new System.Collections.Generic.List<string>(4)
            {
                $"+{_copied:N0} copied",
                $"{_unchanged:N0} same",
            };
            if (_failed > 0) parts.Add($"{_failed:N0} failed");
            if (_heldBack > 0) parts.Add($"{_heldBack:N0} held back");
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Status pill text. An optional store whose source is absent shows "absent" (muted);
    /// a non-optional missing store shows "missing" (danger) - something is wrong.
    /// </summary>
    public string StatusText => _status switch
    {
        StoreStatus.Ok => "OK",
        StoreStatus.Verified => "verified",
        StoreStatus.Failed => "FAILED",
        StoreStatus.SourceMissing when Optional => "absent",
        StoreStatus.SourceMissing => "missing",
        StoreStatus.SourceEmpty => "empty",
        StoreStatus.SourceEmptyBackupHasData => "WIPED?",
        StoreStatus.Error => "ERROR",
        _ => "–",
    };

    /// <summary>True when the store is optional and currently absent - its Live/Backup cells show "-".</summary>
    public bool IsAbsent => Optional && _status == StoreStatus.SourceMissing;

    // ----------------------------------------------------------------- ctor

    public StoreRowViewModel(StoreDefinition def)
    {
        Name = def.Name;
        Description = def.Description;
        Mode = def.Mode;
        ShrinkGuard = def.ShrinkGuard;
        Optional = def.Optional;
    }

    // ----------------------------------------------------------------- update

    /// <summary>
    /// Apply the per-store result from a run manifest. Called on the UI thread.
    /// </summary>
    public void ApplyResult(StoreResult result, bool wasVerify)
    {
        _wasVerify = wasVerify;
        LiveFiles = result.LiveFiles;
        LiveBytes = result.LiveBytes;
        BackupFiles = result.BackupFiles;
        BackupBytes = result.BackupBytes;
        Copied = result.Copied;
        Unchanged = result.Unchanged;
        Failed = result.Failed;
        HeldBack = result.HeldBack;
        Status = result.Status;
        OnPropertyChanged(nameof(LiveText));
        OnPropertyChanged(nameof(BackupText));
    }

    /// <summary>Reset to the pre-run state (when destination changes).</summary>
    public void Reset()
    {
        LiveFiles = 0;
        LiveBytes = 0;
        BackupFiles = 0;
        BackupBytes = 0;
        Copied = 0;
        Unchanged = 0;
        Failed = 0;
        HeldBack = 0;
        Status = null;
        _wasVerify = false;
        OnPropertyChanged(nameof(LiveText));
        OnPropertyChanged(nameof(BackupText));
    }

    // --------------------------------------------------------- byte formatting

    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>
    /// Binary-unit byte formatter matching <see cref="Converters.BytesToHumanConverter"/>:
    /// 1024-based, because that is what Windows Explorer shows.
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double v = bytes;
        var unit = 0;
        while (v >= 1024 && unit < Units.Length - 1) { v /= 1024; unit++; }
        return unit == 0
            ? $"{v:N0} {Units[unit]}"
            : $"{v:N1} {Units[unit]}";
    }
}
