using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;
using Xunit;
using CSharpCodeStudioViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.CSharpCodeStudioViewModel;

namespace CSharpEditorPlugin.Tests;

/// <summary>Files changed outside the studio (git, another editor) show up by themselves, and the studio's own writes don't.</summary>
public class ExternalChangeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FryPDF_ExternalChangeTests_" + Guid.NewGuid().ToString("N"));
    private readonly LocalScriptStorageService _storage;

    public ExternalChangeTests()
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

    [Theory]
    [InlineData("/work/.git/config", true)]
    [InlineData("/work/src/node_modules/left-pad/index.js", true)]
    [InlineData("/work/tools/__pycache__/x.pyc", true)]
    [InlineData("/work/.venv/lib/site.py", true)]
    [InlineData("/work/node_modules", false)] // the folder itself shows in the Explorer (closed): it appearing matters
    [InlineData("/work/bin", false)]
    [InlineData("/work/src/App.cs", false)]
    [InlineData("/work/docs/git-notes.md", false)]
    [InlineData("/elsewhere/.git/config", false)]
    public void ChangesInsideFoldersTheExplorerLeavesOut_AreRecognisedByNameAlone(string path, bool expected)
    {
        if (Path.DirectorySeparatorChar != '/')
        {
            path = path.Replace('/', Path.DirectorySeparatorChar);
        }

        var root = Path.DirectorySeparatorChar == '/' ? "/work" : "\\work";
        Assert.Equal(expected, WorkspaceWalker.IsInsideSkippedFolder(path, root));
    }

    [Fact]
    public async Task AFileAddedOutsideTheStudio_MovesTheVersionsAndRaisesTheEvent()
    {
        _storage.StartWatchingForChanges();
        await _storage.LoadWorkspaceSummariesAsync(); // Starts the watcher on the active folder.
        var structure = _storage.StructureVersion;
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _storage.ExternalChangeDetected += () => raised.TrySetResult();

        await File.WriteAllTextAsync(Path.Combine(_storage.LibraryRootPath, "from-git.frycs"), "{}");

        var winner = await Task.WhenAny(raised.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(raised.Task, winner);
        Assert.True(_storage.StructureVersion > structure);
    }

    [Fact]
    public async Task TheStudiosOwnWrites_AreNotReportedBackAsExternalChanges()
    {
        _storage.StartWatchingForChanges();
        await _storage.LoadWorkspaceSummariesAsync();
        var raised = false;
        _storage.ExternalChangeDetected += () => raised = true;

        var script = await _storage.CreateNewScriptAsync("Own");
        script.Code = "// edited";
        await _storage.SaveScriptAsync(script);
        await Task.Delay(TimeSpan.FromSeconds(1.5)); // Long enough for the file system's report plus the settle time.

        Assert.False(raised);
    }

    [Fact]
    public async Task AnOutsideChangeRightAfterAStudioSave_IsStillNoticed()
    {
        _storage.StartWatchingForChanges();
        await _storage.LoadWorkspaceSummariesAsync();
        var script = await _storage.CreateNewScriptAsync("Saved");
        script.Code = "// saved by the studio";
        await _storage.SaveScriptAsync(script);

        var changed = new TaskCompletionSource<IReadOnlyCollection<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _storage.ExternalFilesChanged += paths => changed.TrySetResult(paths);
        var outside = Path.Combine(_storage.LibraryRootPath, "pulled.py");
        await File.WriteAllTextAsync(outside, "print('from git pull')");

        var winner = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(changed.Task, winner);
        var paths = await changed.Task;
        Assert.NotNull(paths);
        Assert.Contains(paths!, p => Path.GetFileName(p) == "pulled.py");
        Assert.DoesNotContain(paths!, p => Path.GetFileName(p) == "Saved.frycs");
    }

    [Fact]
    public async Task RefreshingTheExplorer_KeepsTheFoldersTheUserHadOpenOpen()
    {
        var folder = await _storage.CreateFolderAsync(null, "Kept");
        await _storage.CreateNewScriptAsync("Inside", folderPath: folder);
        var loose = await _storage.CreateNewScriptAsync("Loose");
        var studio = await CreateStudioAsync(loose);
        studio.ExplorerRootItems.Single(i => i.IsDirectory).IsExpanded = true;

        await studio.RefreshExplorerAsync();

        Assert.True((bool)studio.ExplorerRootItems.Single(i => i.IsDirectory).IsExpanded);
        Assert.Contains(studio.ExplorerRows.Rows, r => r.Name == "Inside.frycs");
    }
}
