using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Activities;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks;

public partial class CSharpNotebookStudioViewModel
{
    // ── Open document ─────────────────────────────────────────────────────────

    private void OnExplorerItemClicked(ExplorerItemViewModel item) => _ = OpenDocumentAsync(item);

    public void OpenDocument(ExplorerItemViewModel item) => _ = OpenDocumentAsync(item);

    public async Task OpenDocumentAsync(ExplorerItemViewModel item)
    {
        DeselectAll(ExplorerRootItems);
        item.IsSelected = true;

        if (item.IsDirectory)
        {
            return;
        }

        var token = _documentOpen.Begin();
        using var activity = _activities.Start(new ActivityOptions($"Opening {item.Name}", ActivityLocation.Notebook), token);
        // API server documents open in the Server Studio.
        if (item.FileExtension.Equals(".fryserver", StringComparison.OrdinalIgnoreCase))
        {
            if (_openServerAction != null && !string.IsNullOrEmpty(item.DocumentId))
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

        // Non-notebook documents (scripts, plain source files, text and data files) open in the Code Studio.
        if (!item.FileExtension.Equals(".frynb", StringComparison.OrdinalIgnoreCase) &&
            !item.FileExtension.Equals(".ipynb", StringComparison.OrdinalIgnoreCase))
        {
            if (_openScriptAction != null)
            {
                ScriptDocumentItem? sc = null;
                if (!string.IsNullOrEmpty(item.DocumentId))
                {
                    sc = await Task.Run(async () => await _storageService.LoadScriptAsync(item.DocumentId));
                }
                if (sc == null && !string.IsNullOrEmpty(item.FullPath))
                {
                    var openRes = await Task.Run<OpenProjectResult>(async () => await _storageService.OpenExternalProjectAsync(item.FullPath));
                    if (openRes.Success && !string.IsNullOrEmpty(openRes.PrimaryDocumentId))
                    {
                        sc = await Task.Run(async () => await _storageService.LoadScriptAsync(openRes.PrimaryDocumentId));
                    }
                }

                if (token.IsCancellationRequested) return;
                if (sc != null)
                {
                    _openScriptAction.Invoke(sc);
                    return;
                }
            }
        }

        var fileName = item.Name;
        var folderName = item.Parent?.Name ?? "Library";
        var filePath = !string.IsNullOrEmpty(item.FullPath) ? item.FullPath : fileName;
        var docTitle = fileName.EndsWith(".frynb", StringComparison.OrdinalIgnoreCase)
            ? fileName.Substring(0, fileName.Length - 6)
            : fileName;

        // The same file: by id when it has one (copies of a notebook keep its title, so a title match opened another copy).
        var existingTab = Tabs.FirstOrDefault(t => !string.IsNullOrEmpty(item.DocumentId)
            ? string.Equals(t.Notebook.Id, item.DocumentId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(t.Title, fileName, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(t.Notebook.Title, docTitle, StringComparison.OrdinalIgnoreCase) ||
              (!string.IsNullOrEmpty(filePath) && string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase)));

        if (existingTab != null)
        {
            if (!token.IsCancellationRequested) SelectTab(existingTab);
            return;
        }

        var resolvedPath = filePath;
        if (!Path.IsPathRooted(resolvedPath) && !string.IsNullOrEmpty(_storageService.ActiveWorkspaceRootPath))
        {
            var candidate = Path.Combine(_storageService.ActiveWorkspaceRootPath, resolvedPath);
            if (File.Exists(candidate)) resolvedPath = candidate;
        }

        NotebookDocumentItem? loadedDoc = null;
        if (!string.IsNullOrEmpty(item.DocumentId))
        {
            loadedDoc = await Task.Run(async () => await _storageService.LoadNotebookAsync(item.DocumentId));
        }

        if (loadedDoc == null && !string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath) &&
            (resolvedPath.EndsWith(".frynb", StringComparison.OrdinalIgnoreCase) ||
             resolvedPath.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase) ||
             resolvedPath.EndsWith(".csnb", StringComparison.OrdinalIgnoreCase)))
        {
            loadedDoc = await Task.Run(async () => await _storageService.LoadNotebookAsync(resolvedPath));
            if (loadedDoc != null && !string.IsNullOrEmpty(loadedDoc.Id))
            {
                item.DocumentId = loadedDoc.Id;
            }
        }

        if (loadedDoc == null)
        {
            var summaries = await Task.Run(async () => await _storageService.LoadWorkspaceSummariesAsync());
            var match = summaries.FirstOrDefault(s => s.IsNotebook && (
                string.Equals(s.Id, item.DocumentId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Title, docTitle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Title, fileName, StringComparison.OrdinalIgnoreCase)));

            if (match != null)
            {
                loadedDoc = await Task.Run(async () => await _storageService.LoadNotebookAsync(match.Id));
                if (loadedDoc != null) item.DocumentId = match.Id;
            }
        }

        if (loadedDoc == null)
        {
            loadedDoc = new NotebookDocumentItem
            {
                Id = item.DocumentId ?? Guid.NewGuid().ToString("N"),
                Title = docTitle,
                Category = "Interactive",
                Created = DateTime.UtcNow,
                LastModified = DateTime.UtcNow
            };

            loadedDoc.Cells.Add(new NotebookCellItem
            {
                Type = CellType.Markdown,
                Source = $"# 📓 {docTitle}\nWrite documentation or notes in this cell.",
                IsMarkdownPreviewMode = true
            });
            loadedDoc.Cells.Add(new NotebookCellItem
            {
                Type = CellType.Code,
                Source = "// Write C# code here\nConsole.WriteLine(\"Hello from notebook!\");"
            });

            await Task.Run(async () => await _storageService.SaveNotebookAsync(loadedDoc));
            item.DocumentId = loadedDoc.Id;
        }

        // PopulateCellsAsync builds all NotebookCellViewModels (Bitmap decode, snapshot
        // materialization) on a Task.Run background thread — no UI-thread freeze.
        var newTab = await CreateTabAsync(loadedDoc, folderName, filePath);
        if (token.IsCancellationRequested) return;
        ConfigureNotebookTab(newTab);
        Tabs.Add(newTab);
        SelectTab(newTab);
    }

    // Go to File: opens any file of the workspace by its path. A notebook opens here, anything else in the Code Studio.
    public async Task OpenWorkspaceFileAsync(string fullPath)
    {
        var token = _documentOpen.Begin();
        using var activity = _activities.Start(new ActivityOptions($"Opening {Path.GetFileName(fullPath)}", ActivityLocation.Notebook), token);
        var result = await Task.Run(() => _storageService.OpenExternalProjectAsync(fullPath));
        if (token.IsCancellationRequested) return;
        if (!result.Success || string.IsNullOrEmpty(result.PrimaryDocumentId))
        {
            if (ActiveTab != null) ActiveTab.KernelStatusText = result.Message;
            return;
        }

        if (result.PrimaryDocumentKind == WorkspaceItemKind.Notebook)
        {
            if (await Task.Run(() => _storageService.LoadNotebookAsync(result.PrimaryDocumentId)) is { } notebook && !token.IsCancellationRequested)
            {
                UpdateActiveNotebook(notebook);
            }
            return;
        }

        if (_openScriptAction != null && await Task.Run(() => _storageService.LoadScriptAsync(result.PrimaryDocumentId)) is { } script && !token.IsCancellationRequested)
        {
            _openScriptAction.Invoke(script);
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
                if (ActiveTab != null)
                {
                    ActiveTab.KernelStatusText = result.Message;
                }
                return;
            }

            await RefreshExplorerAsync();

            Services.Extensibility.WorkspaceCustomizationLoader.LoadInBackground(_storageService.ActiveWorkspaceRootPath, _activities);

            if (!string.IsNullOrEmpty(result.PrimaryDocumentId))
            {
                var loaded = await Task.Run(async () => await _storageService.LoadNotebookAsync(result.PrimaryDocumentId));
                if (loaded != null)
                {
                    UpdateActiveNotebook(loaded);
                }
            }

            if (ActiveTab != null)
            {
                ActiveTab.KernelStatusText = result.Message;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to open external project '{path}': {ex.Message}");
            if (ActiveTab != null)
            {
                ActiveTab.KernelStatusText = $"Error opening project: {ex.Message}";
            }
        }
    }
}
