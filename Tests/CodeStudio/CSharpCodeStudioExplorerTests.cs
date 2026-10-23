using System.Collections.Concurrent;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.Server;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using Xunit;
using CSharpCodeStudioViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.CSharpCodeStudioViewModel;
using ExplorerItemViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer.ExplorerItemViewModel;

namespace CSharpEditorPlugin.Tests;

public class CSharpCodeStudioExplorerTests : IDisposable
{
    private readonly string _testBaseDir;
    private readonly LocalScriptStorageService _testStorage;

    public CSharpCodeStudioExplorerTests()
    {
        _testBaseDir = Path.Combine(Path.GetTempPath(), "FryPDF_CodeStudioExplorerTests_" + Guid.NewGuid().ToString("N"));
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

    // A studio lists its folder in the background when it is made: wait for the listing, so the Explorer has its files.
    private async Task<CSharpCodeStudioViewModel> CreateStudioAsync(ScriptDocumentItem script)
    {
        var studio = new CSharpCodeStudioViewModel(
            script,
            _testStorage,
            new RoslynCompilerService(),
            new ScriptExecutionEngine(),
            backToHubAction: () => { },
            backToHomeAction: () => { });
        await studio.RefreshExplorerAsync();
        return studio;
    }

    [Fact]
    public async Task PopulateExplorerTree_WithLibraryScripts_ListsThemAtRoot()
    {
        var script = await _testStorage.CreateNewScriptAsync("Main Script");
        await _testStorage.CreateNewScriptAsync("Sibling Script");

        var studio = await CreateStudioAsync(script);

        Assert.Contains(studio.ExplorerRootItems, x => !x.IsDirectory && x.Name == "Main Script.frycs");
        Assert.Contains(studio.ExplorerRootItems, x => !x.IsDirectory && x.Name == "Sibling Script.frycs");
    }

    [Fact]
    public async Task TheExplorer_ShowsEverythingInTheFolder_ExceptWhatVsCodeHides_AndNothingFromOutside()
    {
        // As VS Code: every file and folder of the workspace folder; .git, .svn, .hg, CVS, .DS_Store and Thumbs.db hidden;
        // folders the walk doesn't go into (node_modules, bin) shown closed and listed when opened.
        var root = Path.Combine(_testBaseDir, "workspace");
        foreach (var dir in new[] { "src", "node_modules/left-pad", "bin/Debug", ".git/objects", ".frysharp" }) Directory.CreateDirectory(Path.Combine(root, dir));
        foreach (var file in new[] { "src/App.cs", "node_modules/left-pad/index.js", "bin/Debug/app.dll", ".git/config", ".DS_Store", "Thumbs.db",
                     "desktop.ini", "Old.frycsproj", "fry_display.py", "LICENSE", ".gitignore", "notes.txt" })
        {
            File.WriteAllText(Path.Combine(root, file), "x");
        }

        await _testStorage.OpenExternalProjectAsync(root);
        var outsider = await _testStorage.CreateNewScriptAsync("Outsider", folderPath: Path.Combine(_testBaseDir, "elsewhere"));
        var studio = await CreateStudioAsync(outsider);
        await studio.RefreshExplorerAsync();

        var top = studio.ExplorerRootItems.Select(i => i.Name).ToList();
        foreach (var shown in new[] { "src", "node_modules", "bin", ".frysharp", "desktop.ini", "Old.frycsproj", "fry_display.py", "LICENSE", ".gitignore", "notes.txt" })
        {
            Assert.Contains(shown, top);
        }

        foreach (var hidden in new[] { ".git", ".DS_Store", "Thumbs.db", "Outsider.frycs" })
        {
            Assert.DoesNotContain(hidden, top);
        }

        var modules = studio.ExplorerRootItems.Single(i => i.Name == "node_modules");
        Assert.False(modules.ChildrenLoaded);
        modules.IsExpanded = true;
        for (var i = 0; i < 100 && !modules.ChildrenLoaded; i++) await Task.Delay(20);
        var leftPad = Assert.Single(modules.Children);
        Assert.Equal("left-pad", leftPad.Name);
        Assert.True(leftPad.IsDirectory);
        leftPad.IsExpanded = true;
        for (var i = 0; i < 100 && !leftPad.ChildrenLoaded; i++) await Task.Delay(20);
        Assert.Equal("index.js", Assert.Single(leftPad.Children).Name);
    }

    [Fact]
    public async Task PopulateExplorerTree_ForScriptOutsideActiveRoot_DoesNotListIt()
    {
        // The Explorer shows the workspace folder's files only. A document saved outside the active workspace root (here:
        // the still-empty internal library) gets no row, even while it is the open one (it used to be listed at the top).
        var externalDir = Path.Combine(Path.GetTempPath(), "FryPDF_CodeStudioExternalTests_" + Guid.NewGuid().ToString("N"), "MyExternalFolder");
        try
        {
            var script = await _testStorage.CreateNewScriptAsync("External Script", folderPath: externalDir);
            var studio = await CreateStudioAsync(script);

            Assert.DoesNotContain(studio.ExplorerRootItems, x => x.Name == "External Script.frycs");
            Assert.Equal(script.Id, studio.Script.Id); // still open, just not in this folder's list
        }
        finally
        {
            var root = Path.GetDirectoryName(externalDir)!;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PopulateExplorerTree_WithUnrelatedExternalScript_DoesNotShowIt()
    {
        var relevantDir = Path.Combine(Path.GetTempPath(), "FryPDF_CodeStudioExternalTests_" + Guid.NewGuid().ToString("N"), "hello");
        var unrelatedDir = Path.Combine(Path.GetTempPath(), "FryPDF_CodeStudioExternalTests_" + Guid.NewGuid().ToString("N"), "unrelated");
        try
        {
            var openScript = await _testStorage.CreateNewScriptAsync("Open One", folderPath: relevantDir);
            await _testStorage.CreateNewScriptAsync("Untouched", folderPath: unrelatedDir);

            var studio = await CreateStudioAsync(openScript);

            // Neither external folder is the active workspace root, so neither document is listed.
            Assert.DoesNotContain(studio.ExplorerRootItems, x => x.Name is "Open One.frycs" or "Untouched.frycs");
        }
        finally
        {
            foreach (var dir in new[] { relevantDir, unrelatedDir })
            {
                var root = Path.GetDirectoryName(dir)!;
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SwitchToScriptAsync_ToDifferentScript_UpdatesScriptAndCodeAndFiresReloadEvent()
    {
        var first = await _testStorage.CreateNewScriptAsync("First Script");
        first.Code = "// first script code";
        await _testStorage.SaveScriptAsync(first);

        var second = await _testStorage.CreateNewScriptAsync("Second Script");
        second.Code = "// second script code";
        await _testStorage.SaveScriptAsync(second);

        var studio = await CreateStudioAsync(first);
        studio.Code = "// edited in memory, unsaved";

        var reloadFired = false;
        studio.RequestReloadEditorText += () => reloadFired = true;

        var secondItem = studio.ExplorerRootItems.Single(x => x.Name == "Second Script.frycs");
        await studio.SwitchToScriptAsync(secondItem);

        Assert.Equal(second.Id, (string?)studio.Script.Id);
        Assert.Equal((string?)"// second script code", (string?)studio.Code);
        Assert.True(reloadFired);

        var reloadedFirst = await _testStorage.LoadScriptAsync(first.Id);
        Assert.Equal("// edited in memory, unsaved", reloadedFirst!.Code);
    }

    [Fact]
    public async Task SwitchToScriptAsync_ToAlreadyOpenScript_DoesNothingAndDoesNotFireReload()
    {
        var script = await _testStorage.CreateNewScriptAsync("Solo Script");
        var studio = await CreateStudioAsync(script);

        var reloadFired = false;
        studio.RequestReloadEditorText += () => reloadFired = true;

        var sameItem = studio.ExplorerRootItems.Single(x => x.Name == "Solo Script.frycs");
        await studio.SwitchToScriptAsync(sameItem);

        Assert.False(reloadFired);
    }

    [Fact]
    public async Task NewScriptCommand_WithNoSelection_CreatesAtLibraryRootAndSwitchesToIt()
    {
        var initial = await _testStorage.CreateNewScriptAsync("Initial Script");
        var studio = await CreateStudioAsync(initial);

        await studio.NewScript();

        Assert.NotEqual<string>(initial.Id, studio.Script.Id);
        Assert.Contains(studio.ExplorerRootItems, x => x.DocumentId == studio.Script.Id);

        var summaries = await _testStorage.LoadWorkspaceSummariesAsync();
        var created = summaries.Single(s => s.Id == studio.Script.Id);
        Assert.True(string.IsNullOrEmpty(created.FolderPath));
    }

    [Fact]
    public async Task DeleteExplorerItemAsync_OnFolderInsideOpenedExternalRoot_DeletesItLikeAnyOtherFolder()
    {
        // Once a folder is opened as the active workspace root, every real folder inside it is fully
        // manageable — there's no synthetic "external wrapper" node left to protect from deletion.
        var externalDir = Path.Combine(Path.GetTempPath(), "FryPDF_CodeStudioExternalTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalDir);
        try
        {
            await _testStorage.OpenExternalProjectAsync(externalDir);
            var subFolder = await _testStorage.CreateFolderAsync(null, "SubFolder");
            var script = await _testStorage.CreateNewScriptAsync("Inside", folderPath: subFolder);
            var studio = await CreateStudioAsync(script);

            var folderNode = studio.ExplorerRootItems.Single(x => x.IsDirectory);
            Assert.True(folderNode.IsManageableDirectory);

            await studio.DeleteExplorerItemAsync(folderNode);

            Assert.False(Directory.Exists(Path.Combine(externalDir, "SubFolder")));
            Assert.DoesNotContain(studio.ExplorerRootItems, x => ReferenceEquals(x, folderNode));
        }
        finally
        {
            if (Directory.Exists(externalDir)) Directory.Delete(externalDir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteExplorerItemAsync_LastScriptInFolder_FolderStaysVisibleAndEmpty()
    {
        // Folders now always reflect a real directory on disk, so — unlike the old synthetic external
        // group node — an emptied folder doesn't disappear from the tree.
        var openScript = await _testStorage.CreateNewScriptAsync("Kept Open");
        var folder = await _testStorage.CreateFolderAsync(null, "SoloFolder");
        await _testStorage.CreateNewScriptAsync("Only One", folderPath: folder);
        var studio = await CreateStudioAsync(openScript);

        var summaries = await _testStorage.LoadWorkspaceSummariesAsync();
        var docId = summaries.Single(s => s.Title == "Only One").Id;
        var tempItem = new ExplorerItemViewModel { DocumentId = docId, IsDirectory = false };
        await studio.SwitchToScriptAsync(tempItem);

        var folderNode = studio.ExplorerRootItems.Single(x => x.IsDirectory && x.Name == "SoloFolder");
        var docItem = folderNode.Children.Single();

        await studio.DeleteExplorerItemAsync(docItem);

        Assert.Contains(studio.ExplorerRootItems, x => x.IsDirectory && x.Name == "SoloFolder");
    }

    [Fact]
    public async Task DuplicateExplorerItemAsync_ForDocumentInOpenedExternalRoot_KeepsCopyInSameFolder()
    {
        var externalDir = Path.Combine(Path.GetTempPath(), "FryPDF_CodeStudioExternalTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalDir);
        try
        {
            await _testStorage.OpenExternalProjectAsync(externalDir);
            var script = await _testStorage.CreateNewScriptAsync("Original");
            var studio = await CreateStudioAsync(script);
            var originalItem = studio.ExplorerRootItems.Single(x => !x.IsDirectory);

            await studio.DuplicateExplorerItemAsync(originalItem);

            var summaries = await _testStorage.LoadWorkspaceSummariesAsync();
            var copy = Assert.Single(summaries, s => s.Title == "Original Copy");
            Assert.Equal(string.Empty, copy.FolderPath);
            Assert.True(File.Exists(Path.Combine(externalDir, "Original Copy.frycs")));
        }
        finally
        {
            if (Directory.Exists(externalDir)) Directory.Delete(externalDir, recursive: true);
        }
    }

    [Fact]
    public async Task OnItemRenamedAsync_ForCurrentlyOpenScript_UpdatesScriptTitleAndPersists()
    {
        var script = await _testStorage.CreateNewScriptAsync("Old Name");
        var studio = await CreateStudioAsync(script);
        var item = studio.ExplorerRootItems.Single(x => x.DocumentId == script.Id);

        item.Name = "New Name.frycs";
        await studio.OnItemRenamedAsync(item);

        Assert.Equal((string?)"New Name", (string?)studio.Script.Title);

        var reloaded = await _testStorage.LoadScriptAsync(script.Id);
        Assert.Equal("New Name", reloaded!.Title);
    }

    [Fact]
    public async Task UpdateActiveScriptAsync_OnSingleThreadedUiLikeContext_DoesNotDeadlock()
    {
        var externalDir = Path.Combine(Path.GetTempPath(), "FryPDF_CodeStudioDeadlockTests_" + Guid.NewGuid().ToString("N"), "ExtFolder");
        try
        {
            var first = await _testStorage.CreateNewScriptAsync("First", folderPath: externalDir);
            var second = await _testStorage.CreateNewScriptAsync("Second", folderPath: externalDir);
            var studio = await CreateStudioAsync(first);

            var pump = new SingleThreadSynchronizationContext();
            var completed = new TaskCompletionSource<bool>();

            var pumpThread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(pump);
                pump.Post(async _ =>
                {
                    try
                    {
                        await studio.UpdateActiveScriptAsync(second);
                        completed.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        completed.SetException(ex);
                    }
                    finally
                    {
                        pump.Complete();
                    }
                }, null);
                pump.RunOnCurrentThread();
            })
            { IsBackground = true };
            pumpThread.Start();

            var finished = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(ReferenceEquals(finished, completed.Task),
                "UpdateActiveScriptAsync deadlocked on a UI-like single-threaded SynchronizationContext.");
            await completed.Task;

            Assert.Equal(second.Id, (string?)studio.Script.Id);
        }
        finally
        {
            var root = Path.GetDirectoryName(externalDir)!;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenExternalProjectAsync_ValidScriptFile_UpdatesActiveScriptAndExplorer()
    {
        var externalDir = Path.Combine(Path.GetTempPath(), "FryPDF_CodeStudioOpenTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalDir);
        try
        {
            var externalFile = Path.Combine(externalDir, "ImportedScript.cs");
            await File.WriteAllTextAsync(externalFile, "System.Console.WriteLine(\"Imported!\");");

            var initialScript = await _testStorage.CreateNewScriptAsync("Initial");
            var studio = await CreateStudioAsync(initialScript);

            await studio.OpenExternalProjectAsync(externalFile);

            Assert.Equal((string?)"ImportedScript", (string?)studio.Script.Title);
            Assert.Contains((string)"Imported!", (string?)studio.Code);
        }
        finally
        {
            if (Directory.Exists(externalDir)) Directory.Delete(externalDir, recursive: true);
        }
    }

    [Fact]
    public async Task NewNotebookCommand_CreatesNotebookOnDisk_AndInvokesOpenAction()
    {
        NotebookDocumentItem? openedNotebook = null;
        var script = await _testStorage.CreateNewScriptAsync("TestScript");
        var studio = new CSharpCodeStudioViewModel(
            script,
            _testStorage,
            new RoslynCompilerService(),
            new ScriptExecutionEngine(),
            backToHubAction: () => { },
            openNotebookAction: nb => openedNotebook = nb);

        await studio.NewNotebookCommand.ExecuteAsync(null);

        Assert.NotNull(openedNotebook);
        Assert.StartsWith("Notebook_", openedNotebook!.Title);
        var files = Directory.GetFiles(_testStorage.ActiveWorkspaceRootPath, "*.frynb");
        Assert.Single(files);
    }

    [Fact]
    public async Task NewServerCommand_CreatesServerOnDisk_AndInvokesOpenAction()
    {
        FryServerDocumentItem? openedServer = null;
        var script = await _testStorage.CreateNewScriptAsync("TestScript");
        var studio = new CSharpCodeStudioViewModel(
            script,
            _testStorage,
            new RoslynCompilerService(),
            new ScriptExecutionEngine(),
            backToHubAction: () => { },
            openServerAction: s => openedServer = s);

        await studio.NewServerCommand.ExecuteAsync(null);

        Assert.NotNull(openedServer);
        Assert.StartsWith("Server_", openedServer!.Title);
        var files = Directory.GetFiles(_testStorage.ActiveWorkspaceRootPath, "*.fryserver");
        Assert.Single(files);
    }

    [Fact]
    public async Task NewNotebookUnderItemAsync_CreatesNotebookInTargetFolder()
    {
        var script = await _testStorage.CreateNewScriptAsync("TestScript");
        var studio = await CreateStudioAsync(script);
        var subFolder = Path.Combine(_testStorage.ActiveWorkspaceRootPath, "analytics");
        Directory.CreateDirectory(subFolder);
        await studio.RefreshExplorerAsync();

        var folderItem = studio.ExplorerRootItems.FirstOrDefault(x => x.Name == "analytics");
        Assert.NotNull(folderItem);

        await studio.NewNotebookUnderItemAsync(folderItem!);

        var files = Directory.GetFiles(subFolder, "*.frynb");
        Assert.Single(files);
    }

    [Fact]
    public async Task NewServerUnderItemAsync_CreatesServerInTargetFolder()
    {
        var script = await _testStorage.CreateNewScriptAsync("TestScript");
        var studio = await CreateStudioAsync(script);
        var subFolder = Path.Combine(_testStorage.ActiveWorkspaceRootPath, "services");
        Directory.CreateDirectory(subFolder);
        await studio.RefreshExplorerAsync();

        var folderItem = studio.ExplorerRootItems.FirstOrDefault(x => x.Name == "services");
        Assert.NotNull(folderItem);

        await studio.NewServerUnderItemAsync(folderItem!);

        var files = Directory.GetFiles(subFolder, "*.fryserver");
        Assert.Single(files);
    }

    [Fact]
    public void ExplorerItemViewModel_RequestNewNotebookAndServer_FiresCallbacks()
    {
        var item = new ExplorerItemViewModel { Name = "testFolder", IsDirectory = true };
        var notebookFired = false;
        var serverFired = false;

        item.OnNewNotebookRequested = _ => notebookFired = true;
        item.OnNewServerRequested = _ => serverFired = true;

        item.RequestNewNotebookCommand.Execute(null);
        item.RequestNewServerCommand.Execute(null);

        Assert.True(notebookFired);
        Assert.True(serverFired);
    }

    [Fact]
    public void IconForFolder_ResolvesSemanticIconsAndAccents()
    {
        // Settings/Config
        var (fryIcon, fryColor) = ExplorerItemViewModel.IconForFolder(".frysharp", isExpanded: false);
        Assert.Equal("FolderCogOutline", fryIcon);
        Assert.Equal("#8B949E", fryColor);

        // Assets
        var (assetsIcon, assetsColor) = ExplorerItemViewModel.IconForFolder("assets", isExpanded: false);
        Assert.Equal("FolderImage", assetsIcon);
        Assert.Equal("#C586C0", assetsColor);

        // Data
        var (dataIcon, dataColor) = ExplorerItemViewModel.IconForFolder("data", isExpanded: false);
        Assert.Equal("FolderTable", dataIcon);
        Assert.Equal("#75D59A", dataColor);

        // Documentation
        var (docsIcon, docsColor) = ExplorerItemViewModel.IconForFolder("docs", isExpanded: false);
        Assert.Equal("FolderInformationOutline", docsIcon);
        Assert.Equal("#4EC9B0", docsColor);

        // Scripts
        var (scriptsIcon, scriptsColor) = ExplorerItemViewModel.IconForFolder("scripts", isExpanded: false);
        Assert.Equal("FolderWrenchOutline", scriptsIcon);
        Assert.Equal("#E5C07B", scriptsColor);

        // Tests
        var (testsIcon, testsColor) = ExplorerItemViewModel.IconForFolder("tests", isExpanded: false);
        Assert.Equal("FolderEyeOutline", testsIcon);
        Assert.Equal("#7EE787", testsColor);

        // Source
        var (srcIcon, srcColor) = ExplorerItemViewModel.IconForFolder("src", isExpanded: false);
        Assert.Equal("FolderCodeOutline", srcIcon);
        Assert.Equal("#58A6FF", srcColor);

        // Packages
        var (pkgIcon, pkgColor) = ExplorerItemViewModel.IconForFolder("node_modules", isExpanded: false);
        Assert.Equal("FolderZipOutline", pkgIcon);
        Assert.Equal("#CE9178", pkgColor);

        // Standard closed folder
        var (normalClosed, closedColor) = ExplorerItemViewModel.IconForFolder("MyFolder", isExpanded: false);
        Assert.Equal("FolderOutline", normalClosed);

        // Standard open folder
        var (normalOpen, openColor) = ExplorerItemViewModel.IconForFolder("MyFolder", isExpanded: true);
        Assert.Equal("FolderOpenOutline", normalOpen);
        Assert.Equal("#F59E0B", openColor);
    }

    [Fact]
    public void ExplorerItemViewModel_SelectionAndTooltips_WorkForDirectories()
    {
        var folder = new ExplorerItemViewModel
        {
            Name = "DataFolder",
            IsDirectory = true,
            FullPath = "workspace/DataFolder"
        };

        var child1 = new ExplorerItemViewModel { Name = "file1.cs", FullPath = "workspace/DataFolder/file1.cs" };
        var child2 = new ExplorerItemViewModel { Name = "file2.cs", FullPath = "workspace/DataFolder/file2.cs" };
        folder.Children.Add(child1);
        folder.Children.Add(child2);

        Assert.Equal(2, folder.ItemCount);
        Assert.Equal("2", folder.ItemCountBadge);
        Assert.True(folder.ShowCountBadge);
        Assert.Contains("DataFolder (2 items)", folder.ToolTipText);
        Assert.Contains("workspace/DataFolder", folder.ToolTipText);

        folder.IsExpanded = true;
        Assert.False(folder.ShowCountBadge);
        Assert.False(folder.IsEmptyExpandedFolder);

        var emptyFolder = new ExplorerItemViewModel
        {
            Name = "EmptyDir",
            IsDirectory = true,
            IsExpanded = true
        };
        Assert.True(emptyFolder.IsEmptyExpandedFolder);

        // Root files must never show duplicated double lines
        var rootFile = new ExplorerItemViewModel { Name = "New Server.fryserver", FullPath = "New Server.fryserver" };
        Assert.Equal("New Server.fryserver", rootFile.ToolTipText);
        Assert.DoesNotContain("\n", rootFile.ToolTipText);

        // Nested files show their path
        Assert.Equal("workspace/DataFolder/file1.cs", child1.ToolTipText);
    }

    [Fact]
    public async Task ClickingFolderInStudio_SelectsFolder()
    {
        var root = Path.Combine(_testBaseDir, "folder_select_ws");
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs", "readme.md"), "hello");
        await _testStorage.OpenExternalProjectAsync(root);

        var script = await _testStorage.CreateNewScriptAsync("Temp");
        var studio = await CreateStudioAsync(script);

        var docsFolder = studio.ExplorerRootItems.FirstOrDefault(x => x.Name == "docs" && x.IsDirectory);
        Assert.NotNull(docsFolder);

        // Click folder
        docsFolder!.ToggleExpandCommand.Execute(null);

        Assert.True(docsFolder.IsSelected);
        Assert.Equal(docsFolder, studio.SelectedExplorerItem);
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void RunOnCurrentThread()
        {
            foreach (var workItem in _queue.GetConsumingEnumerable())
            {
                workItem.Callback(workItem.State);
            }
        }

        public void Complete() => _queue.CompleteAdding();
    }
}
