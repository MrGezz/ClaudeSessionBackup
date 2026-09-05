using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClaudeSessionBackup.App.Services;
using ClaudeSessionBackup.Core.Config;
using ClaudeSessionBackup.Core.Model;
using ClaudeSessionBackup.Core.Transcripts;

namespace ClaudeSessionBackup.App.ViewModels;

/// <summary>
/// View model for a single transcript block (text, thinking, tool call, etc.)
/// inside a <see cref="TurnViewModel"/>. Owns the lazily-built FlowDocument
/// for Markdown text blocks, the decoded image, and the expand/collapse state.
/// </summary>
public sealed class BlockViewModel : ViewModelBase
{
    private readonly TranscriptBlock _block;
    private readonly MarkdownRenderer _renderer;

    public BlockViewModel(TranscriptBlock block, MarkdownRenderer renderer)
    {
        _block = block;
        _renderer = renderer;
    }

    public TranscriptBlockKind Kind => _block.Kind;
    public string? Text => _block.Text;
    public bool ThinkingRedacted => _block.ThinkingRedacted;
    public string? ToolName => _block.ToolName;
    public string? ToolUseId => _block.ToolUseId;
    public string? InputJson => _block.InputJson;
    public bool IsError => _block.IsError;
    public string? Subtype => _block.Subtype;
    public int ImageByteLength => _block.ImageByteLength;

    /// <summary>First two lines of tool input JSON for the collapsed preview.</summary>
    public string ToolInputPreview
    {
        get
        {
            if (string.IsNullOrEmpty(InputJson)) return "";
            var lines = InputJson.Split('\n');
            return lines.Length <= 2 ? InputJson : string.Join('\n', lines[0], lines[1]);
        }
    }

    /// <summary>Whether long tool results have been expanded to full.</summary>
    private bool _showAllToolResult;
    public bool ShowAllToolResult
    {
        get => _showAllToolResult;
        set => Set(ref _showAllToolResult, value);
    }

    /// <summary>Capped tool result text (~200 lines).</summary>
    public string? ToolResultCapped
    {
        get
        {
            if (Kind != TranscriptBlockKind.ToolResult || Text is null) return Text;
            var lines = Text.Split('\n');
            if (lines.Length <= 200) return Text;
            return string.Join('\n', lines.Take(200)) + $"\n... ({lines.Length - 200} more lines)";
        }
    }

    public string? ToolResultFull => Text;

    /// <summary>True when the tool result was truncated and can be expanded.</summary>
    public bool IsToolResultCapped => ToolResultCapped != ToolResultFull;

    /// <summary>Decoded BitmapImage from the block's ImageBytes, cached on first access.</summary>
    private BitmapImage? _image;
    private bool _imageResolved;
    public BitmapImage? Image
    {
        get
        {
            if (!_imageResolved)
            {
                _imageResolved = true;
                if (_block.ImageBytes is { Length: > 0 } bytes)
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = Math.Min(1200, int.MaxValue);
                        bmp.StreamSource = new MemoryStream(bytes);
                        bmp.EndInit();
                        bmp.Freeze();
                        _image = bmp;
                    }
                    catch
                    {
                        // Corrupt image data: show the placeholder.
                    }
                }
            }
            return _image;
        }
    }

    public bool HasImageBytes => _block.ImageBytes is { Length: > 0 };

    /// <summary>Lazily-built FlowDocument for Markdown text blocks.</summary>
    private System.Windows.Documents.FlowDocument? _flowDoc;
    private bool _flowDocBuilt;
    public System.Windows.Documents.FlowDocument? FlowDocument
    {
        get
        {
            if (!_flowDocBuilt)
            {
                _flowDocBuilt = true;
                if (Kind == TranscriptBlockKind.Text && !string.IsNullOrEmpty(Text))
                {
                    _flowDoc = _renderer.Render(Text);
                }
            }
            return _flowDoc;
        }
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>Whether this block should be shown per the toolbar toggles (ShowThinking, ShowToolCalls).</summary>
    private bool _isBlockVisible = true;
    public bool IsBlockVisible
    {
        get => _isBlockVisible;
        set => Set(ref _isBlockVisible, value);
    }
}

/// <summary>
/// View model for one conversation turn. Groups blocks by role and provides
/// the display properties (role label, timestamp, accent).
/// </summary>
public sealed class TurnViewModel : ViewModelBase
{
    private readonly TranscriptTurn _turn;

    public TurnViewModel(TranscriptTurn turn, MarkdownRenderer renderer)
    {
        _turn = turn;
        foreach (var block in turn.Blocks)
        {
            var bvm = new BlockViewModel(block, renderer);
            // Thinking blocks open by default, tool calls/results collapsed
            if (block.Kind == TranscriptBlockKind.Thinking)
                bvm.IsExpanded = true;

            Blocks.Add(bvm);
        }
    }

    public int Index => _turn.Index;
    public TranscriptRole Role => _turn.Role;
    public DateTime? Timestamp => _turn.Timestamp;
    public string? Model => _turn.Model;
    public bool IsSidechain => _turn.IsSidechain;
    public bool IsToolResultOnly => _turn.IsToolResultOnly;

    /// <summary>Local-time display string for the turn timestamp.</summary>
    public string TimestampDisplay =>
        _turn.Timestamp?.ToLocalTime().ToString("HH:mm:ss") ?? "";

    public string RoleLabel => Role switch
    {
        TranscriptRole.User when IsToolResultOnly => "Tool Results",
        TranscriptRole.User => "User",
        TranscriptRole.Assistant => "Assistant",
        TranscriptRole.System => "System",
        _ => Role.ToString(),
    };

    public ObservableCollection<BlockViewModel> Blocks { get; } = new();

    private bool _isHighlighted;
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => Set(ref _isHighlighted, value);
    }
}

/// <summary>
/// The Transcript page's main view model. Loads a transcript via
/// <see cref="ITranscriptReader"/>, builds turn view models, and drives the
/// toolbar toggles, search, and export actions.
/// </summary>
public sealed class TranscriptViewModel : ViewModelBase
{
    private readonly ITranscriptReader _reader;
    private readonly ITranscriptExporter _exporter;
    private readonly MarkdownRenderer _renderer;
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;

    private Transcript? _transcript;
    private CancellationTokenSource? _loadCts;

    public TranscriptViewModel(
        ITranscriptReader reader,
        ITranscriptExporter exporter,
        MarkdownRenderer renderer,
        AppSettings settings)
    {
        _reader = reader;
        _exporter = exporter;
        _renderer = renderer;
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;

        CloseCommand = new RelayCommand(Close);
        CopyMarkdownCommand = new RelayCommand(CopyMarkdown, () => _transcript is not null);
        ExportMarkdownCommand = new AsyncRelayCommand(ExportMarkdownAsync, () => _transcript is not null);
        ExportHtmlCommand = new AsyncRelayCommand(ExportHtmlAsync, () => _transcript is not null);
        SearchNextCommand = new RelayCommand(SearchNext);
        SearchPrevCommand = new RelayCommand(SearchPrev);
        JumpToTurnCommand = new RelayCommand(JumpToTurn);
    }

    // ------------------------------------------------------------- state

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => Set(ref _isLoading, value);
    }

    private bool _isLoaded;
    public bool IsLoaded
    {
        get => _isLoaded;
        set => Set(ref _isLoaded, value);
    }

    private string _loadStatus = "";
    public string LoadStatus
    {
        get => _loadStatus;
        set => Set(ref _loadStatus, value);
    }

    private string _errorMessage = "";
    public string ErrorMessage
    {
        get => _errorMessage;
        set => Set(ref _errorMessage, value);
    }

    // ----------------------------------------------------------- header

    private string _title = "";
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    private string _project = "";
    public string Project
    {
        get => _project;
        set => Set(ref _project, value);
    }

    private string _dateRange = "";
    public string DateRange
    {
        get => _dateRange;
        set => Set(ref _dateRange, value);
    }

    private string _stats = "";
    public string Stats
    {
        get => _stats;
        set => Set(ref _stats, value);
    }

    private string _sourcePath = "";
    public string SourcePath
    {
        get => _sourcePath;
        set => Set(ref _sourcePath, value);
    }

    private bool _useBackupSource = true;
    public bool UseBackupSource
    {
        get => _useBackupSource;
        set
        {
            if (Set(ref _useBackupSource, value) && _currentEntry is not null)
            {
                _ = LoadTranscriptAsync(_currentEntry);
            }
        }
    }

    private bool _backupExists;
    public bool BackupExists
    {
        get => _backupExists;
        set => Set(ref _backupExists, value);
    }

    private bool _liveExists;
    public bool LiveExists
    {
        get => _liveExists;
        set => Set(ref _liveExists, value);
    }

    // ---------------------------------------------------------- toggles

    private bool _showThinking = true;
    public bool ShowThinking
    {
        get => _showThinking;
        set
        {
            if (Set(ref _showThinking, value)) RebuildVisibleTurns();
        }
    }

    private bool _showToolCalls = true;
    public bool ShowToolCalls
    {
        get => _showToolCalls;
        set
        {
            if (Set(ref _showToolCalls, value)) RebuildVisibleTurns();
        }
    }

    private bool _showToolResults = true;
    public bool ShowToolResults
    {
        get => _showToolResults;
        set
        {
            if (Set(ref _showToolResults, value)) RebuildVisibleTurns();
        }
    }

    private bool _showSystem = true;
    public bool ShowSystem
    {
        get => _showSystem;
        set
        {
            if (Set(ref _showSystem, value)) RebuildVisibleTurns();
        }
    }

    private bool _showAttachments;
    public bool ShowAttachments
    {
        get => _showAttachments;
        set
        {
            if (Set(ref _showAttachments, value) && _currentEntry is not null)
            {
                // Attachments require a reload with IncludeAttachments
                _ = LoadTranscriptAsync(_currentEntry);
            }
        }
    }

    // ----------------------------------------------------------- search

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) ApplySearch();
        }
    }

    private string _searchResultText = "";
    public string SearchResultText
    {
        get => _searchResultText;
        set => Set(ref _searchResultText, value);
    }

    private int _jumpTurnNumber;
    public int JumpTurnNumber
    {
        get => _jumpTurnNumber;
        set => Set(ref _jumpTurnNumber, value);
    }

    // ---------------------------------------------------------- turns

    /// <summary>All turns from the loaded transcript.</summary>
    private List<TurnViewModel> _allTurns = new();

    /// <summary>The filtered set of turns currently visible.</summary>
    public ObservableCollection<TurnViewModel> VisibleTurns { get; } = new();

    /// <summary>Search match indices into VisibleTurns.</summary>
    private List<int> _searchHits = new();
    private int _searchHitIndex = -1;

    /// <summary>Fires when the view should scroll a turn into view.</summary>
    public event Action<int>? ScrollToTurnRequested;

    // --------------------------------------------------------- commands

    public ICommand CloseCommand { get; }
    public ICommand CopyMarkdownCommand { get; }
    public ICommand ExportMarkdownCommand { get; }
    public ICommand ExportHtmlCommand { get; }
    public ICommand SearchNextCommand { get; }
    public ICommand SearchPrevCommand { get; }
    public ICommand JumpToTurnCommand { get; }

    // ---------------------------------------------------------- opening

    private SessionEntry? _currentEntry;

    /// <summary>
    /// Resolves the transcript file path for the given session, preferring
    /// backup over live per the user's source switch.
    /// </summary>
    private (string? path, bool isBackup) ResolveSource(SessionEntry entry)
    {
        string? backupPath = null;
        string? livePath = null;

        if (entry.TranscriptRel is not null)
        {
            var rel = entry.TranscriptRel.Replace('/', '\\');
            var backupBase = Path.Combine(_settings.Destination, "live", "code-transcripts");
            backupPath = Path.Combine(backupBase, rel);
            livePath = Path.Combine(ClaudePaths.Projects, rel);
        }

        BackupExists = backupPath is not null && File.Exists(backupPath);
        LiveExists = livePath is not null && File.Exists(livePath);

        if (UseBackupSource && BackupExists) return (backupPath, true);
        if (!UseBackupSource && LiveExists) return (livePath, false);
        // Fallback: whichever exists
        if (BackupExists) return (backupPath, true);
        if (LiveExists) return (livePath, false);
        return (null, false);
    }

    /// <summary>
    /// Opens a transcript from a catalog entry. Called by the MainViewModel's
    /// OpenTranscriptCommand.
    /// </summary>
    public async Task LoadTranscriptAsync(SessionEntry entry)
    {
        _currentEntry = entry;
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        var (path, isBackup) = ResolveSource(entry);

        if (path is null)
        {
            ErrorMessage = "Transcript file not found. Neither backup nor live copy exists.";
            IsLoaded = false;
            IsLoading = false;
            return;
        }

        ErrorMessage = "";
        IsLoading = true;
        IsLoaded = false;
        LoadStatus = "Loading...";
        SourcePath = path;
        _useBackupSource = isBackup;
        OnPropertyChanged(nameof(UseBackupSource));

        var options = new TranscriptReadOptions
        {
            IncludeAttachments = ShowAttachments,
            IncludeSystem = true,
            DecodeImages = true,
        };

        var progress = new Progress<int>(count =>
        {
            _dispatcher.BeginInvoke(() => LoadStatus = $"Parsed {count:N0} records...");
        });

        try
        {
            var transcript = await Task.Run(
                () => _reader.ReadAsync(path, options, progress, ct),
                ct).ConfigureAwait(true);

            ct.ThrowIfCancellationRequested();
            _transcript = transcript;

            // Header
            Title = transcript.Title ?? "(untitled)";
            Project = !string.IsNullOrEmpty(transcript.Cwd)
                ? Path.GetFileName(transcript.Cwd.TrimEnd('\\', '/'))
                : "";

            var first = transcript.FirstTimestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "?";
            var last = transcript.LastTimestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "?";
            DateRange = $"{first}  to  {last}";

            Stats = $"{transcript.Turns.Count} turns | " +
                    $"{transcript.ToolCalls} tool calls | " +
                    $"{transcript.Images} images | " +
                    $"{transcript.ThinkingBlocks} thinking ({transcript.ThinkingRedacted} redacted)";

            // Build turn VMs
            _allTurns = transcript.Turns
                .Select(t => new TurnViewModel(t, _renderer))
                .ToList();

            RebuildVisibleTurns();
            IsLoaded = true;
            LoadStatus = $"Loaded {transcript.Records:N0} records, {transcript.Turns.Count} turns";
        }
        catch (NotImplementedException)
        {
            ErrorMessage = "TranscriptReader not yet implemented.";
            LoadStatus = "";
        }
        catch (OperationCanceledException)
        {
            LoadStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error loading transcript: {ex.Message}";
            LoadStatus = "";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // -------------------------------------------------------- filtering

    private void RebuildVisibleTurns()
    {
        VisibleTurns.Clear();
        foreach (var turn in _allTurns)
        {
            if (!ShouldShowTurn(turn)) continue;
            VisibleTurns.Add(turn);
        }
        ApplySearch();
    }

    private bool ShouldShowTurn(TurnViewModel turn)
    {
        if (turn.Role == TranscriptRole.System)
        {
            // Check if it's a compact boundary or a system note
            var firstBlock = turn.Blocks.FirstOrDefault();
            if (firstBlock is not null)
            {
                if (firstBlock.Kind == TranscriptBlockKind.Attachment && !ShowAttachments)
                    return false;
                if (firstBlock.Kind == TranscriptBlockKind.SystemNote && !ShowSystem)
                    return false;
                if (firstBlock.Kind == TranscriptBlockKind.CompactBoundary && !ShowSystem)
                    return false;
            }
        }

        // Tool-result-only user turns follow ShowToolResults
        if (turn.IsToolResultOnly && !ShowToolResults)
            return false;

        // Apply block-level visibility for thinking / tool-call toggles.
        // A turn with no remaining visible blocks is hidden entirely.
        bool anyVisible = false;
        foreach (var block in turn.Blocks)
        {
            bool visible = block.Kind switch
            {
                TranscriptBlockKind.Thinking => ShowThinking,
                TranscriptBlockKind.ToolUse => ShowToolCalls,
                _ => true,
            };
            block.IsBlockVisible = visible;
            if (visible) anyVisible = true;
        }

        return anyVisible;
    }

    // ----------------------------------------------------------- search

    private void ApplySearch()
    {
        // Clear old highlights
        foreach (var hit in _searchHits.Where(i => i < VisibleTurns.Count))
        {
            VisibleTurns[hit].IsHighlighted = false;
        }
        _searchHits.Clear();
        _searchHitIndex = -1;

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            SearchResultText = "";
            return;
        }

        var query = SearchText;
        for (int i = 0; i < VisibleTurns.Count; i++)
        {
            var turn = VisibleTurns[i];
            bool match = false;
            foreach (var block in turn.Blocks)
            {
                if (block.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                { match = true; break; }
                if (block.InputJson?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                { match = true; break; }
                if (block.ToolName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                { match = true; break; }
            }
            if (match)
            {
                _searchHits.Add(i);
            }
        }

        SearchResultText = _searchHits.Count == 0
            ? "No matches"
            : $"{_searchHits.Count} match{(_searchHits.Count == 1 ? "" : "es")}";

        if (_searchHits.Count > 0)
        {
            _searchHitIndex = 0;
            HighlightAndScroll();
        }
    }

    private void SearchNext()
    {
        if (_searchHits.Count == 0) return;
        _searchHitIndex = (_searchHitIndex + 1) % _searchHits.Count;
        HighlightAndScroll();
    }

    private void SearchPrev()
    {
        if (_searchHits.Count == 0) return;
        _searchHitIndex = (_searchHitIndex - 1 + _searchHits.Count) % _searchHits.Count;
        HighlightAndScroll();
    }

    private void HighlightAndScroll()
    {
        // Clear previous highlights
        foreach (var turn in VisibleTurns)
            turn.IsHighlighted = false;

        if (_searchHitIndex >= 0 && _searchHitIndex < _searchHits.Count)
        {
            var idx = _searchHits[_searchHitIndex];
            VisibleTurns[idx].IsHighlighted = true;
            SearchResultText = $"{_searchHitIndex + 1} / {_searchHits.Count}";
            ScrollToTurnRequested?.Invoke(idx);
        }
    }

    private void JumpToTurn()
    {
        var target = JumpTurnNumber;
        for (int i = 0; i < VisibleTurns.Count; i++)
        {
            if (VisibleTurns[i].Index == target)
            {
                // Clear previous highlights
                foreach (var turn in VisibleTurns)
                    turn.IsHighlighted = false;

                VisibleTurns[i].IsHighlighted = true;
                ScrollToTurnRequested?.Invoke(i);
                return;
            }
        }
    }

    // ---------------------------------------------------------- export

    private ExportOptions BuildExportOptions() => new()
    {
        IncludeThinking = ShowThinking,
        IncludeToolCalls = ShowToolCalls,
        IncludeToolResults = ShowToolResults,
        IncludeSystem = ShowSystem,
        IncludeAttachments = ShowAttachments,
    };

    private void CopyMarkdown()
    {
        if (_transcript is null) return;
        try
        {
            // Clipboard: never embed images as data URIs - the resulting
            // multi-hundred-MB string freezes the UI and is useless to paste.
            var opts = BuildExportOptions();
            opts.EmbedImages = false;
            var md = _exporter.ToMarkdown(_transcript, opts);
            Clipboard.SetText(md);
            LoadStatus = "Copied to clipboard";
        }
        catch (NotImplementedException)
        {
            LoadStatus = "Exporter not yet implemented";
        }
        catch (Exception ex)
        {
            LoadStatus = $"Copy failed: {ex.Message}";
        }
    }

    private async Task ExportMarkdownAsync()
    {
        if (_transcript is null) return;
        var name = SanitizeFileName(Title) + ".md";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = name,
            DefaultExt = ".md",
            Filter = "Markdown|*.md|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            LoadStatus = "Exporting Markdown...";
            await _exporter.ExportMarkdownAsync(_transcript, dlg.FileName, BuildExportOptions(), CancellationToken.None)
                .ConfigureAwait(true);
            LoadStatus = $"Exported to {dlg.FileName}";
        }
        catch (NotImplementedException)
        {
            LoadStatus = "Exporter not yet implemented";
        }
        catch (Exception ex)
        {
            LoadStatus = $"Export failed: {ex.Message}";
        }
    }

    private async Task ExportHtmlAsync()
    {
        if (_transcript is null) return;
        var name = SanitizeFileName(Title) + ".html";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = name,
            DefaultExt = ".html",
            Filter = "HTML|*.html|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            LoadStatus = "Exporting HTML...";
            await _exporter.ExportHtmlAsync(_transcript, dlg.FileName, BuildExportOptions(), CancellationToken.None)
                .ConfigureAwait(true);
            LoadStatus = $"Exported to {dlg.FileName}";
        }
        catch (NotImplementedException)
        {
            LoadStatus = "Exporter not yet implemented";
        }
        catch (Exception ex)
        {
            LoadStatus = $"Export failed: {ex.Message}";
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "transcript" : clean;
    }

    // ----------------------------------------------------------- close

    private void Close()
    {
        _loadCts?.Cancel();
        _transcript = null;
        _allTurns.Clear();
        VisibleTurns.Clear();
        _searchHits.Clear();
        IsLoaded = false;
        IsLoading = false;
        Title = "";
        Project = "";
        DateRange = "";
        Stats = "";
        SourcePath = "";
        ErrorMessage = "";
        LoadStatus = "";
        SearchText = "";
        SearchResultText = "";
    }

    // -------------------------------------------------------- demo mode

    /// <summary>
    /// Populate the Transcript page directly from an in-memory <see cref="Transcript"/>
    /// without going through the file-based reader. Used by <c>--demo</c>.
    /// </summary>
    internal void LoadDemoTranscript(Transcript transcript)
    {
        _transcript = transcript;
        _currentEntry = null;

        Title = transcript.Title ?? "(untitled)";
        Project = !string.IsNullOrEmpty(transcript.Cwd)
            ? Path.GetFileName(transcript.Cwd.TrimEnd('\\', '/'))
            : "";

        var first = transcript.FirstTimestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "?";
        var last = transcript.LastTimestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "?";
        DateRange = $"{first}  to  {last}";

        Stats = $"{transcript.Turns.Count} turns | " +
                $"{transcript.ToolCalls} tool calls | " +
                $"{transcript.Images} images | " +
                $"{transcript.ThinkingBlocks} thinking ({transcript.ThinkingRedacted} redacted)";

        SourcePath = transcript.Path;
        BackupExists = true;
        LiveExists = false;

        _allTurns = transcript.Turns
            .Select(t => new TurnViewModel(t, _renderer))
            .ToList();

        RebuildVisibleTurns();
        IsLoaded = true;
        LoadStatus = $"Loaded {transcript.Records:N0} records, {transcript.Turns.Count} turns (demo)";
    }
}
