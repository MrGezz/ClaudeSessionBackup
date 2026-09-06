using System.Windows;
using System.Windows.Controls;

namespace ClaudeSessionBackup.App.Controls;

/// <summary>
/// One headline number for the Dashboard: glyph tile, label, value, detail.
/// </summary>
/// <remarks>
/// The store grid answers "what happened to each store". These answer the
/// question the app is opened with - is my data safe right now - without making
/// the user read thirteen rows and a log. Values come from the view model
/// already formatted; the card does no arithmetic, so what it shows cannot
/// disagree with the grid below it.
/// </remarks>
public partial class MetricCard : UserControl
{
    public MetricCard()
    {
        InitializeComponent();
    }

    /// <summary>
    /// A Segoe MDL2 Assets glyph, written in XAML as a character entity such as
    /// <c>&amp;#xE8B7;</c> (folder).
    /// </summary>
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(
            nameof(Glyph), typeof(string), typeof(MetricCard),
            new PropertyMetadata(string.Empty));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>What the number counts, in two or three words.</summary>
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(MetricCard),
            new PropertyMetadata(string.Empty));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The number itself, pre-formatted.</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value), typeof(string), typeof(MetricCard),
            new PropertyMetadata(string.Empty));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>One short line of context under the value.</summary>
    public static readonly DependencyProperty DetailProperty =
        DependencyProperty.Register(
            nameof(Detail), typeof(string), typeof(MetricCard),
            new PropertyMetadata(string.Empty));

    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>Which Status* triple tints the tile and the glyph.</summary>
    public static readonly DependencyProperty ToneProperty =
        DependencyProperty.Register(
            nameof(Tone), typeof(AccentTone), typeof(MetricCard),
            new PropertyMetadata(AccentTone.Neutral));

    public AccentTone Tone
    {
        get => (AccentTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }
}
