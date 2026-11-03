using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;
using Xunit;
using CSharpCodeStudioViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.CSharpCodeStudioViewModel;

namespace CSharpEditorPlugin.Tests.Services;

/// <summary>Saving never writes into the wrong file, never leaves a file half-written, and deleting goes to the Trash.</summary>
public class StorageSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FrySharp_StorageSafety_" + Guid.NewGuid().ToString("N"));
    private readonly LocalScriptStorageService _storage;

    public StorageSafetyTests() => _storage = new LocalScriptStorageService(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private const string Jupyter =
        """{"cells":[{"cell_type":"code","source":["print('SOURCE')"],"metadata":{},"outputs":[],"execution_count":null}],"metadata":{},"nbformat":4,"nbformat_minor":5}""";

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_storage.LibraryRootPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task TwoNotebooksWithOneName_InDifferentFolders_EachSaveIntoItsOwnFile()
    {
        var first = Write(Path.Combine("a", "analysis.ipynb"), Jupyter.Replace("SOURCE", "first"));
        var second = Write(Path.Combine("b", "analysis.ipynb"), Jupyter.Replace("SOURCE", "second"));
        await _storage.LoadWorkspaceSummariesAsync();

        var notebook = await _storage.LoadNotebookAsync(second);
        Assert.NotNull(notebook);
        notebook!.Cells[0].Source = "print('edited second')";
        Assert.True(await _storage.SaveNotebookAsync(notebook));

        Assert.Contains("first", File.ReadAllText(first));
        Assert.DoesNotContain("edited", File.ReadAllText(first));
        Assert.Contains("edited second", File.ReadAllText(second));
    }

    [Fact]
    public async Task ANotebookOpenedByItsExplorerId_SavesBackToItsFile_InItsFormat()
    {
        var path = Write(Path.Combine("nested", "data.ipynb"), Jupyter);
        var summary = (await _storage.LoadWorkspaceSummariesAsync()).Single(s => s.FileName == "data.ipynb");

        var notebook = await _storage.LoadNotebookAsync(summary.Id);
        Assert.NotNull(notebook);
        Assert.Equal(summary.Id, notebook!.Id);
        notebook.Cells[0].Source = "print('saved back')";
        Assert.True(await _storage.SaveNotebookAsync(notebook));

        var text = File.ReadAllText(path);
        Assert.Contains("saved back", text);
        Assert.Contains("nbformat", text);
        Assert.Empty(Directory.GetFiles(_storage.LibraryRootPath, "*.frynb", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SaveNotebookAs_WritesTheChosenFile_AndLeavesTheOriginal()
    {
        var original = Write("original.ipynb", Jupyter);
        var notebook = await _storage.LoadNotebookAsync(original);
        notebook!.Cells[0].Source = "print('copy')";
        var target = Path.Combine(_root, "elsewhere", "copy.ipynb");

        var written = await _storage.SaveNotebookAsAsync(notebook, target);

        Assert.Equal(Path.GetFullPath(target), written);
        Assert.Contains("copy", File.ReadAllText(target));
        Assert.DoesNotContain("'copy'", File.ReadAllText(original));
        Assert.False(Directory.Exists(Path.Combine(_root, "elsewhere", "copy.ipynb", "")) && !File.Exists(target));
    }

    [Fact]
    public async Task SaveSourceFileAs_ReturnsTheNewFileAsADocument()
    {
        var target = Path.Combine(_root, "elsewhere", "copy.py");

        var document = await _storage.SaveSourceFileAsAsync(target, "print('hi')");

        Assert.NotNull(document);
        Assert.Equal(Path.GetFullPath(target), document!.SourceFilePath);
        Assert.Equal("print('hi')", File.ReadAllText(target));
    }

    [Fact]
    public async Task Saving_LeavesNoTemporaryFilesBehind()
    {
        var script = await _storage.CreateNewScriptAsync("Atomic");
        script.Code = "// saved";
        Assert.True(await _storage.SaveScriptAsync(script));

        Assert.Empty(Directory.GetFiles(_storage.LibraryRootPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Deleting_MovesToTheTrash_WhenThereIsOne()
    {
        var trash = new RecordingRecycleBin();
        _storage.RecycleBin = trash;
        var script = await _storage.CreateNewScriptAsync("Doomed");
        var folder = await _storage.CreateFolderAsync(null, "Old");

        Assert.True(await _storage.MoveItemToTrashAsync(script.Id));
        Assert.True(await _storage.MoveFolderToTrashAsync(folder));

        Assert.Equal(2, trash.Trashed.Count);
        Assert.Contains(trash.Trashed, p => p.EndsWith("Doomed.frycs", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_storage.LibraryRootPath, "Old")));
    }

    [Fact]
    public async Task WithoutATrash_NothingIsDeletedByTheTrashCall()
    {
        var script = await _storage.CreateNewScriptAsync("Kept");

        Assert.False(await _storage.MoveItemToTrashAsync(script.Id));

        Assert.NotNull(await _storage.LoadScriptAsync(script.Id));
    }

    [Fact]
    public async Task AnOpenFileChangedOnDisk_ReloadsWhenClean_AndKeepsTheEditsWhenNot()
    {
        var path = Write("main.py", "print('v1')");
        await _storage.LoadWorkspaceSummariesAsync();
        var studio = new CSharpCodeStudioViewModel(new ScriptDocumentItem { Title = "Scratch" }, _storage, new RoslynCompilerService(),
            new ScriptExecutionEngine(), () => { }, () => { }, postToUiThread: a => a());
        await studio.OpenWorkspaceFileAsync(path);
        Assert.Equal("print('v1')", studio.Code);

        File.WriteAllText(path, "print('v2')");
        await studio.ReloadChangedDocumentsAsync([path]);
        Assert.Equal("print('v2')", studio.Code);
        Assert.False(studio.HasChangedOnDiskNotice);

        studio.Code = "print('mine')";
        File.WriteAllText(path, "print('v3')");
        await studio.ReloadChangedDocumentsAsync([path]);
        Assert.Equal("print('mine')", studio.Code);
        Assert.True(studio.HasChangedOnDiskNotice);

        // The studio's own save (leaving the page) must not write over what changed on disk.
        studio.BackToHubCommand.Execute(null);
        Assert.Equal("print('v3')", File.ReadAllText(path));
    }

    private sealed class RecordingRecycleBin : IRecycleBin
    {
        public List<string> Trashed { get; } = new();

        public Task<bool> TryMoveToTrashAsync(string path, CancellationToken ct = default)
        {
            Trashed.Add(path);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return Task.FromResult(true);
        }
    }
}
