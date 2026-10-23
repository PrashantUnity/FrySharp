using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks;

public partial class CSharpNotebookStudioViewModel
{
    // ── Explorer CRUD ─────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task DeleteSelectedExplorerItem()
    {
        var selected = FindSelectedItem(ExplorerRootItems);
        if (selected != null)
        {
            await DeleteExplorerItemAsync(selected);
        }
    }

    public void DeleteExplorerItem(ExplorerItemViewModel item) => _ = DeleteExplorerItemAsync(item);

    [RelayCommand]
    public async Task DeleteExplorerItemAsync(ExplorerItemViewModel item)
    {
        if (item == null) return;

        if (item.IsExternalGroup) return;

        if (item.IsDirectory)
        {
            var descendantIds = CollectDescendantDocumentIds(item);
            if (descendantIds.Count > 0)
            {
                foreach (var tab in Tabs.Where(t => descendantIds.Contains(t.Notebook.Id)).ToList())
                {
                    CloseTab(tab);
                }
            }

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

            var openTab = Tabs.FirstOrDefault(t =>
                string.Equals(t.Notebook.Id, item.DocumentId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Title, item.Name, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(item.FullPath) && string.Equals(t.FilePath, item.FullPath, StringComparison.OrdinalIgnoreCase)));

            if (openTab != null)
            {
                CloseTab(openTab);
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

        if (item.IsSelected)
        {
            var nextFile = FindFirstFile(ExplorerRootItems);
            if (nextFile != null)
            {
                await OpenDocumentAsync(nextFile);
            }
        }
    }

    public void DuplicateExplorerItem(ExplorerItemViewModel item) => _ = DuplicateExplorerItemAsync(item);

    [RelayCommand]
    public async Task DuplicateExplorerItemAsync(ExplorerItemViewModel item)
    {
        if (item == null || item.IsDirectory) return;

        var parent = item.Parent;
        var originalTitle = item.Name.EndsWith(".frynb", StringComparison.OrdinalIgnoreCase)
            ? item.Name.Substring(0, item.Name.Length - 6)
            : item.Name;
        var copyTitle = $"{originalTitle} Copy";
        var copyFileName = $"{copyTitle}.frynb";

        NotebookDocumentItem? origDoc = null;
        if (!string.IsNullOrEmpty(item.DocumentId))
        {
            origDoc = await _storageService.LoadNotebookAsync(item.DocumentId);
        }
        if (origDoc == null)
        {
            var openTab = Tabs.FirstOrDefault(t => string.Equals(t.Title, item.Name, StringComparison.OrdinalIgnoreCase));
            origDoc = openTab?.Notebook;
        }

        var copyDoc = new NotebookDocumentItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = copyTitle,
            Category = origDoc?.Category ?? "Interactive",
            Description = origDoc?.Description ?? "",
            Created = DateTime.UtcNow,
            LastModified = DateTime.UtcNow
        };

        if (origDoc != null && origDoc.Cells.Count > 0)
        {
            foreach (var cell in origDoc.Cells)
            {
                copyDoc.Cells.Add(new NotebookCellItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Type = cell.Type,
                    Source = cell.Source,
                    IsMarkdownPreviewMode = cell.IsMarkdownPreviewMode,
                    IsInputCollapsed = cell.IsInputCollapsed,
                    IsOutputCollapsed = cell.IsOutputCollapsed,
                    IsOutputScrolled = cell.IsOutputScrolled
                });
            }
        }
        else
        {
            copyDoc.Cells.Add(new NotebookCellItem
            {
                Type = CellType.Markdown,
                Source = $"# 📓 {copyTitle}\nDuplicated notebook workspace.",
                IsMarkdownPreviewMode = true
            });
            copyDoc.Cells.Add(new NotebookCellItem
            {
                Type = CellType.Code,
                Source = "// Write C# code here\nConsole.WriteLine(\"Hello from duplicate notebook!\");"
            });
        }

        await _storageService.SaveNotebookAsync(copyDoc, parent?.FullPath);

        var copyPath = string.IsNullOrEmpty(parent?.FullPath) ? copyFileName : $"{parent!.FullPath}/{copyFileName}";
        var copyItem = CreateFileItem(copyFileName, copyDoc.Id, parent, copyPath);

        AddToTree(parent, copyItem);
        if (parent != null) parent.IsExpanded = true;

        await OpenDocumentAsync(copyItem);
    }

    [RelayCommand]
    public void CopyItemPath(ExplorerItemViewModel item)
    {
        if (item == null) return;
        var rel = !string.IsNullOrEmpty(item.FullPath) ? item.FullPath : item.Name;
        var abs = Path.GetFullPath(Path.Combine(_storageService.ActiveWorkspaceRootPath, rel.Replace('/', Path.DirectorySeparatorChar)));
        _ = CopyTextToClipboardAsync(abs);
        CompilerStatusText = $"Copied full path: {abs}";
    }

    [RelayCommand]
    public void CopyItemRelativePath(ExplorerItemViewModel item)
    {
        if (item == null) return;
        var rel = !string.IsNullOrEmpty(item.FullPath) ? item.FullPath : item.Name;
        _ = CopyTextToClipboardAsync(rel);
        CompilerStatusText = $"Copied relative path: {rel}";
    }

    [RelayCommand]
    public void RevealItemInFileManager(ExplorerItemViewModel item)
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
    public async Task NewFile()
    {
        var selected = FindSelectedItem(ExplorerRootItems);
        var targetFolder = (selected != null && selected.IsDirectory) ? selected : selected?.Parent;

        if (targetFolder != null)
        {
            await NewFileUnderItemAsync(targetFolder);
        }
        else
        {
            await NewNotebookTab();
        }
    }

    public void NewFileUnderItem(ExplorerItemViewModel target) => _ = NewFileUnderItemAsync(target);
    public void NewNotebookUnderItem(ExplorerItemViewModel target) => _ = NewFileUnderItemAsync(target);

    public void NewServerUnderItem(ExplorerItemViewModel target) => _ = NewServerUnderItemAsync(target);

    [RelayCommand]
    public async Task NewServerUnderItemAsync(ExplorerItemViewModel target)
    {
        var folder = target.IsDirectory ? target : target.Parent;
        var timestamp = DateTime.Now.ToString("HHmmss");
        var title = $"Server_{timestamp}";
        var folderPath = folder?.FullPath;

        try
        {
            var newServer = await _storageService.CreateNewServerDocumentAsync(title, folderPath: folderPath);
            _openServerAction?.Invoke(newServer);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to create server: {ex.Message}");
        }
    }

    public void NewScriptUnderItem(ExplorerItemViewModel target) => _ = NewScriptUnderItemAsync(target);

    [RelayCommand]
    public async Task NewScriptUnderItemAsync(ExplorerItemViewModel target)
    {
        var folder = target.IsDirectory ? target : target.Parent;
        var timestamp = DateTime.Now.ToString("HHmmss");
        var title = $"Script_{timestamp}";
        var folderPath = folder?.FullPath;

        try
        {
            var newScript = await _storageService.CreateNewScriptAsync(title, folderPath: folderPath);
            _openScriptAction?.Invoke(newScript);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to create script: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task NewFileUnderItemAsync(ExplorerItemViewModel target)
    {
        var folder = target.IsDirectory ? target : target.Parent;
        var timestamp = DateTime.Now.ToString("HHmmss");
        var title = $"Notebook_{timestamp}";
        var fileName = $"{title}.frynb";
        var folderPath = folder?.FullPath;

        NotebookDocumentItem newDoc;
        try
        {
            newDoc = await _storageService.CreateNewNotebookAsync(title, folderPath: folderPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to create notebook: {ex.Message}");
            newDoc = new NotebookDocumentItem { Id = Guid.NewGuid().ToString("N"), Title = title };
        }

        var fullPath = string.IsNullOrEmpty(folderPath) ? fileName : $"{folderPath}/{fileName}";
        ExplorerItemViewModel? newFile = null;

        // Listing a folder that has not been listed yet finds the new notebook on disk: adding a row too would show it twice.
        if (folder is { ChildrenLoaded: false })
        {
            await LazyExplorer.LoadChildrenAsync(folder);
            folder.IsExpanded = true;
            newFile = folder.Children.FirstOrDefault(c => string.Equals(c.DocumentId, newDoc.Id, StringComparison.OrdinalIgnoreCase));
        }

        if (newFile == null)
        {
            newFile = CreateFileItem(fileName, newDoc.Id, folder, fullPath);
            AddToTree(folder, newFile);
            if (folder != null) folder.IsExpanded = true;
        }

        await OpenDocumentAsync(newFile);
        newFile.StartRename();
    }

    [RelayCommand]
    public async Task NewFolder()
    {
        var selected = FindSelectedItem(ExplorerRootItems);
        var targetFolder = (selected != null && selected.IsDirectory) ? selected : selected?.Parent;
        await CreateFolderCoreAsync(targetFolder);
    }

    public void NewFolderUnderItem(ExplorerItemViewModel target) => _ = NewFolderUnderItemAsync(target);

    [RelayCommand]
    public async Task NewFolderUnderItemAsync(ExplorerItemViewModel target)
    {
        var folder = target.IsDirectory ? target : target.Parent;
        await CreateFolderCoreAsync(folder);
    }

    private async Task CreateFolderCoreAsync(ExplorerItemViewModel? parentFolder)
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

    private HashSet<string> CollectDescendantDocumentIds(ExplorerItemViewModel item)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(ExplorerItemViewModel node)
        {
            if (!node.IsDirectory && !string.IsNullOrEmpty(node.DocumentId))
            {
                ids.Add(node.DocumentId);
            }
            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }

        Walk(item);
        return ids;
    }

    private void UpdateDescendantFullPaths(ExplorerItemViewModel node, string oldPrefix, string newPrefix)
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

    private void OnItemRenamed(ExplorerItemViewModel item) => _ = OnItemRenamedAsync(item);

    internal async Task OnItemRenamedAsync(ExplorerItemViewModel item)
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

        var newTitle = item.Name.EndsWith(".frynb", StringComparison.OrdinalIgnoreCase)
            ? item.Name.Substring(0, item.Name.Length - 6)
            : item.Name;

        var openTab = Tabs.FirstOrDefault(t => string.Equals(t.Notebook.Id, item.DocumentId, StringComparison.OrdinalIgnoreCase));
        if (openTab != null)
        {
            openTab.Title = item.Name;
            openTab.Notebook.Title = newTitle;
            // An ephemeral notebook is saved for the very first time on rename — clear the flag so SaveNotebookAsync writes it.
            openTab.Notebook.IsEphemeral = false;
            await _storageService.SaveNotebookAsync(openTab.Notebook);
        }
        else
        {
            var doc = await _storageService.LoadNotebookAsync(item.DocumentId);
            if (doc != null)
            {
                doc.Title = newTitle;
                doc.IsEphemeral = false;
                await _storageService.SaveNotebookAsync(doc);
            }
        }

        await RenameFileAsync(item, openTab);
    }

    // The file on disk takes the new name, as renaming in VS Code's Explorer does (only the title inside used to change,
    // so the folder kept the old name). A name another file already has leaves everything as it was.
    private async Task RenameFileAsync(ExplorerItemViewModel item, NotebookTabViewModel? openTab)
    {
        var oldName = Path.GetFileName(item.FullPath);
        try
        {
            var fileName = await _storageService.RenameDocumentFileAsync(item.DocumentId!, item.Name);
            item.Name = fileName;
            item.FullPath = item.Parent == null ? fileName : $"{item.Parent.FullPath}/{fileName}";
            if (openTab != null)
            {
                openTab.Title = fileName;
                openTab.Notebook.Title = Path.GetFileNameWithoutExtension(fileName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            item.Name = oldName;
            if (openTab != null)
            {
                openTab.Title = oldName;
                openTab.Notebook.Title = Path.GetFileNameWithoutExtension(oldName);
                await _storageService.SaveNotebookAsync(openTab.Notebook);
            }

            CompilerStatusText = $"⚠️ Couldn't rename {oldName}: {ex.Message}";
        }
    }
}
