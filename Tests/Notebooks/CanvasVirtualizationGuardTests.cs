using System.Text.RegularExpressions;
using CSharpEditorPlugin.Tests.TestSupport;
using Xunit;

namespace CSharpEditorPlugin.Tests.Notebooks;

/// <summary>
/// The lists that grow with the user's data must virtualize. A plain StackPanel builds every row: the notebook canvas once
/// lost its VirtualizingStackPanel this way, and a 1,000-cell notebook then took 3.5 minutes and 17 GB to open, with every
/// test still passing.
/// </summary>
public class CanvasVirtualizationGuardTests
{
    [Theory]
    [InlineData("Views/CSharpNotebookStudioView.axaml", "CanvasRows")]
    [InlineData("Controls/Studio/StudioExplorerPanelControl.axaml", "ExplorerRows.Rows")]
    public void TheList_BoundTo_IsLaidOutByAVirtualizingStackPanel(string file, string source)
    {
        var xaml = File.ReadAllText(Path.Combine(RepositoryPaths.Root, file));
        var start = xaml.IndexOf($"ItemsSource=\"{{Binding {source}}}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{file} no longer binds a list to {source}: update this guard.");

        // The list's own panel: the first ItemsPanelTemplate after the binding, before the list ends.
        var end = xaml.IndexOf("</ItemsControl>", start, StringComparison.Ordinal);
        var list = xaml[start..end];
        var panel = Regex.Match(list, @"<ItemsPanelTemplate>\s*<(\w+)");
        Assert.True(panel.Success, $"The list bound to {source} declares no items panel.");
        Assert.Equal("VirtualizingStackPanel", panel.Groups[1].Value);
    }
}
