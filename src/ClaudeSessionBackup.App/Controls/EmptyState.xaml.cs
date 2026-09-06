using System.Windows;
using System.Windows.Controls;

namespace ClaudeSessionBackup.App.Controls;

/// <summary>
/// The panel shown where a list, grid or page has no rows: a tinted glyph tile
/// over a title, one line of explanation, and optional action buttons.
/// </summary>
/// <remarks>
/// Replaces the bare italic sentences the pages used to show. An empty list is
/// the state a user hits first and most often, and a sentence floating in a
/// blank page reads as a rendering failure rather than as an answer; giving it
/// the same card geometry as the rest of the app says "this is the content".
/// The template - including the tone and compact variants - lives in
/// EmptyState.xaml.
/// </remarks>
public partial class EmptyState : UserControl
{
    public EmptyState()
    {
        InitializeComponent();
    }

    /// <summary>
    /// A Segoe MDL2 Assets glyph, written in XAML as a character entity such
    /// as &amp;#xE8A5; (document). Empty renders a blank tile.
    /// </summary>
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(
            nameof(Glyph), typeof(string), typeof(EmptyState),
            new PropertyMetadata(string.Empty));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>The headline: what state the user is in, in a few words.</summary>
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(EmptyState),
            new PropertyMetadata(string.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>One sentence saying what to do about it.</summary>
    public static readonly DependencyProperty BodyProperty =
        DependencyProperty.Register(
            nameof(Body), typeof(string), typeof(EmptyState),
            new PropertyMetadata(string.Empty));

    public string Body
    {
        get => (string)GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    /// <summary>Optional buttons rendered under the body.</summary>
    public static readonly DependencyProperty ActionsProperty =
        DependencyProperty.Register(
            nameof(Actions), typeof(object), typeof(EmptyState),
            new PropertyMetadata(null));

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    /// <summary>Which Status* triple tints the shell, the tile and the glyph.</summary>
    public static readonly DependencyProperty ToneProperty =
        DependencyProperty.Register(
            nameof(Tone), typeof(AccentTone), typeof(EmptyState),
            new PropertyMetadata(AccentTone.Neutral));

    public AccentTone Tone
    {
        get => (AccentTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>
    /// Half-height variant for an empty panel INSIDE a populated page, where the
    /// full-height version would push the rest of the page off screen.
    /// </summary>
    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.Register(
            nameof(Compact), typeof(bool), typeof(EmptyState),
            new PropertyMetadata(false));

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }
}
