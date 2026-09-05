using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ClaudeSessionBackup.App.Services;

/// <summary>
/// Swaps the application between the CM2 dark and light palettes at runtime.
/// </summary>
/// <remarks>
/// <para>
/// The whole mechanism is dictionary replacement. Views reach every brush
/// through <c>{DynamicResource}</c>, and ASSIGNING into
/// <see cref="ResourceDictionary.MergedDictionaries"/> is what makes WPF
/// re-resolve those references - mutating a dictionary in place does not. So a
/// theme switch restyles the open window without rebuilding it.
/// </para>
/// <para>
/// Adapted from Sync-ACC's ThemeManager, which is where the non-obvious parts
/// of this were paid for in debugging.
/// </para>
/// </remarks>
public sealed class ThemeManager
{
    /// <summary>
    /// Index of the CM2 brush restatement in App.xaml's merged dictionaries.
    /// If you add a dictionary above it there, fix these.
    /// </summary>
    private const int CharcoalIndex = 2;

    private const int SemanticIndex = 3;

    public bool IsDark { get; private set; } = true;

    /// <summary>Applies a theme and returns it, so callers can persist the choice.</summary>
    public bool Apply(bool dark)
    {
        IsDark = dark;

        var app = Application.Current;
        if (app is null)
        {
            return dark;
        }

        // WPF-UI's own manager first, so our dictionaries still win the merge
        // order below. On a live switch it also re-applies the backdrop and
        // flips the title-bar chrome between its dark and light forms.
        //
        // updateAccent:false because the accent is ours, not the system's -
        // letting it update would overwrite the CM2 cyan with whatever the
        // user's Windows personalisation colour happens to be.
        ApplicationThemeManager.Apply(
            dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            WindowBackdropType.None,
            updateAccent: false);

        var merged = app.Resources.MergedDictionaries;
        ReplaceAt(merged, CharcoalIndex, dark ? "Charcoal" : "CharcoalLight");
        ReplaceAt(merged, SemanticIndex, dark ? "Semantic.Dark" : "Semantic.Light");

        // The backdrop manager may have just written a LOCAL Background value
        // over each window's binding. Local values beat style setters, so
        // without putting the resource reference back the window keeps the
        // colour it had before the switch while everything inside it changes -
        // which reads as a rendering bug rather than a theme.
        foreach (Window w in app.Windows)
        {
            w.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
        }

        ApplyAccent(app);
        return dark;
    }

    public bool Toggle() => Apply(!IsDark);

    private static void ReplaceAt(Collection<ResourceDictionary> merged, int index, string name)
    {
        var fresh = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{name}.xaml", UriKind.Absolute),
        };

        if (index >= 0 && index < merged.Count)
        {
            merged[index] = fresh;
        }
        else
        {
            // Defensive: if App.xaml was edited and the index no longer exists,
            // appending still produces a correct app (ours merge last anyway)
            // rather than throwing on startup.
            merged.Add(fresh);
        }
    }

    /// <summary>
    /// Feeds the CM2 accent pair to WPF-UI's accent manager verbatim.
    /// </summary>
    /// <remarks>
    /// The four-argument overload is deliberate. The
    /// <c>(Color, ApplicationTheme)</c> overload DERIVES Primary by brightening
    /// the colour it is given, which turns #00BCD4 into a much lighter cyan on
    /// every primary button and ON toggle - a colour the palette never
    /// contains. Passing the tiers explicitly is the only way to get the
    /// palette's own values.
    ///
    /// Tier meanings, as WPF-UI's dictionaries bind them: Primary is the ON
    /// toggle, slider thumb and focus border; Secondary is the accent button AT
    /// REST; Tertiary is its hover. So rest = accent, hover = accent-2.
    ///
    /// The brushes are read from the CURRENTLY merged Semantic dictionary, so
    /// the light theme gets #0097A7 rather than the dark #00BCD4.
    /// </remarks>
    private static void ApplyAccent(Application app)
    {
        if (app.Resources["AppAccentBrush"] is SolidColorBrush accent &&
            app.Resources["AppAccentHoverBrush"] is SolidColorBrush hover)
        {
            ApplicationAccentColorManager.Apply(
                systemAccent: accent.Color,
                primaryAccent: accent.Color,
                secondaryAccent: accent.Color,
                tertiaryAccent: hover.Color);
        }

        // No fallback branch on purpose: the keys are defined in both Semantic
        // dictionaries and their parity is checked, so a miss here means the
        // merge order is wrong - and silently applying some other accent would
        // hide that.
    }
}
