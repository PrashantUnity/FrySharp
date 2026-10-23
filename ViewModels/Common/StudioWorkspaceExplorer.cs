using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Activities;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;

/// <summary>
/// A studio's Explorer of the workspace folder, as VS Code shows it: every file and folder in it (all but .git, .DS_Store
/// and the like), nothing from outside it, each file by its own name. A big workspace, and folders such as node_modules,
/// are listed a folder at a time (<see cref="LazyExplorerTree"/>). Opening a file is the studio's; creating, renaming,
/// duplicating, deleting and copying paths work on the disk through the storage. Bound to
/// <c>StudioExplorerPanelControl</c> (the Server Studio's Explorer).
/// </summary>
public sealed partial class StudioWorkspaceExplorer : ObservableObject, IExplorerNewFileHost
{
    private readonly IScriptStorageService _storage;
    private readonly Func<ExplorerItemViewModel, Task> _open;
    private readonly Func<string?, Task<string?>> _createDocument;
    private readonly ExplorerRefresher _refresh;
    private LazyExplorerTree? _lazy;
    private string? _highlightedId;

    /// <param name="storage">The workspace.</param>
    /// <param name="open">Opens a file the user picked (the studio decides where).</param>
    /// <param name="createDocument">Creates the studio's own kind of document in a folder (path from the root, null for the top) and returns its id.</param>
    /// <param name="activities">Where a slow listing shows and a failed one is reported.</param>
    public StudioWorkspaceExplorer(IScriptStorageService storage, Func<ExplorerItemViewModel, Task> open, Func<string?, Task<string?>> createDocument,
        IActivityService? activities = null)
    {
        _storage = storage;
        _open = open;
        _createDocument = createDocument;
        ExplorerRows = new ExplorerRowList(ExplorerRootItems);
        _refresh = new ExplorerRefresher(storage, activities ?? NullActivityService.Instance, Show);
    }

    public ObservableCollection<ExplorerItemViewModel> ExplorerRootItems { get; } = new();

    /// <summary>The visible rows as one flat list: what the (virtualized) panel binds to.</summary>
    public ExplorerRowList ExplorerRows { get; }

    [ObservableProperty]
    private bool _isExplorerTruncated;

    public string ExplorerTruncationText =>
        $"Showing the first {_storage.WorkspaceFileLimit:N0} files. This folder has more: press Ctrl+P to open any file by name, or open a subfolder.";

    /// <summary>The last thing done that the user should hear about (a path copied, a rename refused).</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>A document was deleted from the disk (its id): the studio closes it if it is open.</summary>
    public event Action<string>? DocumentDeleted;

    /// <summary>A document was renamed (its id, its new file name): the studio updates its title if it is open.</summary>
    public event Action<string, string>? DocumentRenamed;

    public IReadOnlyList<NewFileOption> NewFileOptions => [];

    private LazyExplorerTree Lazy => _lazy ??= new LazyExplorerTree(_storage, ExplorerRootItems, ExplorerRows,
        (name, path, parent) => Folder(name, path, parent, isExpanded: false),
        (summary, name, path, parent) => File(summary, name, path, parent));

    // ── Listing ───────────────────────────────────────────────────────────────

    /// <summary>Lists the workspace and redraws the tree, whether or not anything changed (the Refresh button).</summary>
    [RelayCommand]
    public Task RefreshExplorerAsync() => _refresh.RefreshAsync();

    /// <summary>Backward-compatibility method for tests and callers.</summary>
    public Task RefreshExplorer() => RefreshExplorerAsync();

    /// <summary>Lists the workspace again only when it changed since it was listed (a page coming back, say).</summary>
    public Task RefreshIfStaleAsync() => _refresh.RefreshIfStaleAsync();

    private void Show(WorkspaceListing listing)
    {
        var open = ExplorerItemViewModel.ExpandedFolderPaths(ExplorerRootItems);
        if (listing.IsPartial)
        {
            Lazy.Build(listing, open);
            IsExplorerTruncated = listing.IsTruncated;
        }
        else
        {
            Lazy.Deactivate();
            Build(listing, open);
            IsExplorerTruncated = _storage.IsWorkspaceTruncated;
        }

        Highlight(_highlightedId);
    }

    private void Build(WorkspaceListing listing, HashSet<string> open)
    {
        using var scope = ExplorerRows.Suspend();
        ExplorerRootItems.Clear();
        var folders = new Dictionary<string, ExplorerItemViewModel>(StringComparer.OrdinalIgnoreCase);

        ExplorerItemViewModel? FolderAt(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (folders.TryGetValue(path, out var existing)) return existing;
            var slash = path.LastIndexOf('/');
            var parent = FolderAt(slash >= 0 ? path[..slash] : string.Empty);
            var folder = Folder(path[(slash + 1)..], path, parent, open.Contains(path));
            (parent?.Children ?? ExplorerRootItems).Add(folder);
            folders[path] = folder;
            return folder;
        }

        foreach (var path in listing.FolderPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) FolderAt(path);

        // Folders the walk doesn't go into (node_modules, bin, .venv): shown closed and listed when opened.
        foreach (var path in listing.ClosedFolders)
        {
            if (FolderAt(path) is { } closed) Lazy.MakeUnlisted(closed);
        }

        foreach (var summary in listing.Items)
        {
            var parent = FolderAt(summary.FolderPath);
            var name = summary.ExplorerName;
            (parent?.Children ?? ExplorerRootItems).Add(File(summary, name, parent == null ? name : $"{parent.FullPath}/{name}", parent));
        }

        Sort(ExplorerRootItems);
    }

    private static void Sort(ObservableCollection<ExplorerItemViewModel> items)
    {
        var sorted = items.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (!sorted.SequenceEqual(items))
        {
            items.Clear();
            foreach (var item in sorted) items.Add(item);
        }

        foreach (var folder in items.Where(i => i.IsDirectory)) Sort(folder.Children);
    }

    private ExplorerItemViewModel Folder(string name, string path, ExplorerItemViewModel? parent, bool isExpanded) =>
        Wire(new ExplorerItemViewModel
        {
            Name = name,
            IsDirectory = true,
            IsExpanded = isExpanded,
            Parent = parent,
            Depth = (parent?.Depth ?? -1) + 1,
            FullPath = path,
        });

    private ExplorerItemViewModel File(WorkspaceItemSummary summary, string name, string path, ExplorerItemViewModel? parent)
    {
        var item = Wire(new ExplorerItemViewModel
        {
            Name = name,
            DocumentId = summary.Id,
            IsDirectory = false,
            FileExtension = Path.GetExtension(name),
            Parent = parent,
            Depth = (parent?.Depth ?? -1) + 1,
            FullPath = path,
        });
        if (summary.IsSourceFile && _storage.Languages.Get(summary.LanguageId) is { } language)
        {
            item.IsSourceFile = true;
            item.LanguageIconKind = language.IconKind;
            item.LanguageIconColor = language.AccentHex;
        }

        return item;
    }

    private ExplorerItemViewModel Wire(ExplorerItemViewModel item)
    {
        item.OnItemClicked = i => _ = OpenAsync(i);
        item.OnDeleteRequested = i => _ = DeleteAsync(i);
        item.OnNewFileRequested = i => _ = NewFileAsync(i);
        item.OnNewServerRequested = i => _ = NewFileAsync(i);
        item.OnNewNotebookRequested = i => _ = NewNotebookAsync(i);
        item.OnNewFolderRequested = i => _ = NewFolderAsync(i);
        item.OnRenameCommitted = i => _ = RenameAsync(i);
        item.OnDuplicateRequested = i => _ = DuplicateAsync(i);
        item.OnCopyPathRequested = i => _ = CopyAsync(AbsolutePath(i), "full path");
        item.OnCopyRelativePathRequested = i => _ = CopyAsync(i.FullPath, "relative path");
        item.OnRevealInFileManagerRequested = i => _ = RevealInFileManagerAsync(i);
        return item;
    }

    [RelayCommand]
    public void CollapseAllExplorer()
    {
        foreach (var folder in All(ExplorerRootItems).Where(i => i.IsDirectory)) folder.IsExpanded = false;
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    /// <summary>Marks the open document's row (and opens the folders down to it); none when it isn't a file of the workspace.</summary>
    public void Highlight(string? documentId)
    {
        _highlightedId = documentId;
        var match = documentId == null ? null : Find(ExplorerRootItems, i => string.Equals(i.DocumentId, documentId, StringComparison.OrdinalIgnoreCase));
        foreach (var item in All(ExplorerRootItems)) item.IsSelected = ReferenceEquals(item, match);
        for (var parent = match?.Parent; parent != null; parent = parent.Parent) parent.IsExpanded = true;
        if (match == null && documentId != null && Lazy.IsActive) _ = RevealAsync(documentId);
    }

    private async Task RevealAsync(string documentId)
    {
        var item = await Lazy.RevealAsync(documentId);
        if (item != null && string.Equals(_highlightedId, documentId, StringComparison.OrdinalIgnoreCase)) item.IsSelected = true;
    }

    private static IEnumerable<ExplorerItemViewModel> All(IEnumerable<ExplorerItemViewModel> items) =>
        items.SelectMany(i => new[] { i }.Concat(All(i.Children)));

    private static ExplorerItemViewModel? Find(IEnumerable<ExplorerItemViewModel> items, Func<ExplorerItemViewModel, bool> match) =>
        All(items).FirstOrDefault(match);

    private ExplorerItemViewModel? Selected => Find(ExplorerRootItems, i => i.IsSelected);

    // ── Actions ───────────────────────────────────────────────────────────────

    private async Task OpenAsync(ExplorerItemViewModel item)
    {
        if (item.IsPlaceholder) return;
        foreach (var other in All(ExplorerRootItems)) other.IsSelected = ReferenceEquals(other, item);
        if (item.IsDirectory)
        {
            return;
        }

        await _open(item);
    }

    private Task RevealInFileManagerAsync(ExplorerItemViewModel item)
    {
        var fullPath = AbsolutePath(item);
        if (string.IsNullOrWhiteSpace(fullPath) || (!System.IO.File.Exists(fullPath) && !System.IO.Directory.Exists(fullPath))) return Task.CompletedTask;

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
        return Task.CompletedTask;
    }

    /// <summary>A new document of the studio's kind in the selected folder (or the top), named by the user next.</summary>
    [RelayCommand]
    public Task NewScript() => NewFileAsync(Selected is { } s ? (s.IsDirectory ? s : s.Parent) : null);

    [RelayCommand]
    public Task NewFolder() => NewFolderAsync(Selected is { } s ? (s.IsDirectory ? s : s.Parent) : null);

    private async Task NewFileAsync(ExplorerItemViewModel? target)
    {
        var folder = target is { IsDirectory: false } ? target.Parent : target;
        var id = await _createDocument(folder?.FullPath);
        if (id == null) return;

        await RefreshExplorerAsync();
        if (folder != null) await ListedAsync(folder.FullPath);
        var created = Find(ExplorerRootItems, i => string.Equals(i.DocumentId, id, StringComparison.OrdinalIgnoreCase));
        if (created == null) return;

        await OpenAsync(created);
        created.StartRename();
    }

    private async Task NewNotebookAsync(ExplorerItemViewModel? target)
    {
        var parent = target is { IsDirectory: false } ? target.Parent : target;
        var timestamp = DateTime.Now.ToString("HHmmss");
        var title = $"Notebook_{timestamp}";
        NotebookDocumentItem doc;
        try
        {
            doc = await _storage.CreateNewNotebookAsync(title, folderPath: parent?.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"⚠️ Couldn't create notebook: {ex.Message}";
            return;
        }

        await RefreshExplorerAsync();
        if (parent != null) await ListedAsync(parent.FullPath);
        var created = Find(ExplorerRootItems, i => string.Equals(i.DocumentId, doc.Id, StringComparison.OrdinalIgnoreCase));
        if (created != null)
        {
            await OpenAsync(created);
            created.StartRename();
        }
    }

    private async Task NewFolderAsync(ExplorerItemViewModel? target)
    {
        var parent = target is { IsDirectory: false } ? target.Parent : target;
        string path;
        try
        {
            path = await _storage.CreateFolderAsync(parent?.FullPath, "New Folder");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"⚠️ Couldn't create a folder: {ex.Message}";
            return;
        }

        await RefreshExplorerAsync();
        if (parent != null) await ListedAsync(parent.FullPath);
        Find(ExplorerRootItems, i => i.IsDirectory && string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase))?.StartRename();
    }

    // The folder at a path after a refresh, opened and listed (a big workspace lists it on demand).
    private async Task<ExplorerItemViewModel?> ListedAsync(string path)
    {
        var folder = Find(ExplorerRootItems, i => i.IsDirectory && string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (folder == null) return null;
        for (var parent = folder.Parent; parent != null; parent = parent.Parent) parent.IsExpanded = true;
        if (!folder.ChildrenLoaded) await Lazy.LoadChildrenAsync(folder);
        folder.IsExpanded = true;
        return folder;
    }

    private async Task RenameAsync(ExplorerItemViewModel item)
    {
        var oldName = Path.GetFileName(item.FullPath);
        try
        {
            if (item.IsDirectory)
            {
                var newPath = await _storage.RenameFolderAsync(item.FullPath, item.Name);
                var oldPrefix = item.FullPath + "/";
                foreach (var child in All(item.Children)) child.FullPath = newPath + "/" + child.FullPath[oldPrefix.Length..];
                item.FullPath = newPath;
                return;
            }

            if (string.IsNullOrEmpty(item.DocumentId)) return;
            string fileName;
            if (item.IsSourceFile || !IsDocument(item.FileExtension))
            {
                var renamed = await _storage.RenameSourceFileAsync(item.DocumentId, item.Name);
                fileName = Path.GetFileName(renamed.FilePath);
                var oldId = item.DocumentId;
                item.DocumentId = renamed.Id;
                DocumentRenamed?.Invoke(oldId, fileName);
            }
            else
            {
                fileName = await _storage.RenameDocumentFileAsync(item.DocumentId, item.Name);
                DocumentRenamed?.Invoke(item.DocumentId, fileName);
            }

            item.Name = fileName;
            item.FileExtension = Path.GetExtension(fileName);
            item.FullPath = item.Parent == null ? fileName : $"{item.Parent.FullPath}/{fileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            item.Name = oldName;
            StatusText = $"⚠️ Couldn't rename {oldName}: {ex.Message}";
        }
    }

    private static bool IsDocument(string extension) =>
        extension.Equals(".frycs", StringComparison.OrdinalIgnoreCase) || extension.Equals(".frynb", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".fryserver", StringComparison.OrdinalIgnoreCase);

    private async Task DuplicateAsync(ExplorerItemViewModel item)
    {
        if (item.IsDirectory || item.IsPlaceholder) return;
        try
        {
            var copy = await _storage.DuplicateFileAsync(item.FullPath);
            await RefreshExplorerAsync();
            var parentPath = copy.Contains('/') ? copy[..copy.LastIndexOf('/')] : null;
            if (parentPath != null) await ListedAsync(parentPath);
            if (Find(ExplorerRootItems, i => !i.IsDirectory && string.Equals(i.FullPath, copy, StringComparison.OrdinalIgnoreCase)) is { } created)
            {
                foreach (var other in All(ExplorerRootItems)) other.IsSelected = ReferenceEquals(other, created);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"⚠️ Couldn't duplicate {item.Name}: {ex.Message}";
        }
    }

    private async Task DeleteAsync(ExplorerItemViewModel item)
    {
        if (item.IsPlaceholder) return;
        try
        {
            if (item.IsDirectory)
            {
                var ids = All(item.Children).Where(c => !c.IsDirectory && c.DocumentId != null).Select(c => c.DocumentId!).ToList();
                await _storage.DeleteFolderAsync(item.FullPath);
                foreach (var id in ids) DocumentDeleted?.Invoke(id);
            }
            else if (!string.IsNullOrEmpty(item.DocumentId))
            {
                await _storage.DeleteItemAsync(item.DocumentId);
                DocumentDeleted?.Invoke(item.DocumentId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"⚠️ Couldn't delete {item.Name}: {ex.Message}";
            return;
        }

        using (ExplorerRows.Suspend())
        {
            (item.Parent?.Children ?? ExplorerRootItems).Remove(item);
        }
    }

    private string AbsolutePath(ExplorerItemViewModel item) =>
        Path.GetFullPath(Path.Combine(_storage.ActiveWorkspaceRootPath, item.FullPath.Replace('/', Path.DirectorySeparatorChar)));

    private async Task CopyAsync(string text, string what)
    {
        try
        {
            var clipboard = Avalonia.Application.Current?.ApplicationLifetime switch
            {
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop =>
                    desktop.Windows.FirstOrDefault(w => w.IsActive)?.Clipboard ?? desktop.MainWindow?.Clipboard,
                Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime single =>
                    Avalonia.Controls.TopLevel.GetTopLevel(single.MainView)?.Clipboard,
                _ => null,
            };
            if (clipboard != null) await clipboard.SetTextAsync(text);
            StatusText = $"Copied {what}: {text}";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Copy failed: {ex.Message}");
        }
    }
}
