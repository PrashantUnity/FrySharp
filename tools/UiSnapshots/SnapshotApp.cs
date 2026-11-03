using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Material.Icons.Avalonia;

namespace PdfEditorApp.Plugins.CSharpEditor.Tools.UiSnapshots;

/// <summary>
/// The app's look, set up the way Runner/App.axaml does it: the Material 3 tokens (every <c>M3…Brush</c> the views use),
/// Fluent, Material icons, AvaloniaEdit's styles and the Material 3 control styles. Without these the views still lay
/// out, but their DynamicResource brushes resolve to nothing and the snapshot doesn't look like the app.
/// </summary>
internal sealed class SnapshotApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        var baseUri = new Uri("avares://UiSnapshots/");
        // FRY_PLUGIN_STYLES_ONLY=1: render as the plugin does inside FryPDF, where the app has none of the studio's styles and
        // the studio host includes them (once) for everything below it.
        bool pluginOnly = Environment.GetEnvironmentVariable("FRY_PLUGIN_STYLES_ONLY") is { Length: > 0 };
        if (!pluginOnly) Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://CSharpEditorPlugin/Styles/Tokens/StudioPaletteTokens.axaml") });
        Styles.Add(new FluentTheme());
        Styles.Add(new MaterialIconStyles(null));
        if (!pluginOnly) Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://CSharpEditorPlugin/Styles/StudioStyles.axaml") });
    }
}
