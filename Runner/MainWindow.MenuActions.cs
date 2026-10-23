using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Views;

namespace PdfEditorApp.Plugins.CSharpEditor.Runner;

public partial class MainWindow : Window
{
    // ── File Menu Actions ──
    public async void NewScript_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) await code.NewScriptCommand.ExecuteAsync(null);
        else await StudioHostVm.ManagerViewModel.CreateNewScriptAsync();
    }

    public async void NewNotebook_OnClick(object? sender, EventArgs e) =>
        await StudioHostVm.ManagerViewModel.CreateNewNotebookAsync();

    public async void NewServer_OnClick(object? sender, EventArgs e) =>
        await StudioHostVm.ManagerViewModel.CreateNewServerCommand.ExecuteAsync(null);

    public async void OpenFile_OnClick(object? sender, EventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Script, Notebook, or Server",
            AllowMultiple = false
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".ipynb" or ".csnb" or ".frynb")
            {
                var nb = await StudioHostVm.StorageService.LoadNotebookAsync(path);
                if (nb != null) StudioHostVm.NavigateToNotebookStudio(nb);
            }
            else if (ext is ".fryserver")
            {
                var srv = await StudioHostVm.StorageService.LoadServerDocumentAsync(path);
                if (srv != null) StudioHostVm.NavigateToServerStudio(srv, path);
            }
            else if (ext is ".zip" or ".csproj" or ".frycsproj")
            {
                await StudioHostVm.OpenExistingProjectAsync(path);
            }
            else if (ActiveCodeStudio is { } code)
            {
                await code.OpenExternalProjectAsync(path);
            }
            else
            {
                var lang = StudioHostVm.Languages.Registry.FindByExtension(path);
                var doc = new ScriptDocumentItem
                {
                    Id = ScriptStorageIdHelper.SourceFileId(path),
                    Title = Path.GetFileName(path),
                    SourceFilePath = path,
                    LanguageId = lang?.Id ?? "csharp",
                    Code = File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty
                };
                StudioHostVm.NavigateToCodeStudio(doc);
            }
        }
    }

    public async void OpenFolder_OnClick(object? sender, EventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Workspace Folder",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            await StudioHostVm.OpenExistingProjectAsync(path);
        }
    }

    public async void Save_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) await code.SaveCommand.ExecuteAsync(null);
        else if (ActiveNotebookStudio is { } nb) await nb.SaveAsync();
        else if (ActiveServerStudio is { } srv) await srv.SaveDocumentAsync();
    }

    public async void SaveAs_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code)
        {
            var ext = Path.GetExtension(code.Script?.SourceFilePath ?? ".csx");
            if (string.IsNullOrEmpty(ext)) ext = ".csx";
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Script As",
                DefaultExtension = ext,
                SuggestedFileName = code.Script?.Title ?? "Script" + ext
            });
            if (file?.TryGetLocalPath() is { } path)
            {
                await File.WriteAllTextAsync(path, code.Code);
            }
        }
        else if (ActiveNotebookStudio is { } nb && nb.ActiveTab?.Notebook is { } notebook)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Notebook As",
                DefaultExtension = ".ipynb",
                SuggestedFileName = notebook.Title + ".ipynb",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Jupyter Notebook (*.ipynb)") { Patterns = new[] { "*.ipynb" } },
                    new FilePickerFileType("C# Notebook (*.csnb)") { Patterns = new[] { "*.csnb" } }
                }
            });
            if (file?.TryGetLocalPath() is { } path)
            {
                if (nb.ActiveTab != null) nb.ActiveTab.FilePath = path;
                await StudioHostVm.StorageService.SaveNotebookAsync(notebook, path);
                if (nb.ActiveTab != null) nb.ActiveTab.IsModified = false;
            }
        }
        else if (ActiveServerStudio is { } srv && srv.Document is { } srvDoc)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save API Server As",
                DefaultExtension = ".fryserver",
                SuggestedFileName = srv.DocumentTitle + ".fryserver"
            });
            if (file?.TryGetLocalPath() is { } path)
            {
                srv.FilePath = path;
                await StudioHostVm.StorageService.SaveServerDocumentAsync(srvDoc);
            }
        }
    }

    public async void SaveAll_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) await code.SaveCommand.ExecuteAsync(null);
        else if (ActiveNotebookStudio is { } nb) await nb.SaveAsync();
        else if (ActiveServerStudio is { } srv) await srv.SaveDocumentAsync();
    }

    public async void CloseTab_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code)
        {
            var tab = code.OpenTabs.FirstOrDefault(t => t.IsActive);
            if (tab != null) await code.CloseTabAsync(tab);
            else StudioHostVm.NavigateToManager();
        }
        else if (ActiveNotebookStudio is { } nb)
        {
            if (nb.ActiveTab != null && nb.Tabs.Count > 1) nb.CloseTab(nb.ActiveTab);
            else StudioHostVm.NavigateToManager();
        }
        else if (ActiveServerStudio != null)
        {
            StudioHostVm.NavigateToManager();
        }
        else if (ActiveDocs != null || ActiveSettings != null || ActiveBlindProblems != null)
        {
            StudioHostVm.NavigateToPreviousPage();
        }
    }

    public void CloseWindow_OnClick(object? sender, EventArgs e) => Close();

    // ── Edit Menu Actions ──
    private AvaloniaEdit.TextEditor? GetFocusedTextEditor()
    {
        var focused = FocusManager?.GetFocusedElement() as Visual;
        return focused as AvaloniaEdit.TextEditor ?? focused?.FindAncestorOfType<AvaloniaEdit.TextEditor>();
    }

    public void Undo_OnClick(object? sender, EventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox tb) { tb.Undo(); return; }
        if (GetFocusedTextEditor() is { } editor) { editor.Undo(); return; }
        if (ActiveCodeStudio != null && StudioHost.Pages.ViewFor(ActiveCodeStudio) is CSharpCodeStudioView cv) { cv.GetEditor()?.Undo(); return; }
        if (ActiveNotebookStudio != null && StudioHost.Pages.ViewFor(ActiveNotebookStudio) is CSharpNotebookStudioView nv) { nv.GetActiveEditor()?.Undo(); return; }
    }

    public void Redo_OnClick(object? sender, EventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox tb) { tb.Redo(); return; }
        if (GetFocusedTextEditor() is { } editor) { editor.Redo(); return; }
        if (ActiveCodeStudio != null && StudioHost.Pages.ViewFor(ActiveCodeStudio) is CSharpCodeStudioView cv) { cv.GetEditor()?.Redo(); return; }
        if (ActiveNotebookStudio != null && StudioHost.Pages.ViewFor(ActiveNotebookStudio) is CSharpNotebookStudioView nv) { nv.GetActiveEditor()?.Redo(); return; }
    }

    public void Cut_OnClick(object? sender, EventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox tb) { tb.Cut(); return; }
        if (GetFocusedTextEditor() is { } editor) { editor.Cut(); return; }
        if (ActiveCodeStudio != null && StudioHost.Pages.ViewFor(ActiveCodeStudio) is CSharpCodeStudioView cv) { cv.GetEditor()?.Cut(); return; }
        if (ActiveNotebookStudio != null && StudioHost.Pages.ViewFor(ActiveNotebookStudio) is CSharpNotebookStudioView nv) { nv.GetActiveEditor()?.Cut(); return; }
    }

    public void Copy_OnClick(object? sender, EventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox tb) { tb.Copy(); return; }
        if (GetFocusedTextEditor() is { } editor) { editor.Copy(); return; }
        if (ActiveCodeStudio != null && StudioHost.Pages.ViewFor(ActiveCodeStudio) is CSharpCodeStudioView cv) { cv.GetEditor()?.Copy(); return; }
        if (ActiveNotebookStudio != null && StudioHost.Pages.ViewFor(ActiveNotebookStudio) is CSharpNotebookStudioView nv) { nv.GetActiveEditor()?.Copy(); return; }
        if (FocusManager?.GetFocusedElement() is SelectableTextBlock stb && !string.IsNullOrEmpty(stb.SelectedText) && Clipboard != null)
        {
            _ = Clipboard.SetTextAsync(stb.SelectedText);
        }
    }

    public void Paste_OnClick(object? sender, EventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox tb) { tb.Paste(); return; }
        if (GetFocusedTextEditor() is { } editor) { editor.Paste(); return; }
        if (ActiveCodeStudio != null && StudioHost.Pages.ViewFor(ActiveCodeStudio) is CSharpCodeStudioView cv) { cv.GetEditor()?.Paste(); return; }
        if (ActiveNotebookStudio != null && StudioHost.Pages.ViewFor(ActiveNotebookStudio) is CSharpNotebookStudioView nv) { nv.GetActiveEditor()?.Paste(); return; }
    }

    public void SelectAll_OnClick(object? sender, EventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox tb) { tb.SelectAll(); return; }
        if (GetFocusedTextEditor() is { } editor) { editor.SelectAll(); return; }
        if (ActiveCodeStudio != null && StudioHost.Pages.ViewFor(ActiveCodeStudio) is CSharpCodeStudioView cv) { cv.GetEditor()?.SelectAll(); return; }
        if (ActiveNotebookStudio != null && StudioHost.Pages.ViewFor(ActiveNotebookStudio) is CSharpNotebookStudioView nv) { nv.GetActiveEditor()?.SelectAll(); return; }
    }

    public void Find_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) code.ToggleSearch();
        else if (ActiveNotebookStudio is { } nb) nb.SelectActivityBarItem(3);
        else FocusFirstActiveTextBox();
    }

    public void Replace_OnClick(object? sender, EventArgs e) => ActiveCodeStudio?.ToggleSearch();
    public void FindInFiles_OnClick(object? sender, EventArgs e) => ActiveCodeStudio?.SelectActivityBarItem(1);

    public void FormatDocument_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) code.FormatCode();
        else if (ActiveNotebookStudio is { } nb)
        {
            if (nb.ActiveTab?.ActiveCell is { } cell) cell.FormatCode();
            else nb.FormatAllCodeCells();
        }
    }

    public void FocusFirstActiveTextBox()
    {
        var target = this.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(tb => tb.IsVisible && tb.IsEnabled && tb.IsEffectivelyVisible);
        target?.Focus();
    }

    // ── View Menu Actions ──
    public void CommandPalette_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) code.ShowCommandPalette();
        else StudioHostVm.NavigateToSettings("Keymap");
    }

    public void ViewExplorer_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) code.SelectActivityBarItem(0);
        else if (ActiveNotebookStudio is { } nb) nb.SelectActivityBarItem(0);
    }

    public void ViewSearch_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) code.SelectActivityBarItem(1);
        else if (ActiveNotebookStudio is { } nb) nb.SelectActivityBarItem(3);
    }

    public void ViewSourceControl_OnClick(object? sender, EventArgs e) => ActiveCodeStudio?.SelectActivityBarItem(6);
    public void ViewRunDebug_OnClick(object? sender, EventArgs e) => ActiveCodeStudio?.SelectActivityBarItem(2);
    public void ViewNuGet_OnClick(object? sender, EventArgs e) => ActiveCodeStudio?.SelectActivityBarItem(3);
    public void ViewProblems_OnClick(object? sender, EventArgs e) => ActiveCodeStudio?.SelectActivityBarItem(5);

    public void ToggleSideBar_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) code.ToggleSideBar();
        else if (ActiveNotebookStudio is { } nb) nb.ToggleSideBar();
    }

    public void ToggleBottomDeck_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) code.ToggleBottomDeck();
    }

    public void ToggleAiComposer_OnClick(object? sender, EventArgs e)
    {
        StudioHostVm.ToggleAiComposerCommand.Execute(null);
    }

    public void ZoomIn_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) code.ZoomInCommand.Execute(null);
        else if (ActiveNotebookStudio is { } nb) nb.ZoomIn();
    }

    public void ZoomOut_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) code.ZoomOutCommand.Execute(null);
        else if (ActiveNotebookStudio is { } nb) nb.ZoomOut();
    }

    public void ResetZoom_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) code.ResetZoomCommand.Execute(null);
        else if (ActiveNotebookStudio is { } nb) nb.ResetZoom();
    }

    public void ReturnToHub_OnClick(object? sender, EventArgs e) => StudioHostVm.NavigateToManager();
    public void OpenDocs_OnClick(object? sender, EventArgs e) => StudioHostVm.NavigateToDocs();
    public void OpenBlind_OnClick(object? sender, EventArgs e) => StudioHostVm.NavigateToBlindProblems();
    public void OpenSettings_OnClick(object? sender, EventArgs e) => StudioHostVm.NavigateToSettings();
    public void OpenCustomizationSettings_OnClick(object? sender, EventArgs e) => StudioHostVm.NavigateToSettings("Customization");

    // ── Run Menu Actions ──
    public async void StartDebugging_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code && !code.IsExecuting && !code.IsDebugging)
        {
            await code.DebugCodeCommand.ExecuteAsync(null);
        }
    }

    public async void RunWithoutDebugging_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code && !code.IsExecuting && !code.IsDebugging)
        {
            await code.RunCodeCommand.ExecuteAsync(null);
        }
        else if (ActiveNotebookStudio is { } nb && !nb.IsExecuting)
        {
            if (nb.ActiveTab?.ActiveCell != null) nb.ActiveTab.ActiveCell.RunCellCommand.Execute(null);
            else await nb.RunAllCellsAsync();
        }
        else if (ActiveServerStudio is { } srv && !srv.IsServerRunning)
        {
            await srv.StartServerCommand.ExecuteAsync(null);
        }
    }

    public async void ApplyCustomization_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code)
        {
            await code.ApplyActiveTabAsCustomizationCommand.ExecuteAsync(null);
        }
    }

    public async void ReloadCustomizations_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code)
        {
            await code.ReloadCustomizationsCommand.ExecuteAsync(null);
        }
        else if (StudioHostVm.SettingsViewModel is { } settings)
        {
            await settings.ReloadCustomizationScriptCommand.ExecuteAsync(null);
        }
        else if (PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.CustomizationManager is { } mgr)
        {
            await mgr.ReloadAsync();
        }
    }

    public async void OpenUserInitScript_OnClick(object? sender, EventArgs e)
    {
        var mgr = PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.CustomizationManager;
        var scriptPath = mgr.Storage.GlobalInitScriptPath;
        await mgr.Storage.EnsureInitScriptExistsAsync();

        if (ActiveCodeStudio is { } code)
        {
            await code.OpenWorkspaceFileAsync(scriptPath);
        }
        else
        {
            var doc = new ScriptDocumentItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = Path.GetFileName(scriptPath),
                SourceFilePath = scriptPath,
                Code = File.Exists(scriptPath) ? await File.ReadAllTextAsync(scriptPath) : string.Empty
            };
            StudioHostVm.NavigateToCodeStudio(doc);
        }
    }

    public async void OpenWorkspaceInitScript_OnClick(object? sender, EventArgs e)
    {
        var mgr = PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.CustomizationManager;
        var wsPath = mgr.Storage.WorkspaceInitScriptPath;
        if (string.IsNullOrWhiteSpace(wsPath))
        {
            PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.UI.ShowWarning("No workspace folder is currently open.");
            return;
        }

        var dir = System.IO.Path.GetDirectoryName(wsPath);
        if (!string.IsNullOrWhiteSpace(dir) && !System.IO.Directory.Exists(dir))
        {
            System.IO.Directory.CreateDirectory(dir);
        }

        if (!System.IO.File.Exists(wsPath))
        {
            var starter = PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.Storage.CustomizationStorageService.GetStarterInitScript();
            await System.IO.File.WriteAllTextAsync(wsPath, starter);
        }

        if (ActiveCodeStudio is { } code)
        {
            await code.OpenWorkspaceFileAsync(wsPath);
        }
        else
        {
            var doc = new ScriptDocumentItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = Path.GetFileName(wsPath),
                SourceFilePath = wsPath,
                Code = File.Exists(wsPath) ? await File.ReadAllTextAsync(wsPath) : string.Empty
            };
            StudioHostVm.NavigateToCodeStudio(doc);
        }
    }

    public async void StopExecution_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code)
        {
            if (code.IsDebugging) code.StopDebug();
            else if (code.IsExecuting) code.StopCommand.Execute(null);
        }
        else if (ActiveNotebookStudio is { } nb && nb.IsExecuting)
        {
            nb.ActiveTab?.InterruptExecution();
        }
        else if (ActiveServerStudio is { } srv && srv.IsServerRunning)
        {
            await srv.StopServerCommand.ExecuteAsync(null);
        }
    }

    public void StepOver_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code && code.IsDebugging && code.IsPaused) code.StepOver();
    }

    public void StepInto_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code && code.IsDebugging && code.IsPaused) code.StepInto();
    }

    public void ToggleBreakpoint_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) code.ToggleBreakpoint(code.CaretLine);
    }

    public void ClearAllBreakpoints_OnClick(object? sender, EventArgs e) => ActiveCodeStudio?.ClearAllBreakpoints();

    // ── Window Menu Actions ──
    public void Minimize_OnClick(object? sender, EventArgs e) => WindowState = WindowState.Minimized;
    public void ToggleMaximize_OnClick(object? sender, EventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    public void BringAllToFront_OnClick(object? sender, EventArgs e) => Activate();

    // ── Help Menu Actions ──
    public void KeyboardShortcuts_OnClick(object? sender, EventArgs e)
    {
        if (ActiveCodeStudio is { } code) code.ShowCommandPalette();
        else StudioHostVm.NavigateToSettings("Keymap");
    }

    public async void ReportIssue_OnClick(object? sender, EventArgs e)
    {
        try { await Launcher.LaunchUriAsync(new Uri("https://github.com/CodeFryDev")); }
        catch { }
    }

    public void About_OnClick(object? sender, EventArgs e)
    {
        var about = new AboutWindow();
        about.OpenDocumentationAction = () => StudioHostVm.NavigateToDocs();
        about.ShowDialog(this);
    }
}
