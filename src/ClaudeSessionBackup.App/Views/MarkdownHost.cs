using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ClaudeSessionBackup.App.ViewModels;

namespace ClaudeSessionBackup.App.Views;

/// <summary>
/// Gives a <see cref="FlowDocumentScrollViewer"/> its own rendered
/// <see cref="FlowDocument"/> for the block it is currently showing.
/// </summary>
/// <remarks>
/// A FlowDocument has exactly ONE owning viewer, the way a UIElement has one
/// parent. Handing a viewer a document another viewer still holds throws
/// <c>ArgumentException: Document belongs to another FlowDocumentScrollViewer
/// already</c>, and XAML rethrows that from the DataTemplate as
/// XamlParseException - the crash of 2026-09-06.
///
/// It fired because the turn list virtualises with
/// VirtualizationMode="Recycling" and BlockViewModel cached one document per
/// block: scrolling a text turn out of view detached its viewer WITHOUT
/// clearing Document, so the viewer kept ownership, and scrolling back handed
/// the same instance to a second, live viewer.
///
/// Two rules make that unrepresentable:
/// <list type="bullet">
///   <item>every attach renders its OWN document, so no instance is ever shared;</item>
///   <item>Unloaded clears Document, so a recycled viewer releases the element
///         tree instead of pinning it for the life of the page.</item>
/// </list>
/// Rendering is a Markdig parse plus an element walk - cheap per screenful, and
/// dropping the cache bounds memory by what is VISIBLE rather than by what has
/// been visited (an 18k-turn transcript used to accumulate 18k documents).
/// </remarks>
public static class MarkdownHost
{
    /// <summary>The block whose Markdown the viewer should render.</summary>
    public static readonly DependencyProperty BlockProperty =
        DependencyProperty.RegisterAttached(
            "Block",
            typeof(BlockViewModel),
            typeof(MarkdownHost),
            new PropertyMetadata(null, OnBlockChanged));

    public static void SetBlock(DependencyObject element, BlockViewModel? value) =>
        element.SetValue(BlockProperty, value);

    public static BlockViewModel? GetBlock(DependencyObject element) =>
        (BlockViewModel?)element.GetValue(BlockProperty);

    /// <summary>
    /// Marks a viewer whose Loaded/Unloaded handlers are already wired. Recycling
    /// reuses one viewer for many blocks, so hooking on every Block change would
    /// stack a duplicate handler pair per recycle.
    /// </summary>
    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached(
            "Hooked", typeof(bool), typeof(MarkdownHost), new PropertyMetadata(false));

    private static void OnBlockChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FlowDocumentScrollViewer viewer) return;

        if (!(bool)viewer.GetValue(HookedProperty))
        {
            viewer.SetValue(HookedProperty, true);
            viewer.Loaded += OnViewerLoaded;
            viewer.Unloaded += OnViewerUnloaded;
        }

        // The previous block's document is this viewer's to release. Clearing it
        // first also makes Build below unconditional for the new block.
        viewer.Document = null;

        // A recycled container can change block while it is still in the tree, so
        // there is no Loaded to wait for; one that is not loaded yet renders in
        // OnViewerLoaded instead, which also skips containers that never appear.
        if (viewer.IsLoaded)
            Build(viewer);
    }

    private static void OnViewerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FlowDocumentScrollViewer viewer)
            Build(viewer);
    }

    private static void OnViewerUnloaded(object sender, RoutedEventArgs e)
    {
        // Releases ownership AND the rendered tree. Without this the viewer would
        // still own the document when the page is shown again, which is the exact
        // condition that used to throw.
        if (sender is FlowDocumentScrollViewer viewer)
            viewer.Document = null;
    }

    private static void Build(FlowDocumentScrollViewer viewer)
    {
        // Already showing this block: Loaded can fire after OnBlockChanged has
        // rendered, and re-rendering would throw away good scroll position.
        if (viewer.Document is not null) return;

        viewer.Document = GetBlock(viewer)?.BuildDocument();
    }
}
