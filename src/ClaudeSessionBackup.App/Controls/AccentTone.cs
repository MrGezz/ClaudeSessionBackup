namespace ClaudeSessionBackup.App.Controls;

/// <summary>
/// Which of the semantic status colours a small surface is tinted with.
/// </summary>
/// <remarks>
/// Each value names one of the Status* brush TRIPLES that Semantic.Dark.xaml and
/// Semantic.Light.xaml both define (ink / 10 % fill / 45 % line). Naming the
/// tone rather than the colour is what lets a card, a pill and an empty state
/// agree on what "this is fine" and "look at this" look like, and keeps every
/// one of them following the light/dark toggle through DynamicResource.
/// </remarks>
public enum AccentTone
{
    /// <summary>Nothing to report - grey. The default.</summary>
    Neutral,

    /// <summary>Here is what to do next - violet.</summary>
    Info,

    /// <summary>Checked and healthy - green.</summary>
    Success,

    /// <summary>Worth a look - amber.</summary>
    Warning,

    /// <summary>Something is wrong - red.</summary>
    Danger,
}
