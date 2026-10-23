using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.Server;
using PdfEditorApp.Plugins.CSharpEditor.Services.Activities;
using PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;

public partial class CSharpCodeStudioViewModel
{
    public ObservableCollection<Explorer.ExplorerItemViewModel> ExplorerRootItems { get; } = new();

    private ExplorerRowList? _explorerRows;

    /// <summary>The Explorer's visible rows as one flat list: what the (virtualized) Explorer binds to.</summary>
    public ExplorerRowList ExplorerRows => _explorerRows ??= new ExplorerRowList(ExplorerRootItems);

    /// <summary>True when the workspace folder holds more files than the Explorer lists; the panel then says so rather than looking complete.</summary>
    [ObservableProperty]
    private bool _isExplorerTruncated;

    public string ExplorerTruncationText =>
        $"Showing the first {_storageService.WorkspaceFileLimit:N0} files. This folder has more: press Ctrl+P to open any file by name, or open a subfolder.";

    private ExplorerRefresher? _explorerRefresher;

    // When the tree is listed: never while the studio is built, but on the first visit, from the host once the engine
    // is warm, and when the workspace changed (see ExplorerRefresher).
    private ExplorerRefresher ExplorerRefresh => _explorerRefresher ??= new ExplorerRefresher(_storageService, _activities, ShowExplorerListing);

    private LazyExplorerTree? _lazyExplorer;

    // The Explorer of a workspace too big to list at once: one folder at a time (see LazyExplorerTree).
    private LazyExplorerTree LazyExplorer => _lazyExplorer ??= new LazyExplorerTree(
        _storageService,
        ExplorerRootItems,
        ExplorerRows,
        (name, path, parent) => CreateFolderItem(name, path, isExpanded: false, parent: parent),
        (summary, name, fullPath, parent) => CreateFileItem(name, summary.Id, parent, fullPath, summary.IsSourceFile ? _languages.Registry.Get(summary.LanguageId) : null));

    // Draws what the storage listed: the whole workspace, or (when it is too big) its top folder, the rest coming as folders open.
    private void ShowExplorerListing(WorkspaceListing listing)
    {
        if (!listing.IsPartial)
        {
            LazyExplorer.Deactivate();
            RebuildExplorerTree(listing.FolderPaths, listing.Items, listing.ClosedFolders);
            return;
        }

        var openFolders = Explorer.ExplorerItemViewModel.ExpandedFolderPaths(ExplorerRootItems);
        LazyExplorer.Build(listing, openFolders);
        // Only the top folder itself can be cut off here (a nested one says so in its own rows).
        IsExplorerTruncated = listing.IsTruncated;
        HighlightExplorerItem(Script?.Id, Script?.Title, Script?.SourceFilePath);
    }

    /// <summary>Lists the workspace and redraws the Explorer, whether or not anything changed (the Refresh button).</summary>
    [RelayCommand]
    public Task RefreshExplorerAsync() => ExplorerRefresh.RefreshAsync();

    /// <summary>
    /// Brings the Explorer up to date without rescanning the workspace when nothing in it has changed: switching a tab
    /// or coming back to this page only moves the highlight. The Explorer lists the workspace folder's files only: a
    /// document opened from elsewhere gets no row of its own.
    /// </summary>
    public async Task RefreshExplorerIfStaleAsync()
    {
        if (await ExplorerRefresh.RefreshIfStaleAsync()) return;

        HighlightExplorerItem(Script?.Id, Script?.Title, Script?.SourceFilePath);
    }

    // A page that is not on screen leaves refreshing to its next visit. Assumed on screen until told otherwise (the
    // studio is also used without a host that says so).
    private bool _isPageActive = true;

    // Shown again after other pages (or other programs) may have added, removed or renamed items: catch up.
    public void OnActivated()
    {
        _isPageActive = true;
        RefreshExplorerIfStaleAsync().FireAndForget(_activities, "Listing the workspace");

        // Reading the file index starts (or refreshes) its background walk, so Go to File is ready when it is used.
        _ = _storageService.FileIndex;
    }

    // Go to File: opens any file of the workspace by its path, whether or not the Explorer has drawn it.
    public async Task OpenWorkspaceFileAsync(string fullPath)
    {
        var token = _fileOpen.Begin();
        using var activity = _activities.Start(new ActivityOptions($"Opening {Path.GetFileName(fullPath)}", ActivityLocation.Editor), token);
        var result = await Task.Run(() => _storageService.OpenExternalProjectAsync(fullPath));
        if (token.IsCancellationRequested) return;
        if (!result.Success || string.IsNullOrEmpty(result.PrimaryDocumentId))
        {
            CompilerStatusText = result.Message;
            return;
        }

        if (result.PrimaryDocumentKind == WorkspaceItemKind.Server)
        {
            if (_openServerAction != null && await Task.Run(() => _storageService.LoadServerDocumentAsync(result.PrimaryDocumentId)) is { } server && !token.IsCancellationRequested)
            {
                _openServerAction.Invoke(server);
            }

            return;
        }

        if (result.PrimaryDocumentKind == WorkspaceItemKind.Notebook)
        {
            if (_openNotebookAction != null && await Task.Run(() => _storageService.LoadNotebookAsync(result.PrimaryDocumentId)) is { } notebook && !token.IsCancellationRequested)
            {
                _openNotebookAction.Invoke(notebook);
            }

            return;
        }

        await LoadAndShowScriptAsync(result.PrimaryDocumentId, token);
    }

    // The common end of every "open this file" path: load it off the UI thread (joining a load of the same file that is
    // already running), then show it, unless a newer open has started meanwhile.
    private async Task LoadAndShowScriptAsync(string documentId, CancellationToken token)
    {
        var loaded = await _scriptLoads.RunAsync(documentId, () => Task.Run(() => _storageService.LoadScriptAsync(documentId)));
        if (loaded == null || token.IsCancellationRequested) return;
        await SaveDocumentAsync(userAsked: false);
        if (token.IsCancellationRequested) return;
        await UpdateActiveScriptAsync(loaded);
    }

    public void OnDeactivated() => _isPageActive = false;

    private void RebuildExplorerTree(IReadOnlyList<string> folderPaths, IReadOnlyList<WorkspaceItemSummary> summaries, IReadOnlyList<string> closedFolders)
    {
        // Thousands of single changes below; the flat row list is rebuilt once, when the scope ends.
        using var rowsScope = ExplorerRows.Suspend();

        // A refresh keeps the folders the user had open open.
        var expandedFolders = Explorer.ExplorerItemViewModel.ExpandedFolderPaths(ExplorerRootItems);
        ExplorerRootItems.Clear();
        var folderNodes = new Dictionary<string, Explorer.ExplorerItemViewModel>(StringComparer.OrdinalIgnoreCase);
        // The file names already in each folder (the root under its own key): a name-by-name scan of the siblings for every
        // file would be quadratic in the size of a folder.
        var rootKey = new object();
        var namesByFolder = new Dictionary<object, HashSet<string>>();

        Explorer.ExplorerItemViewModel? GetOrCreateFolder(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;
            if (folderNodes.TryGetValue(relativePath, out var existing)) return existing;

            var lastSlash = relativePath.LastIndexOf('/');
            var name = lastSlash >= 0 ? relativePath[(lastSlash + 1)..] : relativePath;
            var parentPath = lastSlash >= 0 ? relativePath[..lastSlash] : string.Empty;
            var parent = GetOrCreateFolder(parentPath);

            var node = CreateFolderItem(name, relativePath, isExpanded: expandedFolders.Contains(relativePath), parent: parent);
            AddToTree(parent, node);
            folderNodes[relativePath] = node;
            return node;
        }

        foreach (var path in folderPaths.OrderBy(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            GetOrCreateFolder(path);
        }

        // The folders the walk doesn't go into (node_modules, bin, .venv): shown closed and listed when opened, as in VS Code.
        foreach (var path in closedFolders)
        {
            if (GetOrCreateFolder(path) is { } closed) LazyExplorer.MakeUnlisted(closed);
        }

        foreach (var s in summaries.OrderBy(x => x.ExplorerName, StringComparer.OrdinalIgnoreCase))
        {
            var name = s.ExplorerName; // the file's own name: copies with one title are all listed

            var parent = GetOrCreateFolder(s.FolderPath);
            var fullPath = string.IsNullOrEmpty(s.FolderPath) ? name : $"{s.FolderPath}/{name}";

            var folderKey = (object?)parent ?? rootKey;
            if (!namesByFolder.TryGetValue(folderKey, out var names))
            {
                namesByFolder[folderKey] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            if (!names.Add(name))
            {
                continue;
            }

            var docItem = CreateFileItem(name, s.Id, parent, fullPath, s.IsSourceFile ? _languages.Registry.Get(s.LanguageId) : null);
            AddToTree(parent, docItem);
        }

        SortExplorerTree(ExplorerRootItems);
        HighlightExplorerItem(Script?.Id, Script?.Title, Script?.SourceFilePath);
        IsExplorerTruncated = _storageService.IsWorkspaceTruncated;
    }

    private void SortExplorerTree(ObservableCollection<Explorer.ExplorerItemViewModel> items)
    {
        using var rowsScope = ExplorerRows.Suspend();

        var sorted = items.OrderByDescending<Explorer.ExplorerItemViewModel, bool>(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (!sorted.SequenceEqual(items))
        {
            items.Clear();
            foreach (var item in sorted) items.Add(item);
        }

        foreach (var folder in items.Where(i => i.IsDirectory))
        {
            SortExplorerTree(folder.Children);
        }
    }

    private void DeselectAll(IEnumerable<Explorer.ExplorerItemViewModel> items)
    {
        foreach (var it in items)
        {
            it.IsSelected = false;
            if (it.Children.Count > 0) DeselectAll(it.Children);
        }
    }

    private void HighlightExplorerItem(string? documentId, string? title = null, string? sourceFilePath = null)
    {
        DeselectAll(ExplorerRootItems);
        if (string.IsNullOrEmpty(documentId) && string.IsNullOrEmpty(sourceFilePath) && string.IsNullOrEmpty(title)) return;

        var match = FindExplorerItem(ExplorerRootItems, documentId, title, sourceFilePath);
        if (match != null)
        {
            match.IsSelected = true;
            var parent = match.Parent;
            while (parent != null)
            {
                parent.IsExpanded = true;
                parent = parent.Parent;
            }
        }
        else if (LazyExplorer.IsActive && !string.IsNullOrEmpty(documentId))
        {
            _ = RevealInLazyExplorerAsync(documentId);
        }
    }

    private Explorer.ExplorerItemViewModel? FindExplorerItem(IEnumerable<Explorer.ExplorerItemViewModel> items, string? documentId, string? title, string? sourceFilePath)
    {
        foreach (var item in items)
        {
            if (!item.IsDirectory)
            {
                if (!string.IsNullOrEmpty(documentId) && string.Equals(item.DocumentId, documentId, StringComparison.OrdinalIgnoreCase))
                    return item;

                if (!string.IsNullOrEmpty(sourceFilePath))
                {
                    if (string.Equals(item.FullPath, sourceFilePath, StringComparison.OrdinalIgnoreCase))
                        return item;
                    try
                    {
                        if (Path.IsPathRooted(item.FullPath) && Path.IsPathRooted(sourceFilePath) &&
                            string.Equals(Path.GetFullPath(item.FullPath), Path.GetFullPath(sourceFilePath), StringComparison.OrdinalIgnoreCase))
                            return item;
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(title))
                {
                    if (string.Equals(item.Name, title, StringComparison.OrdinalIgnoreCase))
                        return item;
                    try
                    {
                        if (string.Equals(Path.GetFileNameWithoutExtension(item.Name), Path.GetFileNameWithoutExtension(title), StringComparison.OrdinalIgnoreCase))
                            return item;
                    }
                    catch { }
                }
            }

            var childMatch = FindExplorerItem(item.Children, documentId, title, sourceFilePath);
            if (childMatch != null) return childMatch;
        }
        return null;
    }

    // A big workspace lists a folder when it is opened: open the folders down to the document, then highlight it.
    private async Task RevealInLazyExplorerAsync(string documentId)
    {
        var item = await LazyExplorer.RevealAsync(documentId);
        if (!string.Equals(Script?.Id, documentId, StringComparison.OrdinalIgnoreCase)) return;

        if (item == null) return; // not in the workspace's folders: the Explorer shows the folder only

        DeselectAll(ExplorerRootItems);
        item.IsSelected = true;
    }

    // The row of a document, opening the folders down to it first when the workspace is listed folder by folder.
    private async Task<Explorer.ExplorerItemViewModel?> FindOrRevealExplorerItemAsync(string documentId)
    {
        var item = FindByDocumentId(ExplorerRootItems, documentId);
        if (item != null || !LazyExplorer.IsActive) return item;

        return await LazyExplorer.RevealAsync(documentId);
    }

    private Explorer.ExplorerItemViewModel? FindByDocumentId(IEnumerable<Explorer.ExplorerItemViewModel> items, string documentId)
    {
        foreach (var item in items)
        {
            if (!item.IsDirectory && string.Equals(item.DocumentId, documentId, StringComparison.OrdinalIgnoreCase)) return item;
            var childMatch = FindByDocumentId(item.Children, documentId);
            if (childMatch != null) return childMatch;
        }
        return null;
    }

    public Explorer.ExplorerItemViewModel? SelectedExplorerItem => FindSelectedItem(ExplorerRootItems);

    public Explorer.ExplorerItemViewModel? FindSelectedItem(IEnumerable<Explorer.ExplorerItemViewModel> items)
    {
        foreach (var item in items)
        {
            if (item.IsSelected) return item;
            var childMatch = FindSelectedItem(item.Children);
            if (childMatch != null) return childMatch;
        }
        return null;
    }

    private void AddToTree(Explorer.ExplorerItemViewModel? parent, Explorer.ExplorerItemViewModel child)
    {
        if (parent != null) parent.Children.Add(child);
        else ExplorerRootItems.Add(child);
    }

    private Explorer.ExplorerItemViewModel CreateFolderItem(string name, string fullPath, bool isExpanded = false, Explorer.ExplorerItemViewModel? parent = null, bool isExternalGroup = false)
    {
        return new Explorer.ExplorerItemViewModel
        {
            Name = name,
            IsDirectory = true,
            IsExternalGroup = isExternalGroup,
            IsExpanded = isExpanded,
            Parent = parent,
            Depth = (parent?.Depth ?? -1) + 1,
            FullPath = fullPath,
            OnItemClicked = OnExplorerItemClicked,
            OnDeleteRequested = DeleteExplorerItem,
            OnNewFileRequested = NewScriptUnderItem,
            OnNewNotebookRequested = NewNotebookUnderItem,
            OnNewServerRequested = NewServerUnderItem,
            OnNewFolderRequested = NewFolderUnderItem,
            OnRenameCommitted = OnItemRenamed,
            OnDuplicateRequested = DuplicateExplorerItem,
            OnCopyPathRequested = CopyItemPath,
            OnCopyRelativePathRequested = CopyItemRelativePath,
            OnRevealInFileManagerRequested = RevealItemInFileManager
        };
    }

    /// <param name="sourceLanguage">The language of a plain source file (main.py), or null for a .frycs/.frynb document.</param>
    private Explorer.ExplorerItemViewModel CreateFileItem(string name, string? documentId, Explorer.ExplorerItemViewModel? parent, string fullPath, ILanguageDefinition? sourceLanguage = null)
    {
        return new Explorer.ExplorerItemViewModel
        {
            Name = name,
            DocumentId = documentId,
            IsDirectory = false,
            IsSourceFile = sourceLanguage != null,
            LanguageIconKind = sourceLanguage?.IconKind,
            LanguageIconColor = sourceLanguage?.AccentHex,
            FileExtension = Path.GetExtension(name),
            Parent = parent,
            Depth = (parent?.Depth ?? -1) + 1,
            FullPath = fullPath,
            OnItemClicked = OnExplorerItemClicked,
            OnDeleteRequested = DeleteExplorerItem,
            OnNewFileRequested = NewScriptUnderItem,
            OnNewNotebookRequested = NewNotebookUnderItem,
            OnNewServerRequested = NewServerUnderItem,
            OnNewFolderRequested = NewFolderUnderItem,
            OnRenameCommitted = OnItemRenamed,
            OnDuplicateRequested = DuplicateExplorerItem,
            OnCopyPathRequested = CopyItemPath,
            OnCopyRelativePathRequested = CopyItemRelativePath,
            OnRevealInFileManagerRequested = RevealItemInFileManager
        };
    }

    private void OnExplorerItemClicked(Explorer.ExplorerItemViewModel item) => _ = SwitchToScriptAsync(item);

    public async Task SwitchToScriptAsync(Explorer.ExplorerItemViewModel item)
    {
        DeselectAll(ExplorerRootItems);
        item.IsSelected = true;

        if (item.IsDirectory)
        {
            return;
        }

        if (string.IsNullOrEmpty(item.DocumentId)) return;

        var token = _fileOpen.Begin();
        using var activity = _activities.Start(new ActivityOptions($"Opening {item.Name}", ActivityLocation.Editor), token);
        if (item.FileExtension.Equals(".fryserver", StringComparison.OrdinalIgnoreCase))
        {
            if (_openServerAction != null)
            {
                var server = await Task.Run(() => _storageService.LoadServerDocumentAsync(item.DocumentId));
                if (token.IsCancellationRequested) return;
                if (server != null)
                {
                    _openServerAction.Invoke(server);
                    return;
                }
            }
        }

        if (item.FileExtension.Equals(".frynb", StringComparison.OrdinalIgnoreCase) ||
            item.FileExtension.Equals(".ipynb", StringComparison.OrdinalIgnoreCase) ||
            item.FileExtension.Equals(".csnb", StringComparison.OrdinalIgnoreCase))
        {
            if (_openNotebookAction != null)
            {
                var nb = await Task.Run(async () => await _storageService.LoadNotebookAsync(item.DocumentId));
                if (nb == null && !string.IsNullOrEmpty(item.FullPath))
                {
                    var candidate = item.FullPath;
                    if (!Path.IsPathRooted(candidate) && !string.IsNullOrEmpty(_storageService.ActiveWorkspaceRootPath))
                    {
                        var full = Path.Combine(_storageService.ActiveWorkspaceRootPath, candidate);
                        if (File.Exists(full)) candidate = full;
                    }
                    if (File.Exists(candidate))
                    {
                        nb = await Task.Run(async () => await _storageService.LoadNotebookAsync(candidate));
                    }
                }

                if (token.IsCancellationRequested) return;
                if (nb != null)
                {
                    _openNotebookAction.Invoke(nb);
                    return;
                }
            }
        }

        if (Script != null && string.Equals(Script.Id, item.DocumentId, StringComparison.OrdinalIgnoreCase))
        {
            HighlightExplorerItem(item.DocumentId, item.Name, item.FullPath);
            return;
        }

        // Already open in a tab: switch to it, nothing to read.
        if (OpenTabs.FirstOrDefault(t => string.Equals(t.Id, item.DocumentId, StringComparison.OrdinalIgnoreCase)) is { } openTab)
        {
            await SaveDocumentAsync(userAsked: false);
            if (token.IsCancellationRequested) return;
            await SwitchToTabAsync(openTab);
            return;
        }

        await LoadAndShowScriptAsync(item.DocumentId, token);
    }

    public void DeleteExplorerItem(Explorer.ExplorerItemViewModel item) => _ = DeleteExplorerItemAsync(item);

    [RelayCommand]
    public async Task DeleteExplorerItemAsync(Explorer.ExplorerItemViewModel item)
    {
        if (item == null || item.IsExternalGroup) return;

        if (item.IsDirectory)
        {
            try
            {
                await _storageService.DeleteFolderAsync(item.FullPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CSharpEditorPlugin] Failed to delete folder '{item.FullPath}': {ex.Message}");
            }
        }
        else if (!string.IsNullOrEmpty(item.DocumentId))
        {
            try
            {
                await _storageService.DeleteItemAsync(item.DocumentId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CSharpEditorPlugin] Failed to delete '{item.DocumentId}': {ex.Message}");
            }
        }

        if (item.Parent != null)
        {
            var parent = item.Parent;
            parent.Children.Remove(item);
            if (parent.IsExternalGroup && parent.Children.Count == 0)
            {
                ExplorerRootItems.Remove(parent);
            }
        }
        else
        {
            ExplorerRootItems.Remove(item);
        }
    }

    [RelayCommand]
    public async Task OpenExternalProjectAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        using var activity = _activities.Start(new ActivityOptions("Opening folder", ActivityLocation.Window) { Detail = Path.GetFileName(path) ?? path, Blocking = true });
        try
        {
            var result = await Task.Run(async () => await _storageService.OpenExternalProjectAsync(path));
            if (!result.Success)
            {
                CompilerStatusText = result.Message;
                return;
            }

            await RefreshExplorerAsync();

            WorkspaceCustomizationLoader.LoadInBackground(_storageService.ActiveWorkspaceRootPath, _activities);

            if (!string.IsNullOrEmpty(result.PrimaryDocumentId))
            {
                await LoadAndShowScriptAsync(result.PrimaryDocumentId, _fileOpen.Begin());
            }

            CompilerStatusText = result.Message;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to open external project '{path}': {ex.Message}");
            CompilerStatusText = $"Error opening project: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task NewScript()
    {
        var selected = FindSelectedItem(ExplorerRootItems);
        var targetFolder = (selected != null && selected.IsDirectory) ? selected : selected?.Parent;

        if (targetFolder != null)
        {
            await NewScriptUnderItemAsync(targetFolder);
            return;
        }

        var timestamp = DateTime.Now.ToString("HHmmss");
        var title = $"Script_{timestamp}";
        ScriptDocumentItem newDoc;
        try
        {
            newDoc = await _storageService.CreateNewScriptAsync(title);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to create script: {ex.Message}");
            return;
        }

        await SaveDocumentAsync(userAsked: false);
        await UpdateActiveScriptAsync(newDoc);
        (await FindOrRevealExplorerItemAsync(newDoc.Id))?.StartRename();
    }

    [RelayCommand]
    public async Task NewNotebook()
    {
        var selected = FindSelectedItem(ExplorerRootItems);
        var targetFolder = (selected != null && selected.IsDirectory) ? selected : selected?.Parent;
        if (targetFolder != null)
        {
            await NewNotebookUnderItemAsync(targetFolder);
            return;
        }

        await NewNotebookCoreAsync(null);
    }

    public void NewNotebookUnderItem(Explorer.ExplorerItemViewModel target) => _ = NewNotebookUnderItemAsync(target);

    [RelayCommand]
    public async Task NewNotebookUnderItemAsync(Explorer.ExplorerItemViewModel target)
    {
        var folder = target.IsDirectory ? target : target.Parent;
        await NewNotebookCoreAsync(folder?.FullPath);
    }

    private async Task NewNotebookCoreAsync(string? folderPath)
    {
        var timestamp = DateTime.Now.ToString("HHmmss");
        var title = $"Notebook_{timestamp}";

        NotebookDocumentItem newNb;
        try
        {
            newNb = await _storageService.CreateNewNotebookAsync(title, folderPath: folderPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to create notebook: {ex.Message}");
            CompilerStatusText = $"⚠️ Couldn't create notebook: {ex.Message}";
            return;
        }

        await RefreshExplorerAsync();

        if (_openNotebookAction != null)
        {
            _openNotebookAction.Invoke(newNb);
        }
        else
        {
            (await FindOrRevealExplorerItemAsync(newNb.Id))?.StartRename();
        }
    }

    [RelayCommand]
    public async Task NewServer()
    {
        var selected = FindSelectedItem(ExplorerRootItems);
        var targetFolder = (selected != null && selected.IsDirectory) ? selected : selected?.Parent;
        if (targetFolder != null)
        {
            await NewServerUnderItemAsync(targetFolder);
            return;
        }

        await NewServerCoreAsync(null);
    }

    public void NewServerUnderItem(Explorer.ExplorerItemViewModel target) => _ = NewServerUnderItemAsync(target);

    [RelayCommand]
    public async Task NewServerUnderItemAsync(Explorer.ExplorerItemViewModel target)
    {
        var folder = target.IsDirectory ? target : target.Parent;
        await NewServerCoreAsync(folder?.FullPath);
    }

    private async Task NewServerCoreAsync(string? folderPath)
    {
        var timestamp = DateTime.Now.ToString("HHmmss");
        var title = $"Server_{timestamp}";

        FryServerDocumentItem newServer;
        try
        {
            newServer = await _storageService.CreateNewServerDocumentAsync(title, folderPath: folderPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to create server: {ex.Message}");
            CompilerStatusText = $"⚠️ Couldn't create server: {ex.Message}";
            return;
        }

        await RefreshExplorerAsync();

        if (_openServerAction != null)
        {
            _openServerAction.Invoke(newServer);
        }
        else
        {
            (await FindOrRevealExplorerItemAsync(newServer.Id))?.StartRename();
        }
    }

    private IReadOnlyList<NewFileOption>? _newFileOptions;

    /// <summary>"New Python File" and so on: one entry per language whose documents are plain source files.</summary>
    public IReadOnlyList<NewFileOption> NewFileOptions => _newFileOptions ??= _languages.Registry.SourceFileLanguages
        .Select(language => new NewFileOption(
            language.Id,
            $"New {language.DisplayName} File",
            language.IconKind,
            language.AccentHex,
            new AsyncRelayCommand<Explorer.ExplorerItemViewModel?>(target => NewSourceFileAsync(language.Id, target))))
        .ToArray();

    /// <summary>Creates a source file (script_HHmmss.py) in <paramref name="target"/>'s folder, or the selected one, opens it and starts renaming it.</summary>
    public async Task NewSourceFileAsync(string languageId, Explorer.ExplorerItemViewModel? target = null)
    {
        target ??= FindSelectedItem(ExplorerRootItems);
        var folder = target == null ? null : target.IsDirectory ? target : target.Parent;

        ScriptDocumentItem? newDoc;
        try
        {
            newDoc = await _storageService.CreateNewSourceFileAsync(languageId, folderPath: folder?.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CompilerStatusText = $"⚠️ Couldn't create the file: {ex.Message}";
            return;
        }

        if (newDoc == null)
        {
            CompilerStatusText = "⚠️ Couldn't create the file.";
            return;
        }

        await SaveDocumentAsync(userAsked: false);
        await UpdateActiveScriptAsync(newDoc);
        (await FindOrRevealExplorerItemAsync(newDoc.Id))?.StartRename();
    }

    [RelayCommand]
    public async Task NewFolder()
    {
        var selected = FindSelectedItem(ExplorerRootItems);
        var targetFolder = (selected != null && selected.IsDirectory) ? selected : selected?.Parent;
        await CreateFolderCoreAsync(targetFolder);
    }

    public void NewScriptUnderItem(Explorer.ExplorerItemViewModel target) => _ = NewScriptUnderItemAsync(target);

    [RelayCommand]
    public async Task NewScriptUnderItemAsync(Explorer.ExplorerItemViewModel target)
    {
        var folder = target.IsDirectory ? target : target.Parent;
        var timestamp = DateTime.Now.ToString("HHmmss");
        var title = $"Script_{timestamp}";
        var folderPath = folder?.FullPath;

        ScriptDocumentItem newDoc;
        try
        {
            newDoc = await _storageService.CreateNewScriptAsync(title, folderPath: folderPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to create script: {ex.Message}");
            return;
        }

        await SaveDocumentAsync(userAsked: false);
        await UpdateActiveScriptAsync(newDoc);

        var newItem = await FindOrRevealExplorerItemAsync(newDoc.Id);
        newItem?.StartRename();
    }

    public void NewFolderUnderItem(Explorer.ExplorerItemViewModel target) => _ = NewFolderUnderItemAsync(target);

    [RelayCommand]
    public async Task NewFolderUnderItemAsync(Explorer.ExplorerItemViewModel target)
    {
        var folder = target.IsDirectory ? target : target.Parent;
        await CreateFolderCoreAsync(folder);
    }

    private async Task CreateFolderCoreAsync(Explorer.ExplorerItemViewModel? parentFolder)
    {
        string newRelativePath;
        try
        {
            newRelativePath = await _storageService.CreateFolderAsync(parentFolder?.FullPath, "New Folder");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to create folder: {ex.Message}");
            return;
        }

        var name = newRelativePath.Contains('/') ? newRelativePath[(newRelativePath.LastIndexOf('/') + 1)..] : newRelativePath;

        // Under a folder that has not been listed yet, listing it finds the new folder on disk: adding a row too would show it twice.
        if (parentFolder is { ChildrenLoaded: false })
        {
            await LazyExplorer.LoadChildrenAsync(parentFolder);
            parentFolder.IsExpanded = true;
            parentFolder.Children.FirstOrDefault(c => c.IsDirectory && string.Equals(c.FullPath, newRelativePath, StringComparison.OrdinalIgnoreCase))?.StartRename();
            return;
        }

        var newFolder = CreateFolderItem(name, newRelativePath, isExpanded: true, parent: parentFolder);
        AddToTree(parentFolder, newFolder);
        if (parentFolder != null) parentFolder.IsExpanded = true;
        newFolder.StartRename();
    }

    public void DuplicateExplorerItem(Explorer.ExplorerItemViewModel item) => _ = DuplicateExplorerItemAsync(item);

    [RelayCommand]
    public async Task DuplicateExplorerItemAsync(Explorer.ExplorerItemViewModel item)
    {
        if (item == null || item.IsDirectory || string.IsNullOrEmpty(item.DocumentId)) return;

        if (item.IsSourceFile)
        {
            await DuplicateSourceFileAsync(item);
            return;
        }

        var parent = item.Parent;
        var originalTitle = item.Name.EndsWith(".frycs", StringComparison.OrdinalIgnoreCase)
            ? item.Name.Substring(0, item.Name.Length - 6)
            : item.Name;
        var copyTitle = $"{originalTitle} Copy";
        var copyFileName = $"{copyTitle}.frycs";

        var isActive = Script != null && string.Equals(Script.Id, item.DocumentId, StringComparison.OrdinalIgnoreCase);
        var origDoc = isActive ? Script : await _storageService.LoadScriptAsync(item.DocumentId);

        var copyDoc = new ScriptDocumentItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = copyTitle,
            Category = origDoc?.Category ?? "Custom",
            Description = origDoc?.Description ?? "",
            ExecutionMode = origDoc?.ExecutionMode ?? "Statements",
            Code = origDoc?.Code ?? string.Empty,
            Notes = origDoc?.Notes ?? string.Empty,
            TestCases = origDoc?.TestCases?
                .Select(tc => new TestCaseItem { Name = tc.Name, Input = tc.Input, ExpectedOutput = tc.ExpectedOutput })
                .ToList() ?? new List<TestCaseItem>(),
            Created = DateTime.UtcNow,
            LastModified = DateTime.UtcNow
        };

        await _storageService.SaveScriptAsync(copyDoc, parent?.FullPath);

        var copyPath = string.IsNullOrEmpty(parent?.FullPath) ? copyFileName : $"{parent!.FullPath}/{copyFileName}";
        var copyItem = CreateFileItem(copyFileName, copyDoc.Id, parent, copyPath);
        AddToTree(parent, copyItem);
        if (parent != null) parent.IsExpanded = true;

        await SwitchToScriptAsync(copyItem);
    }

    // main.py → main_copy.py next to it, with the text as it is in the editor if it's open.
    private async Task DuplicateSourceFileAsync(Explorer.ExplorerItemViewModel item)
    {
        var isActive = string.Equals(Script.Id, item.DocumentId, StringComparison.OrdinalIgnoreCase);
        var original = isActive ? Script : await _storageService.LoadScriptAsync(item.DocumentId!);
        if (original == null) return;

        var copy = await _storageService.CreateNewSourceFileAsync(original.LanguageId, $"{Path.GetFileNameWithoutExtension((string?)item.Name)}_copy", item.Parent?.FullPath);
        if (copy == null) return;
        copy.Code = isActive ? Code : original.Code;
        await _storageService.SaveSourceFileAsync(copy, overwriteChangesOnDisk: true);

        await SaveDocumentAsync(userAsked: false);
        await UpdateActiveScriptAsync(copy);
    }

    private void OnItemRenamed(Explorer.ExplorerItemViewModel item) => _ = OnItemRenamedAsync(item);

    internal async Task OnItemRenamedAsync(Explorer.ExplorerItemViewModel item)
    {
        if (item.IsDirectory)
        {
            if (item.IsExternalGroup) return;

            try
            {
                var oldPath = item.FullPath;
                var newPath = await _storageService.RenameFolderAsync(oldPath, item.Name);
                UpdateDescendantFullPaths(item, oldPath, newPath);
                item.FullPath = newPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CSharpEditorPlugin] Failed to rename folder '{item.FullPath}': {ex.Message}");
            }
            return;
        }

        if (string.IsNullOrEmpty(item.DocumentId)) return;

        if (item.IsSourceFile)
        {
            await RenameSourceFileAsync(item);
            return;
        }

        var newTitle = item.Name.EndsWith(".frycs", StringComparison.OrdinalIgnoreCase)
            ? item.Name.Substring(0, item.Name.Length - 6)
            : item.Name;

        var isOpen = Script != null && string.Equals(Script.Id, item.DocumentId, StringComparison.OrdinalIgnoreCase);
        if (isOpen)
        {
            Script!.Title = newTitle;
            OnPropertyChanged(nameof(Script));
            await SaveAsync();
        }
        else
        {
            var doc = await _storageService.LoadScriptAsync(item.DocumentId);
            if (doc != null)
            {
                doc.Title = newTitle;
                await _storageService.SaveScriptAsync(doc);
            }
        }

        // The file on disk takes the new name, as renaming in VS Code's Explorer does (only the title inside used to
        // change, so the folder kept the old name). A name another file already has leaves everything as it was.
        var oldName = Path.GetFileName((string?)item.FullPath) ?? item.Name;
        try
        {
            var fileName = await _storageService.RenameDocumentFileAsync(item.DocumentId, item.Name);
            item.Name = fileName;
            item.FullPath = item.Parent == null ? fileName : $"{item.Parent.FullPath}/{fileName}";
            if (isOpen)
            {
                Script!.Title = Path.GetFileNameWithoutExtension(fileName);
                OnPropertyChanged(nameof(Script));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            item.Name = oldName;
            if (isOpen)
            {
                Script!.Title = Path.GetFileNameWithoutExtension(oldName);
                OnPropertyChanged(nameof(Script));
                await SaveAsync();
            }

            CompilerStatusText = $"⚠️ Couldn't rename {oldName}: {ex.Message}";
        }
    }

    // A source file's name is its file name: renaming moves the file (and its id, which follows the path).
    private async Task RenameSourceFileAsync(Explorer.ExplorerItemViewModel item)
    {
        var oldId = item.DocumentId!;
        var oldName = Path.GetFileName((string?)item.FullPath) ?? string.Empty;
        try
        {
            var renamed = await _storageService.RenameSourceFileAsync(oldId, item.Name);
            var newName = Path.GetFileName(renamed.FilePath);
            item.DocumentId = renamed.Id;
            item.Name = newName;
            item.FullPath = item.Parent == null ? newName : $"{item.Parent.FullPath}/{newName}";

            // An open tab keeps editing the same file under its new name.
            var tab = OpenTabs.FirstOrDefault(t => string.Equals(t.Id, oldId, StringComparison.OrdinalIgnoreCase));
            var document = tab?.Document ?? (string.Equals(Script.Id, oldId, StringComparison.OrdinalIgnoreCase) ? Script : null);
            if (document != null)
            {
                document.Id = renamed.Id;
                document.Title = newName;
                document.SourceFilePath = renamed.FilePath;
                tab?.NotifyTitleChanged();
                if (ReferenceEquals(document, Script)) OnPropertyChanged(nameof(Script));
                RefreshQuickOpenDocuments();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            item.Name = oldName;
            item.FileExtension = Path.GetExtension(oldName) ?? string.Empty;
            CompilerStatusText = $"⚠️ Couldn't rename {oldName}: {ex.Message}";
        }
    }

    private void UpdateDescendantFullPaths(Explorer.ExplorerItemViewModel node, string oldPrefix, string newPrefix)
    {
        foreach (var child in node.Children)
        {
            if (child.FullPath.StartsWith(oldPrefix, StringComparison.Ordinal))
            {
                child.FullPath = newPrefix + child.FullPath[oldPrefix.Length..];
            }
            UpdateDescendantFullPaths(child, oldPrefix, newPrefix);
        }
    }

    [RelayCommand]
    public void CopyItemPath(Explorer.ExplorerItemViewModel item)
    {
        if (item == null) return;
        var rel = !string.IsNullOrEmpty(item.FullPath) ? item.FullPath : item.Name;
        var abs = Path.GetFullPath(Path.Combine(_storageService.ActiveWorkspaceRootPath, rel.Replace('/', Path.DirectorySeparatorChar)));
        _ = CopyTextToClipboardAsync(abs);
        CompilerStatusText = $"Copied full path: {abs}";
    }

    [RelayCommand]
    public void CopyItemRelativePath(Explorer.ExplorerItemViewModel item)
    {
        if (item == null) return;
        var rel = !string.IsNullOrEmpty(item.FullPath) ? item.FullPath : item.Name;
        _ = CopyTextToClipboardAsync(rel);
        CompilerStatusText = $"Copied relative path: {rel}";
    }

    [RelayCommand]
    public void RevealItemInFileManager(Explorer.ExplorerItemViewModel item)
    {
        if (item == null) return;
        var rel = !string.IsNullOrEmpty(item.FullPath) ? item.FullPath : item.Name;
        var fullPath = Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(_storageService.ActiveWorkspaceRootPath, rel.Replace('/', Path.DirectorySeparatorChar)));

        if (string.IsNullOrWhiteSpace(fullPath) || (!File.Exists(fullPath) && !Directory.Exists(fullPath))) return;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var target = item.IsDirectory ? fullPath : (Path.GetDirectoryName(fullPath) ?? fullPath);
                Process.Start("open", $"\"{target}\"");
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = item.IsDirectory ? $"\"{fullPath}\"" : $"/select,\"{fullPath}\"",
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsLinux())
            {
                var target = item.IsDirectory ? fullPath : (Path.GetDirectoryName(fullPath) ?? fullPath);
                Process.Start("xdg-open", $"\"{target}\"");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Reveal in file manager failed: {ex.Message}");
        }
    }

    [RelayCommand]
    public void CollapseAllExplorer()
    {
        foreach (var item in ExplorerRootItems)
        {
            CollapseItemRecursive(item);
        }
    }

    private void CollapseItemRecursive(Explorer.ExplorerItemViewModel item)
    {
        if (item.IsDirectory)
        {
            item.IsExpanded = false;
            foreach (var child in item.Children)
            {
                CollapseItemRecursive(child);
            }
        }
    }
}
