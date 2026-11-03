using System.Collections.ObjectModel;
using System.Collections.Specialized;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;
using Xunit;
using CSharpCodeStudioViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.CSharpCodeStudioViewModel;
using ExplorerItemViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer.ExplorerItemViewModel;

namespace CSharpEditorPlugin.Tests;

public class ExplorerRowListTests
{
    private static ExplorerItemViewModel Folder(string name, ExplorerItemViewModel? parent = null) =>
        new() { Name = name, IsDirectory = true, Parent = parent, Depth = (parent?.Depth ?? -1) + 1 };

    private static ExplorerItemViewModel File(string name, ExplorerItemViewModel? parent = null) =>
        new() { Name = name, Parent = parent, Depth = (parent?.Depth ?? -1) + 1 };

    private static string Names(ExplorerRowList list) => string.Join(",", list.Rows.Select<ExplorerItemViewModel, string>(r => r.Name));

    // A (folder): a1, N (folder): deep      z
    private static (ObservableCollection<ExplorerItemViewModel> Roots, ExplorerItemViewModel A, ExplorerItemViewModel N) Sample()
    {
        var roots = new ObservableCollection<ExplorerItemViewModel>();
        var a = Folder("A");
        a.Children.Add(File("a1", a));
        var n = Folder("N", a);
        n.Children.Add(File("deep", n));
        a.Children.Add(n);
        roots.Add(a);
        roots.Add(File("z"));
        return (roots, a, n);
    }

    [Fact]
    public void CollapsedFolders_ShowOnlyTheirOwnRow()
    {
        var (roots, _, _) = Sample();

        var list = new ExplorerRowList(roots);

        Assert.Equal("A,z", Names(list));
    }

    [Fact]
    public void ExpandingAFolder_InsertsItsRowsRightAfterIt_AndCollapsingRemovesThemAgain()
    {
        var (roots, a, n) = Sample();
        var list = new ExplorerRowList(roots);

        a.IsExpanded = true;
        Assert.Equal("A,a1,N,z", Names(list));

        n.IsExpanded = true;
        Assert.Equal("A,a1,N,deep,z", Names(list));

        a.IsExpanded = false;
        Assert.Equal("A,z", Names(list));
    }

    [Fact]
    public void ExpandingAFolderHiddenByACollapsedAncestor_ChangesNothing_UntilTheAncestorOpens()
    {
        var (roots, a, n) = Sample();
        var list = new ExplorerRowList(roots);

        n.IsExpanded = true;
        Assert.Equal("A,z", Names(list));

        a.IsExpanded = true;
        Assert.Equal("A,a1,N,deep,z", Names(list));
    }

    [Fact]
    public void ExpandingOrCollapsing_RaisesOneRangeNotification_NotOnePerRow()
    {
        var (roots, a, _) = Sample();
        var list = new ExplorerRowList(roots);
        var events = new List<NotifyCollectionChangedEventArgs>();
        list.Rows.CollectionChanged += (_, e) => events.Add(e);

        a.IsExpanded = true;
        a.IsExpanded = false;

        Assert.Equal(2, events.Count);
        Assert.Equal(NotifyCollectionChangedAction.Add, events[0].Action);
        Assert.Equal(2, events[0].NewItems!.Count);
        Assert.Equal(1, events[0].NewStartingIndex);
        Assert.Equal(NotifyCollectionChangedAction.Remove, events[1].Action);
        Assert.Equal(2, events[1].OldItems!.Count);
    }

    [Fact]
    public void AddingAndRemovingItems_FollowsTheTree()
    {
        var (roots, a, _) = Sample();
        var list = new ExplorerRowList(roots);
        a.IsExpanded = true;

        var added = File("a2", a);
        a.Children.Add(added);
        Assert.Equal("A,a1,N,a2,z", Names(list));

        a.Children.Remove(added);
        roots.Add(File("zz"));
        Assert.Equal("A,a1,N,z,zz", Names(list));
    }

    [Fact]
    public void ANewFolderAddedLater_IsWatchedToo()
    {
        var (roots, _, _) = Sample();
        var list = new ExplorerRowList(roots);

        var late = Folder("Late");
        late.Children.Add(File("l1", late));
        roots.Add(late);
        late.IsExpanded = true;

        Assert.Equal("A,z,Late,l1", Names(list));
    }

    // A rebuild changes the list only where the rows differ: at most one removal and one insertion, whatever was changed.
    [Fact]
    public void Suspend_TurnsManyChangesIntoOneUpdate()
    {
        var (roots, a, _) = Sample();
        var list = new ExplorerRowList(roots);
        var events = new List<NotifyCollectionChangedEventArgs>();
        list.Rows.CollectionChanged += (_, e) => events.Add(e);

        using (list.Suspend())
        {
            for (var i = 0; i < 50; i++) roots.Add(File($"f{i}"));
            a.IsExpanded = true;
            Assert.Empty(events);
        }

        Assert.InRange(events.Count, 1, 2);
        Assert.Equal(1 + 2 + 1 + 50, list.Rows.Count); // A, a1, N, z, f0..f49 (A open)
        Assert.Equal("A,a1,N,z,f0", string.Join(",", list.Rows.Take(5).Select(r => r.Name)));
    }

    [Fact]
    public void ANestedSuspend_RebuildsOnlyWhenTheOutermostScopeEnds()
    {
        var (roots, _, _) = Sample();
        var list = new ExplorerRowList(roots);
        var changes = 0;
        list.Rows.CollectionChanged += (_, _) => changes++;

        using (list.Suspend())
        {
            using (list.Suspend())
            {
                roots.Add(File("x"));
            }

            Assert.Equal(0, changes);
        }

        Assert.Equal(1, changes);
        Assert.Equal("A,z,x", Names(list));
    }

    // A refresh of a workspace that didn't change gives the same items back: the rows on screen must stay as they are.
    [Fact]
    public void ARebuildToTheSameRows_RaisesNothing()
    {
        var (roots, _, _) = Sample();
        var list = new ExplorerRowList(roots);
        var events = new List<NotifyCollectionChangedEventArgs>();
        list.Rows.CollectionChanged += (_, e) => events.Add(e);

        using (list.Suspend())
        {
            var extra = File("gone-again");
            roots.Add(extra);
            roots.Remove(extra);
        }

        Assert.Empty(events);
        Assert.Equal("A,z", Names(list));
    }

    // A row added in the middle is inserted there: the rows before and after it (and their controls) stay.
    [Fact]
    public void ARowAddedInTheMiddle_IsInsertedWithoutResettingTheList()
    {
        var (roots, _, _) = Sample();
        var list = new ExplorerRowList(roots);
        var kept = list.Rows.ToList();
        var events = new List<NotifyCollectionChangedEventArgs>();
        list.Rows.CollectionChanged += (_, e) => events.Add(e);

        roots.Insert(1, File("m"));

        var change = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Add, change.Action);
        Assert.Equal(1, change.NewStartingIndex);
        Assert.Equal("A,m,z", Names(list));
        Assert.Same(kept[0], list.Rows[0]);
        Assert.Same(kept[1], list.Rows[2]);
    }

    [Fact]
    public void ChangeChildren_SwapsOnlyTheFoldersRows_AndDoesNotResetTheList()
    {
        var (roots, a, _) = Sample();
        a.IsExpanded = true;
        var list = new ExplorerRowList(roots);
        var actions = new List<NotifyCollectionChangedAction>();
        list.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        list.ChangeChildren(a, () =>
        {
            a.Children.Clear();
            a.Children.Add(File("b1", a));
            a.Children.Add(File("b2", a));
        });

        Assert.Equal("A,b1,b2,z", Names(list));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
    }

    [Fact]
    public void ChangeChildren_OfACollapsedFolder_LeavesTheRowsAlone_UntilItOpens()
    {
        var (roots, a, _) = Sample();
        var list = new ExplorerRowList(roots);
        var changes = 0;
        list.Rows.CollectionChanged += (_, _) => changes++;

        list.ChangeChildren(a, () =>
        {
            a.Children.Clear();
            a.Children.Add(File("b1", a));
        });

        Assert.Equal(0, changes);
        Assert.Equal("A,z", Names(list));
        a.IsExpanded = true;
        Assert.Equal("A,b1,z", Names(list));
    }

    [Fact]
    public void ChangeChildren_WithAFolderThatIsOpenedAsItIsAdded_ShowsItsRowsToo()
    {
        var (roots, a, _) = Sample();
        a.IsExpanded = true;
        var list = new ExplorerRowList(roots);

        list.ChangeChildren(a, () =>
        {
            a.Children.Clear();
            var sub = Folder("sub", a);
            sub.Children.Add(File("s1", sub));
            a.Children.Add(sub);
            sub.IsExpanded = true;
        });

        Assert.Equal("A,sub,s1,z", Names(list));
    }
}

public class ExplorerRowListStudioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FryPDF_ExplorerRowsTests_" + Guid.NewGuid().ToString("N"));
    private readonly LocalScriptStorageService _storage;

    public ExplorerRowListStudioTests()
    {
        _storage = new LocalScriptStorageService(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // The OS cleans the temp folder eventually.
        }
    }

    // A studio lists its folder in the background when it is made: wait for the listing, so the Explorer has its files.
    private async Task<CSharpCodeStudioViewModel> CreateStudioAsync(PdfEditorApp.Plugins.CSharpEditor.Models.ScriptDocumentItem script)
    {
        var studio = new CSharpCodeStudioViewModel(
            script,
            _storage,
            new RoslynCompilerService(),
            new ScriptExecutionEngine(),
            backToHubAction: () => { },
            backToHomeAction: () => { });
        await studio.RefreshExplorerAsync();
        return studio;
    }

    [Fact]
    public async Task TheStudiosRows_ListFoldersCollapsed_AndOpeningOneRevealsItsFiles()
    {
        var folder = await _storage.CreateFolderAsync(null, "Algorithms");
        await _storage.CreateNewScriptAsync("Inside One", folderPath: folder);
        await _storage.CreateNewScriptAsync("Inside Two", folderPath: folder);
        var loose = await _storage.CreateNewScriptAsync("Loose");

        var studio = await CreateStudioAsync(loose);

        Assert.Equal(new[] { "Algorithms", "Loose.frycs" }, studio.ExplorerRows.Rows.Select<ExplorerItemViewModel, string>(r => r.Name));

        studio.ExplorerRootItems.Single(x => x.IsDirectory).IsExpanded = true;

        Assert.Equal(new[] { "Algorithms", "Inside One.frycs", "Inside Two.frycs", "Loose.frycs" }, studio.ExplorerRows.Rows.Select<ExplorerItemViewModel, string>(r => r.Name));
    }

    [Fact]
    public async Task OpeningAScriptInsideACollapsedFolder_ExpandsItSoItsRowIsThere()
    {
        var folder = await _storage.CreateFolderAsync(null, "Deep");
        var inside = await _storage.CreateNewScriptAsync("Buried", folderPath: folder);

        var studio = await CreateStudioAsync(inside);

        Assert.Contains(studio.ExplorerRows.Rows, r => r.DocumentId == inside.Id && r.IsSelected);
    }

    [Fact]
    public async Task RefreshingTheExplorer_KeepsTheRowsInStepWithTheTree()
    {
        var first = await _storage.CreateNewScriptAsync("One");
        var studio = await CreateStudioAsync(first);

        await _storage.CreateNewScriptAsync("Two");
        await studio.RefreshExplorerAsync();

        Assert.Equal(new[] { "One.frycs", "Two.frycs" }, studio.ExplorerRows.Rows.Select<ExplorerItemViewModel, string>(r => r.Name));
        Assert.Equal(studio.ExplorerRootItems.Count, studio.ExplorerRows.Rows.Count);
    }
}
