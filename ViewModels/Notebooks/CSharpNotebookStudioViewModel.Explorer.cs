using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Activities;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks;

public partial class CSharpNotebookStudioViewModel
{
    // ── Explorer refresh ──────────────────────────────────────────────────────

    private ExplorerRefresher? _explorerRefresher;

    // When the tree is listed: never while the studio is built, but on the first visit, from the host once the engine
    // is warm, and when the workspace changed (see ExplorerRefresher).
    private ExplorerRefresher ExplorerRefresh => _explorerRefresher ??= new ExplorerRefresher(_storageService, _activities, ShowExplorerListing);

    /// <summary>Lists the workspace and redraws the Explorer, whether or not anything changed (the Refresh button).</summary>
    [RelayCommand]
    public Task RefreshExplorerAsync() => ExplorerRefresh.RefreshAsync();

    /// <summary>Backward-compatibility method for tests and callers.</summary>
    public Task RefreshExplorer() => RefreshExplorerAsync();

    /// <summary>Reloads the Explorer only if the workspace changed since it was built (a script created in the Hub, say), or never was.</summary>
    public Task RefreshExplorerIfStaleAsync() => ExplorerRefresh.RefreshIfStaleAsync();

    // ── Page lifecycle ────────────────────────────────────────────────────────

    // A page that is not on screen leaves refreshing to its next visit. Assumed on screen until told otherwise (the
    // studio is also used without a host that says so).
    private bool _isPageActive = true;

    // Shown again after other pages (or other programs) may have added, removed or renamed items: catch up.
    public void OnActivated()
    {
        _isPageActive = true;
        UpdateExtensibilityDocumentResolver();
        RefreshExplorerIfStaleAsync().FireAndForget(_activities, "Listing the workspace");
        _ = RefreshGitStatusAsync();

        // Reading the file index starts (or refreshes) its background walk, so Go to File is ready when it is used.
        _ = _storageService.FileIndex;
    }

    public void OnDeactivated() => _isPageActive = false;

    // ── Explorer tree build & helpers ─────────────────────────────────────────

    [RelayCommand]
    public void CollapseAllExplorer()
    {
        foreach (var item in ExplorerRootItems)
        {
            CollapseItemRecursive(item);
        }
    }

    private void CollapseItemRecursive(ExplorerItemViewModel item)
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

    private LazyExplorerTree? _lazyExplorer;

    // The Explorer of a workspace too big to list at once: one folder at a time (see LazyExplorerTree).
    private LazyExplorerTree LazyExplorer => _lazyExplorer ??= new LazyExplorerTree(
        _storageService,
        ExplorerRootItems,
        ExplorerRows,
        (name, path, parent) => CreateFolderItem(name, path, isExpanded: false, parent: parent),
        (summary, name, fullPath, parent) =>
        {
            var item = CreateFileItem(name, summary.Id, parent, fullPath);
            if (summary.IsSourceFile && _storageService.Languages.Get(summary.LanguageId) is { } language)
            {
                item.IsSourceFile = true;
                item.LanguageIconKind = language.IconKind;
                item.LanguageIconColor = language.AccentHex;
            }

            return item;
        });

    // Draws what the storage listed: the whole workspace, or (when it is too big) its top folder, the rest coming as folders open.
    private void ShowExplorerListing(WorkspaceListing listing)
    {
        if (!listing.IsPartial)
        {
            LazyExplorer.Deactivate();
            RebuildExplorerTree(listing.FolderPaths, listing.Items, listing.ClosedFolders);
            return;
        }

        LazyExplorer.Build(listing, ExplorerItemViewModel.ExpandedFolderPaths(ExplorerRootItems));

        // Only the top folder itself can be cut off here (a nested one says so in its own rows).
        IsExplorerTruncated = listing.IsTruncated;
        if (ActiveTab != null)
        {
            HighlightExplorerItem(ActiveTab.Title);
        }
    }


    private void RebuildExplorerTree(IReadOnlyList<string> folderPaths, IReadOnlyList<WorkspaceItemSummary> summaries, IReadOnlyList<string> closedFolders)
    {
        // Thousands of single changes below; the flat row list is rebuilt once, when the scope ends.
        using var rowsScope = ExplorerRows.Suspend();

        // A refresh keeps the folders the user had open open.
        var expandedFolders = ExplorerItemViewModel.ExpandedFolderPaths(ExplorerRootItems);
        ExplorerRootItems.Clear();
        var folderNodes = new Dictionary<string, ExplorerItemViewModel>(StringComparer.OrdinalIgnoreCase);
        // The file names already in each folder (the root under its own key): a name-by-name scan of the siblings for every
        // file would be quadratic in the size of a folder.
        var rootKey = new object();
        var namesByFolder = new Dictionary<object, HashSet<string>>();

        ExplorerItemViewModel? GetOrCreateFolder(string relativePath)
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

            var docItem = CreateFileItem(name, s.Id, parent, fullPath);
            if (s.IsSourceFile && _storageService.Languages.Get(s.LanguageId) is { } language)
            {
                docItem.IsSourceFile = true;
                docItem.LanguageIconKind = language.IconKind;
                docItem.LanguageIconColor = language.AccentHex;
            }
            AddToTree(parent, docItem);
        }

        SortExplorerTree(ExplorerRootItems);

        if (ActiveTab != null)
        {
            HighlightExplorerItem(ActiveTab.Title);
        }

        IsExplorerTruncated = _storageService.IsWorkspaceTruncated;
    }

    private void SortExplorerTree(System.Collections.ObjectModel.ObservableCollection<ExplorerItemViewModel> items)
    {
        using var rowsScope = ExplorerRows.Suspend();

        var sorted = items.OrderByDescending<ExplorerItemViewModel, bool>(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (!sorted.SequenceEqual(items))
        {
            items.Clear();
            foreach (var item in sorted)
            {
                items.Add(item);
            }
        }

        foreach (var folder in items.Where(i => i.IsDirectory))
        {
            SortExplorerTree(folder.Children);
        }
    }

    private ExplorerItemViewModel CreateFolderItem(string name, string fullPath, bool isExpanded = false, ExplorerItemViewModel? parent = null, bool isExternalGroup = false)
    {
        return new ExplorerItemViewModel
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

    private ExplorerItemViewModel CreateFileItem(string name, string? documentId, ExplorerItemViewModel? parent, string fullPath)
    {
        return new ExplorerItemViewModel
        {
            Name = name,
            DocumentId = documentId,
            IsDirectory = false,
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

    private void AddToTree(ExplorerItemViewModel? parent, ExplorerItemViewModel child)
    {
        if (parent != null)
        {
            parent.Children.Add(child);
        }
        else
        {
            ExplorerRootItems.Add(child);
        }
    }

    /// <summary>
    /// The notebook's row, when it is one of the workspace folder's files. The Explorer lists that folder only: a notebook
    /// opened from elsewhere (the library, a problem, another folder) gets no row of its own.
    /// </summary>
    public ExplorerItemViewModel? FindDocumentInExplorer(NotebookDocumentItem notebook) =>
        string.IsNullOrEmpty(notebook.Id) ? null : FindItemByDocumentId(ExplorerRootItems, notebook.Id);

    private static ExplorerItemViewModel? FindItemByDocumentId(IEnumerable<ExplorerItemViewModel> items, string documentId)
    {
        foreach (var it in items)
        {
            if (string.Equals(it.DocumentId, documentId, StringComparison.OrdinalIgnoreCase)) return it;
            if (FindItemByDocumentId(it.Children, documentId) is { } found) return found;
        }

        return null;
    }

    // ── Tree selection helpers ─────────────────────────────────────────────────

    private void DeselectAll(IEnumerable<ExplorerItemViewModel> items)
    {
        foreach (var it in items)
        {
            it.IsSelected = false;
            if (it.Children.Count > 0)
            {
                DeselectAll(it.Children);
            }
        }
    }

    private void HighlightExplorerItem(string fileName)
    {
        DeselectAll(ExplorerRootItems);
        var docId = ActiveTab?.Notebook?.Id;
        // By id: several files can share a title ("Interactive C# Notebook (2).frynb" and the rest).
        var match = string.IsNullOrEmpty(docId) ? FindItemByIdOrName(ExplorerRootItems, null, fileName) : FindItemByDocumentId(ExplorerRootItems, docId);
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
        else if (LazyExplorer.IsActive && !string.IsNullOrEmpty(docId))
        {
            _ = RevealInLazyExplorerAsync(docId);
        }
    }

    // A big workspace lists a folder when it is opened: open the folders down to the notebook, then highlight it.
    private async Task RevealInLazyExplorerAsync(string documentId)
    {
        var item = await LazyExplorer.RevealAsync(documentId);
        if (!string.Equals(ActiveTab?.Notebook?.Id, documentId, StringComparison.OrdinalIgnoreCase)) return;

        // Not in the workspace's folders: the Explorer shows the folder only, so there is nothing to highlight.
        if (item != null)
        {
            DeselectAll(ExplorerRootItems);
            item.IsSelected = true;
        }
    }

    private ExplorerItemViewModel? FindItemByIdOrName(IEnumerable<ExplorerItemViewModel> items, string? docId, string name)
    {
        foreach (var it in items)
        {
            if (!string.IsNullOrEmpty(docId) && string.Equals(it.DocumentId, docId, StringComparison.OrdinalIgnoreCase))
                return it;

            if (string.Equals(it.Name, name, StringComparison.OrdinalIgnoreCase))
                return it;

            var found = FindItemByIdOrName(it.Children, docId, name);
            if (found != null) return found;
        }
        return null;
    }

    private ExplorerItemViewModel? FindSelectedItem(IEnumerable<ExplorerItemViewModel> items)
    {
        foreach (var it in items)
        {
            if (it.IsSelected) return it;
            var found = FindSelectedItem(it.Children);
            if (found != null) return found;
        }
        return null;
    }

    private ExplorerItemViewModel? FindFirstFile(IEnumerable<ExplorerItemViewModel> items)
    {
        foreach (var it in items)
        {
            if (!it.IsDirectory) return it;
            var found = FindFirstFile(it.Children);
            if (found != null) return found;
        }
        return null;
    }
}
