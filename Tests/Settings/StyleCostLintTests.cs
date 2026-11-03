using Avalonia;
using Material.Icons;
using PdfEditorApp.Plugins.CSharpEditor.Controls.Common;
using CSharpEditorPlugin.Tests.TestSupport;
using Xunit;

namespace CSharpEditorPlugin.Tests;

/// <summary>
/// Guards against what made every control of the studio cost more than it should (see the performance mandate, §7):
/// a second copy of the studio's styles, endless animations, icon names left to Avalonia's slow conversion.
/// </summary>
public class StyleCostLintTests
{
    private static readonly string[] Folders = ["Views", "Controls", "Styles", "Runner", "Visualizers", "Charting", "Charting3D"];

    private static IEnumerable<(string Path, string Text)> Xaml() =>
        Folders.Select(f => Path.Combine(RepositoryPaths.Root, f))
            .Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, "*.axaml", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(p => (Path.GetRelativePath(RepositoryPaths.Root, p).Replace('\\', '/'), File.ReadAllText(p)));

    // The bundle applies once on any branch of the tree: the Runner's application has it, and code adds it where nothing
    // above does (StudioStyleSheet). A control or page that includes it again doubles what each control below it costs.
    [Fact]
    public void TheStudioStyles_AreIncludedInXamlOnlyByTheRunnersApplication()
    {
        string[] allowed =
        [
            "Runner/App.axaml",
            // Forwarders the snapshot tool includes on views it shows without the studio host.
            "Controls/SharedStudioStyles.axaml",
            "Views/CSharpManagerStyles.axaml",
        ];

        var offenders = Xaml()
            .Where(x => x.Text.Contains("Styles/StudioStyles.axaml", StringComparison.Ordinal) && !allowed.Contains(x.Path))
            .Select(x => x.Path)
            .ToList();

        Assert.True(offenders.Count == 0, "Include the studio's styles through StudioStyleSheet, not in XAML: " + string.Join(", ", offenders));
    }

    // AvaloniaEdit's theme comes with the studio's styles; another copy in a view is a second one.
    [Fact]
    public void TheEditorTheme_IsIncludedOnlyByTheStudioStyles()
    {
        var offenders = Xaml()
            .Where(x => x.Text.Contains("avares://AvaloniaEdit/Themes/", StringComparison.Ordinal) && x.Path != "Styles/StudioStyles.axaml")
            .Select(x => x.Path)
            .ToList();

        Assert.True(offenders.Count == 0, "AvaloniaEdit's theme is part of Styles/StudioStyles.axaml: " + string.Join(", ", offenders));
    }

    // An endless animation redraws its page on every frame for as long as it is shown (a pulsing status dot cost the Hub
    // ~60 ms of drawing per frame).
    [Fact]
    public void NothingAnimatesForever()
    {
        var offenders = Xaml()
            .Where(x => x.Text.Contains("IterationCount=\"Infinite\"", StringComparison.OrdinalIgnoreCase)
                        || x.Text.Contains("IterationCount=\"INFINITE\"", StringComparison.Ordinal))
            .Select(x => x.Path)
            .ToList();

        Assert.True(offenders.Count == 0, "An animation that never ends keeps the page redrawing: " + string.Join(", ", offenders));
    }

    [Fact]
    public void IconKindConverter_TurnsANameIntoItsIcon_InAnyCase()
    {
        var converter = IconKindConverter.Instance;

        Assert.Equal(MaterialIconKind.ChevronDown, converter.Convert("ChevronDown", typeof(MaterialIconKind), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(MaterialIconKind.ChevronDown, converter.Convert("chevrondown", typeof(MaterialIconKind), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(MaterialIconKind.Play, converter.Convert(MaterialIconKind.Play, typeof(MaterialIconKind), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    // An unknown or missing name leaves the binding to its FallbackValue, as a failed conversion did.
    [Fact]
    public void IconKindConverter_GivesNothingForANameThatIsNoIcon()
    {
        var converter = IconKindConverter.Instance;

        Assert.Same(AvaloniaProperty.UnsetValue, converter.Convert("NoSuchIconAnywhere", typeof(MaterialIconKind), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Same(AvaloniaProperty.UnsetValue, converter.Convert(null, typeof(MaterialIconKind), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(IconKindConverter.Parse(""));
    }
}
