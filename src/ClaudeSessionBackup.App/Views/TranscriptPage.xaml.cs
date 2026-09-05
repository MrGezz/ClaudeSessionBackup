using System.Windows;
using System.Windows.Controls;
using ClaudeSessionBackup.App.ViewModels;
using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.App.Views;

/// <summary>
/// Code-behind for the Transcript page. Wires the scroll-to-turn event from
/// the view model into the virtualised ListBox.
/// </summary>
/// <remarks>
/// The UserControl's DataContext stays as MainViewModel (inherited from
/// MainWindow) so the Visibility DataTrigger in MainWindow.xaml can bind
/// {Binding CurrentPage}.  The root Grid binds its DataContext to
/// TranscriptVM, so all content bindings address TranscriptViewModel.
/// The scroll-to-turn event is wired when the Grid's DataContext changes.
/// </remarks>
public partial class TranscriptPage : UserControl
{
    public TranscriptPage()
    {
        InitializeComponent();
        // The Grid (Content) holds TranscriptVM as its DataContext.
        if (Content is FrameworkElement grid)
        {
            grid.DataContextChanged += OnGridDataContextChanged;
        }
    }

    private void OnGridDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TranscriptViewModel oldVm)
        {
            oldVm.ScrollToTurnRequested -= ScrollToIndex;
        }
        if (e.NewValue is TranscriptViewModel newVm)
        {
            newVm.ScrollToTurnRequested += ScrollToIndex;
        }
    }

    private void ScrollToIndex(int index)
    {
        if (index >= 0 && index < TurnList.Items.Count)
        {
            TurnList.ScrollIntoView(TurnList.Items[index]);
        }
    }

    private void ToggleToolResult_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is BlockViewModel block)
        {
            block.ShowAllToolResult = !block.ShowAllToolResult;
        }
    }
}

/// <summary>
/// Selects the right DataTemplate for each <see cref="BlockViewModel"/> based
/// on its <see cref="TranscriptBlockKind"/>.
/// </summary>
public sealed class BlockTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? ThinkingTemplate { get; set; }
    public DataTemplate? ToolUseTemplate { get; set; }
    public DataTemplate? ToolResultTemplate { get; set; }
    public DataTemplate? ImageTemplate { get; set; }
    public DataTemplate? SystemNoteTemplate { get; set; }
    public DataTemplate? CompactBoundaryTemplate { get; set; }
    public DataTemplate? AttachmentTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not BlockViewModel block) return base.SelectTemplate(item, container);

        return block.Kind switch
        {
            TranscriptBlockKind.Text => TextTemplate,
            TranscriptBlockKind.Thinking => ThinkingTemplate,
            TranscriptBlockKind.ToolUse => ToolUseTemplate,
            TranscriptBlockKind.ToolResult => ToolResultTemplate,
            TranscriptBlockKind.Image => ImageTemplate,
            TranscriptBlockKind.SystemNote => SystemNoteTemplate,
            TranscriptBlockKind.CompactBoundary => CompactBoundaryTemplate,
            TranscriptBlockKind.Attachment => AttachmentTemplate,
            _ => base.SelectTemplate(item, container),
        };
    }
}
