using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using Xunit;
using CSharpNotebookStudioViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks.CSharpNotebookStudioViewModel;
using NotebookCellViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks.NotebookCellViewModel;

namespace CSharpEditorPlugin.Tests;

public class CSharpNotebookStudioVsCodeLayoutTests : IDisposable
{
    private readonly string _testBaseDir;
    private readonly LocalScriptStorageService _testStorage;

    public CSharpNotebookStudioVsCodeLayoutTests()
    {
        _testBaseDir = Path.Combine(Path.GetTempPath(), "FryPDF_NotebookVsCodeTests_" + Guid.NewGuid().ToString("N"));
        _testStorage = new LocalScriptStorageService(_testBaseDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testBaseDir))
            {
                Directory.Delete(_testBaseDir, recursive: true);
            }
        }
        catch { }
    }

    private CSharpNotebookStudioViewModel CreateStudio(NotebookDocumentItem? notebook = null)
    {
        var nb = notebook ?? new NotebookDocumentItem
        {
            Title = "Notebook VS Code Studio Test"
        };

        return new CSharpNotebookStudioViewModel(
            nb,
            _testStorage,
            new RoslynCompilerService(),
            new ScriptExecutionEngine(),
            backToHubAction: () => { },
            backToHomeAction: () => { });
    }

    [Fact]
    public void InitialLayout_DefaultsToExplorerAndVisibleSidebar()
    {
        var studio = CreateStudio();

        Assert.Equal(0, studio.SelectedActivityBarIndex);
        Assert.True((bool)studio.IsSideBarVisible);
        Assert.Equal((string?)"EXPLORER", (string?)studio.SideBarTitle);
        Assert.True(studio.IsExplorerActive);
        Assert.False(studio.IsOutlineActive);
        Assert.False(studio.IsVariablesActive);
        Assert.False(studio.IsSearchActive);
        Assert.True(studio.SideBarGridLength.Value > 0);
    }

    [Theory]
    [InlineData(0, "EXPLORER", true, false, false, false)]
    [InlineData(1, "OUTLINE", false, true, false, false)]
    [InlineData(2, "LIVE VARIABLES", false, false, true, false)]
    [InlineData(3, "SEARCH", false, false, false, true)]
    public void SelectActivityBarItem_SwitchesTitleAndActiveState(
        int index,
        string expectedTitle,
        bool isExplorer,
        bool isOutline,
        bool isVariables,
        bool isSearch)
    {
        var studio = CreateStudio();
        studio.IsSideBarVisible = false;

        studio.SelectActivityBarItem(index);

        Assert.Equal(index, studio.SelectedActivityBarIndex);
        Assert.True((bool)studio.IsSideBarVisible);
        Assert.Equal(expectedTitle, (string?)studio.SideBarTitle);
        Assert.Equal(isExplorer, studio.IsExplorerActive);
        Assert.Equal(isOutline, studio.IsOutlineActive);
        Assert.Equal(isVariables, studio.IsVariablesActive);
        Assert.Equal(isSearch, studio.IsSearchActive);
    }

    [Fact]
    public void SelectActivityBarItem_Index6_ActivatesSourceControl()
    {
        var studio = CreateStudio();
        studio.IsSideBarVisible = false;

        Assert.False(studio.IsSourceControlActive);

        studio.SelectActivityBarItem(6);

        Assert.Equal(6, studio.SelectedActivityBarIndex);
        Assert.True(studio.IsSourceControlActive);
        Assert.StartsWith("SOURCE CONTROL", studio.SideBarTitle);
        Assert.True(studio.IsSideBarVisible);
    }

    [Fact]
    public void SelectActivityBarItem_WhenAlreadyActive_TogglesVisibility()
    {
        var studio = CreateStudio();
        Assert.True((bool)studio.IsSideBarVisible);
        Assert.Equal(0, studio.SelectedActivityBarIndex);

        // Clicking the already active tool toggles the side bar closed
        studio.SelectActivityBarItem(0);
        Assert.False((bool)studio.IsSideBarVisible);
        Assert.Equal<double>(0, studio.SideBarGridLength.Value);

        // Clicking it again re-opens the side bar
        studio.SelectActivityBarItem(0);
        Assert.True((bool)studio.IsSideBarVisible);
        Assert.True(studio.SideBarGridLength.Value > 0);
    }

    [Fact]
    public void SelectActivityBarItemCommand_FromStringParameter_ExecutesSuccessfully()
    {
        var studio = CreateStudio();

        // XAML passes string command parameters like "1", "2", "3", "6"
        Assert.True((bool)studio.SelectActivityBarItemCommand.CanExecute("1"));
        studio.SelectActivityBarItemCommand.Execute("1");

        Assert.Equal(1, studio.SelectedActivityBarIndex);
        Assert.True(studio.IsOutlineActive);
        Assert.Equal((string?)"OUTLINE", (string?)studio.SideBarTitle);

        Assert.True((bool)studio.SelectActivityBarItemCommand.CanExecute("2"));
        studio.SelectActivityBarItemCommand.Execute("2");

        Assert.Equal(2, studio.SelectedActivityBarIndex);
        Assert.True(studio.IsVariablesActive);
        Assert.Equal((string?)"LIVE VARIABLES", (string?)studio.SideBarTitle);

        Assert.True((bool)studio.SelectActivityBarItemCommand.CanExecute("6"));
        studio.SelectActivityBarItemCommand.Execute("6");

        Assert.Equal(6, studio.SelectedActivityBarIndex);
        Assert.True(studio.IsSourceControlActive);
        Assert.StartsWith("SOURCE CONTROL", studio.SideBarTitle);
    }

    [Fact]
    public void NotebookStudio_ImplementsIGitStudioHostViewModel()
    {
        var studio = CreateStudio();
        Assert.IsAssignableFrom<PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Git.IGitStudioHostViewModel>(studio);
    }

    [Fact]
    public void ToggleSideBar_TogglesVisibilityAndGridLength()
    {
        var studio = CreateStudio();
        Assert.True((bool)studio.IsSideBarVisible);

        studio.ToggleSideBar();
        Assert.False((bool)studio.IsSideBarVisible);
        Assert.Equal<double>(0, studio.SideBarGridLength.Value);

        studio.ToggleSideBar();
        Assert.True((bool)studio.IsSideBarVisible);
        Assert.True(studio.SideBarGridLength.Value > 0);
    }

    [Fact]
    public void LegacyToggles_OutlineAndVariables_DelegateToActivityBar()
    {
        var studio = CreateStudio();

        // Toggle Outline delegates to Activity Bar index 1
        studio.ToggleOutline();
        Assert.Equal(1, studio.SelectedActivityBarIndex);
        Assert.True(studio.IsOutlineActive);
        Assert.True((bool)studio.IsSideBarVisible);

        // Toggle again collapses
        studio.ToggleOutline();
        Assert.False((bool)studio.IsSideBarVisible);

        // Toggle Variable Inspector delegates to Activity Bar index 2
        studio.ToggleVariableInspector();
        Assert.Equal(2, studio.SelectedActivityBarIndex);
        Assert.True(studio.IsVariablesActive);
        Assert.True((bool)studio.IsSideBarVisible);
    }

    [Fact]
    public void Outline_SelectCellFromOutline_ActivatesCellAndFiresScrollEvent()
    {
        var studio = CreateStudio();
        var tab = studio.ActiveTab!;
        tab.AddCodeCell();
        tab.AddCodeCell();
        var targetCell = Enumerable.Last<NotebookCellViewModel>(tab.Cells);

        NotebookCellViewModel? scrolledCell = null;
        studio.RequestScrollToCell += cell => scrolledCell = cell;

        studio.SelectCellFromOutline(targetCell);

        Assert.Same(targetCell, tab.ActiveCell);
        Assert.Same(targetCell, scrolledCell);
    }

    [Fact]
    public void BetweenCellInsert_InsertCodeCellAfter_InsertsImmediatelyAfterTarget()
    {
        var studio = CreateStudio();
        var tab = studio.ActiveTab!;
        Assert.NotEmpty(tab.Cells);
        int initialCount = tab.Cells.Count;
        var initialCell = tab.Cells[0];
        initialCell.Source = "// Cell 0";

        studio.InsertCodeCellAfter(initialCell);

        Assert.Equal(initialCount + 1, tab.Cells.Count);
        Assert.Equal(initialCell, tab.Cells[0]);
        Assert.True((bool)tab.Cells[1].IsCodeCell);
        Assert.Same(tab.Cells[1], tab.ActiveCell);
    }

    [Fact]
    public void BetweenCellInsert_InsertMarkdownCellAfter_InsertsImmediatelyAfterTarget()
    {
        var studio = CreateStudio();
        var tab = studio.ActiveTab!;
        Assert.NotEmpty(tab.Cells);
        int initialCount = tab.Cells.Count;
        var initialCell = tab.Cells[0];
        initialCell.Source = "// Cell 0";

        studio.InsertMarkdownCellAfter(initialCell);

        Assert.Equal(initialCount + 1, tab.Cells.Count);
        Assert.Equal(initialCell, tab.Cells[0]);
        Assert.True((bool)tab.Cells[1].IsMarkdownCell);
        Assert.Same(tab.Cells[1], tab.ActiveCell);
    }

    [Fact]
    public void NotebookSearch_FiltersCellsBasedOnContent()
    {
        var studio = CreateStudio();
        var tab = studio.ActiveTab!;
        tab.Cells.Clear();

        tab.AddCodeCell();
        tab.Cells[0].Source = "int x = 42;\nConsole.WriteLine(x);";

        tab.AddCodeCell();
        tab.Cells[1].Source = "string name = \"FryPDF\";\n// No match here";

        tab.AddMarkdownCell();
        tab.Cells[2].Source = "# Header\nDocumentation cell with Console note";

        var cell1 = tab.Cells[0];
        var cell2 = tab.Cells[1];
        var cell3 = tab.Cells[2];

        studio.SearchText = "Console";

        Assert.Equal(2, studio.SearchResultsCount);
        Assert.Equal(2, studio.FilteredCells.Count());
        Assert.Contains(cell1, studio.FilteredCells);
        Assert.Contains(cell3, studio.FilteredCells);
        Assert.DoesNotContain(cell2, studio.FilteredCells);

        studio.SearchText = "";
        Assert.Equal(3, studio.FilteredCells.Count());
        Assert.Equal(3, studio.SearchResultsCount);
    }

    [Theory]
    [InlineData("\"hello\"", false, "#CE9178")]
    [InlineData("42", false, "#B5CEA8")]
    [InlineData("3.14", false, "#B5CEA8")]
    [InlineData("1000", false, "#B5CEA8")]
    [InlineData("true", false, "#569CD6")]
    [InlineData("False", false, "#569CD6")]
    [InlineData("[Collection]", false, "#4EC9B0")]
    [InlineData("", true, "#808080")]
    [InlineData("{ MyProp = 1 }", false, "#D4D4D4")]
    public void ObjectInspectorPropertyRow_ValueForeground_CalculatesAccurateSyntaxColors(
        string valueStr,
        bool isNull,
        string expectedHex)
    {
        var row = new ObjectInspectorPropertyRow
        {
            Name = "testProp",
            SimpleValueText = valueStr,
            IsNull = isNull
        };

        Assert.Equal(expectedHex, row.ValueForeground);
    }
}
