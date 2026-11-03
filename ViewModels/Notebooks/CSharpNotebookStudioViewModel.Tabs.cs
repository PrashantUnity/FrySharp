using System.Diagnostics;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.Server;
using PdfEditorApp.Plugins.CSharpEditor.Services.Activities;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks;

public partial class CSharpNotebookStudioViewModel
{
    // ── Tab lifecycle ────────────────────────────────────────────────────────

    [RelayCommand]
    public void SelectTab(NotebookTabViewModel? tab)
    {
        if (tab == null) return;

        ActiveTab = tab;
        HighlightExplorerItem(tab.Title);

        var adapter = new Services.Extensibility.Editor.NotebookDocumentContextAdapter(this);
        Services.Extensibility.StudioAppContext.Instance.EditorService.ActiveDocumentResolver = () => adapter;
        Services.Extensibility.StudioAppContext.Instance.HookRegistry.InvokeDocumentOpened(adapter);
    }

    // ── Unsaved changes ──────────────────────────────────────────────────────

    private static bool IsUnsaved(NotebookTabViewModel tab) => tab.IsModified;

    /// <summary>The titles of the open notebooks with edits that aren't saved (closing the studio asks about them).</summary>
    public IReadOnlyList<string> UnsavedDocumentTitles => Tabs.Where(IsUnsaved).Select(t => t.Title).ToList();

    /// <summary>Saves every open notebook that has unsaved edits. False when one of them couldn't be saved.</summary>
    public async Task<bool> SaveAllAsync()
    {
        var allSaved = true;
        foreach (var tab in Tabs.Where(IsUnsaved).ToList())
        {
            allSaved &= await SaveTabAsync(tab);
        }

        return allSaved;
    }

    /// <summary>
    /// Runs <paramref name="close"/> at once when none of <paramref name="closing"/> has unsaved edits (or there is nobody to
    /// ask: tests, tools); otherwise asks first, and closes only after Save (and every save worked) or Don't Save.
    /// </summary>
    private void CloseWhenConfirmed(IEnumerable<NotebookTabViewModel> closing, Action close)
    {
        var unsaved = closing.Where(IsUnsaved).ToList();
        if (unsaved.Count == 0)
        {
            close();
            return;
        }

        PendingClose = ConfirmThenCloseAsync(unsaved, close);
        PendingClose.FireAndForget(_activities, "Closing notebooks");
    }

    /// <summary>The close that is waiting for the unsaved-changes answer, or its saves (tests await it).</summary>
    internal Task PendingClose { get; private set; } = Task.CompletedTask;

    private async Task ConfirmThenCloseAsync(List<NotebookTabViewModel> unsaved, Action close)
    {
        var answer = await StudioQuestions.AskToSaveAsync(_dialogs, unsaved.Select(t => t.Title).ToList());
        if (answer is not (null or StudioQuestions.DontSave or StudioQuestions.Save)) return;

        if (answer == StudioQuestions.Save)
        {
            foreach (var tab in unsaved)
            {
                if (!await SaveTabAsync(tab)) return;
            }
        }

        close();
    }

    [RelayCommand]
    public void CloseTab(NotebookTabViewModel? tab)
    {
        if (tab == null) return;
        CloseWhenConfirmed([tab], () => CloseTabNow(tab));
    }

    private void CloseTabNow(NotebookTabViewModel tab)
    {
        if (!Tabs.Contains(tab)) return;

        ReleaseTab(tab);

        var idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (ActiveTab == tab)
        {
            if (Tabs.Count > 0)
            {
                var nextIdx = Math.Min(idx, Tabs.Count - 1);
                SelectTab(Tabs[nextIdx]);
            }
            else
            {
                ActiveTab = null;
            }
        }
    }

    [RelayCommand]
    public async Task NewNotebookTab()
    {
        await Task.Yield();

        // Create an ephemeral (in-memory only) notebook — nothing is written to disk until the user
        // renames it (which triggers OnItemRenamedAsync → first save) or presses Ctrl+S.
        // This prevents "Notebook_065959.frynb" clutter from accumulating in the workspace folder.
        var newDoc = new NotebookDocumentItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "New Notebook",
            Description = "Interactive cell-based notebook",
            Category = "Interactive",
            Created = DateTime.UtcNow,
            LastModified = DateTime.UtcNow,
            IsEphemeral = true
        };
        newDoc.Cells.Add(new NotebookCellItem
        {
            Type = CellType.Markdown,
            Source = "# 📓 New Notebook\nWrite documentation or notes in this cell.",
            IsMarkdownPreviewMode = true
        });
        newDoc.Cells.Add(new NotebookCellItem
        {
            Type = CellType.Code,
            Source = "// C# Code Cell\nConsole.WriteLine(\"Hello from Notebook cell!\");"
        });

        var newTab = CreateTab(newDoc, "Library", $"{newDoc.Title}.frynb");

        ConfigureNotebookTab(newTab);
        Tabs.Add(newTab);
        SelectTab(newTab);
        RefreshQuickOpenDocuments();
        // Not saved yet, so not in the workspace folder: it appears in the Explorer once it is saved there.
    }

    // Every notebook tab runs its cells with the studio's languages, in the active workspace when it has no folder of its own.
    private NotebookTabViewModel CreateTab(NotebookDocumentItem notebook, string folderName, string filePath) =>
        new(notebook,
            folderName: folderName,
            filePath: filePath,
            onSelectTab: SelectTab,
            onCloseTab: CloseTab,
            getTimeoutSeconds: _getTimeoutSeconds,
            languages: _languages,
            workspaceRoot: () => _storageService.ActiveWorkspaceRootPath);

    /// <summary>
    /// Creates a tab and populates its cells on a background thread (<see cref="NotebookTabViewModel.PopulateCellsAsync"/>)
    /// so Bitmap decoding and snapshot materialization never block the UI thread.
    /// </summary>
    private async Task<NotebookTabViewModel> CreateTabAsync(
        NotebookDocumentItem notebook, string folderName, string filePath,
        CancellationToken ct = default)
    {
        var tab = new NotebookTabViewModel(
            notebook,
            folderName: folderName,
            filePath: filePath,
            onSelectTab: SelectTab,
            onCloseTab: CloseTab,
            getTimeoutSeconds: _getTimeoutSeconds,
            languages: _languages,
            workspaceRoot: () => _storageService.ActiveWorkspaceRootPath,
            skipInitialPopulate: true); // cells are loaded below, off the UI thread
        await tab.PopulateCellsAsync(ct);
        return tab;
    }

    /// <summary>A closed tab's cells let go of their live outputs, and its kernels in other programs end.</summary>
    private static void ReleaseTab(NotebookTabViewModel tab)
    {
        // A cell still running would keep going with nothing left to show it.
        tab.InterruptExecution();
        tab.DisposeAllCellResources();
        tab.ShutdownKernels();
    }

    private void ConfigureNotebookTab(NotebookTabViewModel tab)
    {
        tab.OnCloseOthers = t => CloseOtherTabs(t);
        tab.OnCloseToTheRight = t => CloseTabsToTheRight(t);
        tab.OnCloseAll = _ => CloseAllTabs();
        tab.OnCopyPath = t => CopyNotebookTabPath(t);
        tab.OnRevealInExplorer = t => RevealNotebookTabInExplorer(t);
    }

    [RelayCommand]
    public void CloseOtherTabs(NotebookTabViewModel? tab)
    {
        if (tab == null || Tabs.Count <= 1) return;
        var toRemove = Tabs.Where(t => t != tab).ToList();
        CloseWhenConfirmed(toRemove, () =>
        {
            foreach (var t in toRemove)
            {
                ReleaseTab(t);
                Tabs.Remove(t);
            }
            if (ActiveTab != tab)
            {
                SelectTab(tab);
            }
            RefreshQuickOpenDocuments();
        });
    }

    [RelayCommand]
    public void CloseTabsToTheRight(NotebookTabViewModel? tab)
    {
        if (tab == null) return;
        int idx = Tabs.IndexOf(tab);
        if (idx < 0 || idx >= Tabs.Count - 1) return;
        var toRemove = Tabs.Skip(idx + 1).ToList();
        CloseWhenConfirmed(toRemove, () =>
        {
            foreach (var t in toRemove)
            {
                ReleaseTab(t);
                Tabs.Remove(t);
            }
            if (ActiveTab != null && !Tabs.Contains(ActiveTab))
            {
                SelectTab(tab);
            }
            RefreshQuickOpenDocuments();
        });
    }

    [RelayCommand]
    public void CloseAllTabs()
    {
        CloseWhenConfirmed(Tabs.ToList(), () =>
        {
            foreach (var t in Tabs)
            {
                ReleaseTab(t);
            }
            Tabs.Clear();
            _ = NewNotebookTab();
            RefreshQuickOpenDocuments();
        });
    }

    public void CopyNotebookTabPath(NotebookTabViewModel? tab)
    {
        if (tab == null) return;
        try
        {
            var text = !string.IsNullOrEmpty(tab.FilePath) ? tab.FilePath : tab.Title;
            _ = CopyTextToClipboardAsync(text);
        }
        catch
        {
        }
    }

    public void RevealNotebookTabInExplorer(NotebookTabViewModel? tab)
    {
        if (tab == null) return;
        SelectedActivityBarIndex = 0; // Explorer
        IsSideBarVisible = true;
        HighlightExplorerItem(tab.Title);
    }

    // ── Active notebook switching ────────────────────────────────────────────

    public void UpdateActiveNotebook(NotebookDocumentItem notebook)
    {
        Notebook = notebook;

        var expItem = FindDocumentInExplorer(notebook);

        // The same document, by id: copies of a notebook keep its title, so a title match picked the wrong tab.
        var existingTab = Tabs.FirstOrDefault(t => !string.IsNullOrEmpty(notebook.Id)
            ? string.Equals(t.Notebook.Id, notebook.Id, StringComparison.OrdinalIgnoreCase)
            : string.Equals(t.Notebook.Title, notebook.Title, StringComparison.OrdinalIgnoreCase));

        if (existingTab != null)
        {
            SelectTab(existingTab);
        }
        else
        {
            var folder = expItem?.Parent?.Name ?? "Library";
            var newTab = CreateTab(notebook, folder, expItem?.FullPath ?? $"{notebook.Title}.frynb");

            ConfigureNotebookTab(newTab);
            Tabs.Add(newTab);
            SelectTab(newTab);
            RefreshQuickOpenDocuments();
        }
    }

    // ── Export ───────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task ExportActiveNotebookAsIpynbAsync()
    {
        if (ActiveTab == null) return;
        var content = DocumentExportService.ExportNotebookToIpynb(ActiveTab.Notebook, _languages.Registry);
        await CopyTextToClipboardAsync(content);
        Debug.WriteLine($"[CSharpEditorPlugin] Notebook '{ActiveTab.Title}' exported to Jupyter .ipynb and copied to clipboard!");
    }

    [RelayCommand]
    public async Task ExportActiveNotebookAsMarkdownAsync()
    {
        if (ActiveTab == null) return;
        var content = DocumentExportService.ExportNotebookToMarkdown(ActiveTab.Notebook, _languages.Registry);
        await CopyTextToClipboardAsync(content);
        Debug.WriteLine($"[CSharpEditorPlugin] Notebook '{ActiveTab.Title}' exported to Markdown .md and copied to clipboard!");
    }

    // ── Clipboard helper ─────────────────────────────────────────────────────

    private static async Task CopyTextToClipboardAsync(string text)
    {
        try
        {
            IClipboard? clipboard = Avalonia.Application.Current?.ApplicationLifetime switch
            {
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop =>
                    desktop.Windows.FirstOrDefault(w => w.IsActive)?.Clipboard ?? desktop.MainWindow?.Clipboard,
                Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView =>
                    Avalonia.Controls.TopLevel.GetTopLevel(singleView.MainView)?.Clipboard,
                _ => null
            };
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch
        {
        }
    }

    // ── Quick Open / Command Palette ─────────────────────────────────────────

    [RelayCommand]
    public void ShowQuickOpen(string? mode = null)
    {
        RefreshQuickOpenDocuments();
        InitializeQuickOpenCommands();
        var qMode = mode?.ToLowerInvariant() switch
        {
            "commands" => QuickOpenMode.Commands,
            "line" => QuickOpenMode.GoToLine,
            _ => QuickOpenMode.Files
        };
        QuickOpen.Show(qMode);
    }

    [RelayCommand]
    public void ShowCommandPalette() => ShowQuickOpen("commands");

    private void InitializeQuickOpenCommands()
    {
        var cmds = new List<QuickOpenItem>
        {
            new() { Title = "Notebook: Run Active Cell", Subtitle = "Execute currently selected cell (Shift+Enter)", Category = "Notebook", IconKind = "Play", IconColorHex = "#75D59A", ShortcutHint = "Shift+Enter", ExecuteAction = () => { if (ActiveTab?.ActiveCell != null) ActiveTab.ActiveCell.RunCellCommand.Execute(null); } },
            new() { Title = "Notebook: Run All Cells", Subtitle = "Sequential execution of all code cells", Category = "Notebook", IconKind = "FastForward", IconColorHex = "#75D59A", ShortcutHint = "Ctrl+Shift+Enter", ExecuteAction = () => { _ = RunAllCellsAsync(); } },
            new() { Title = "Notebook: Add Code Cell Below", Subtitle = "Insert a new C# code cell below active cell", Category = "Notebook", IconKind = "CodeBraces", IconColorHex = "#58A6FF", ExecuteAction = () => AddCodeCell(ActiveTab?.ActiveCell) },
            new() { Title = "Notebook: Add Markdown Cell", Subtitle = "Insert a new documentation cell", Category = "Notebook", IconKind = "FormatHeaderPound", IconColorHex = "#4EC9B0", ExecuteAction = () => AddMarkdownCell(ActiveTab?.ActiveCell) },
            new() { Title = "Notebook: Format All Cells", Subtitle = "Format C# code across all cells", Category = "Notebook", IconKind = "FormatPaint", IconColorHex = "#75D59A", ExecuteAction = FormatAllCodeCells },
            new() { Title = "Notebook: Clear All Outputs", Subtitle = "Clear stdout, stderr, and rich visuals", Category = "Notebook", IconKind = "Broom", IconColorHex = "#8B949E", ExecuteAction = ClearAllOutputs },
            new() { Title = "Export: Export to Jupyter Notebook (.ipynb)", Subtitle = "Copy standard Jupyter v4 JSON to clipboard", Category = "Export", IconKind = "ExportVariant", IconColorHex = "#D97706", ExecuteAction = () => _ = ExportActiveNotebookAsIpynbAsync() },
            new() { Title = "Export: Export to Markdown (.md)", Subtitle = "Copy GitHub Markdown formatted document to clipboard", Category = "Export", IconKind = "ExportVariant", IconColorHex = "#75D59A", ExecuteAction = () => _ = ExportActiveNotebookAsMarkdownAsync() },
            new() { Title = "File: Save Notebook", Subtitle = "Persist current notebook changes", Category = "File", IconKind = "ContentSaveOutline", IconColorHex = "#58A6FF", ShortcutHint = "Ctrl+S", ExecuteAction = () => { _ = SaveAsync(); } },
            new() { Title = "File: New Notebook Tab", Subtitle = "Open a new interactive notebook tab", Category = "File", IconKind = "FilePlusOutline", IconColorHex = "#58A6FF", ShortcutHint = "Ctrl+N", ExecuteAction = () => { _ = NewNotebookTab(); } },
            new() { Title = "File: Close Active Tab", Subtitle = "Close the current notebook tab", Category = "Tabs", IconKind = "Close", IconColorHex = "#E5534B", ShortcutHint = "Ctrl+W", ExecuteAction = () => CloseTab(ActiveTab) },
            new() { Title = "File: Close Other Tabs", Subtitle = "Close all tabs except active", Category = "Tabs", IconKind = "CloseBoxMultipleOutline", IconColorHex = "#E5534B", ExecuteAction = () => CloseOtherTabs(ActiveTab) },
            new() { Title = "File: Close All Tabs", Subtitle = "Close all open notebook tabs", Category = "Tabs", IconKind = "CloseCircleMultipleOutline", IconColorHex = "#E5534B", ExecuteAction = CloseAllTabs },
            new() { Title = "Preferences: Open User Customization Script (init.csx)", Subtitle = "Open ~/.frysharp/init.csx to configure themes, shortcuts, and startup logic", Category = "Preferences", IconKind = "CogOutline", IconColorHex = "#A371F7", ExecuteAction = () => { _ = OpenUserInitScriptAsync(); } },
            new() { Title = "Preferences: Open Workspace Customization Script (.frysharp/init.csx)", Subtitle = "Open workspace .frysharp/init.csx for project-specific customization", Category = "Preferences", IconKind = "FolderCogOutline", IconColorHex = "#A371F7", ExecuteAction = () => { _ = OpenWorkspaceInitScriptAsync(); } },
            new() { Title = "Customization: Apply Active Cell as Customization", Subtitle = "Directly execute active cell code to hot-reload customization state", Category = "Customization", IconKind = "PlayCircleOutline", IconColorHex = "#75D59A", ShortcutHint = "Ctrl+Alt+R", ExecuteAction = () => { _ = ApplyActiveCellAsCustomizationAsync(); } },
            new() { Title = "Customization: Reload Customizations (~/.frysharp/init.csx)", Subtitle = "Recompile and apply user customization script and theme tokens", Category = "Customization", IconKind = "Refresh", IconColorHex = "#A371F7", ShortcutHint = "Ctrl+Shift+R", ExecuteAction = () => { _ = ReloadCustomizationsAsync(); } },
            new() { Title = "Customization: Theme - Dark+ (Default)", Subtitle = "Apply Dark+ standard modern palette", Category = "Customization", IconKind = "PaletteOutline", IconColorHex = "#2F81F7", ExecuteAction = () => Services.Extensibility.StudioAppContext.Instance.Themes.ApplyTheme("dark-plus") },
            new() { Title = "Customization: Theme - Light+ (Default)", Subtitle = "Apply Light+ standard modern palette", Category = "Customization", IconKind = "PaletteOutline", IconColorHex = "#0969DA", ExecuteAction = () => Services.Extensibility.StudioAppContext.Instance.Themes.ApplyTheme("light-plus") },
            new() { Title = "Customization: Theme - Dracula Pro", Subtitle = "Apply Dracula vibrant purple palette", Category = "Customization", IconKind = "PaletteOutline", IconColorHex = "#BD93F9", ExecuteAction = () => Services.Extensibility.StudioAppContext.Instance.Themes.ApplyTheme("dracula") },
            new() { Title = "Customization: Theme - Cyberpunk Neon", Subtitle = "Apply Cyberpunk electric yellow & neon cyan palette", Category = "Customization", IconKind = "PaletteOutline", IconColorHex = "#FFE600", ExecuteAction = () => Services.Extensibility.StudioAppContext.Instance.Themes.ApplyTheme("cyberpunk") },
            new() { Title = "Customization: Theme - Monokai Classic", Subtitle = "Apply Monokai high-contrast warm palette", Category = "Customization", IconKind = "PaletteOutline", IconColorHex = "#A6E22E", ExecuteAction = () => Services.Extensibility.StudioAppContext.Instance.Themes.ApplyTheme("monokai") },
            new() { Title = "Customization: Theme - One Dark Pro", Subtitle = "Apply Atom One Dark iconic balanced dark palette", Category = "Customization", IconKind = "PaletteOutline", IconColorHex = "#61AFEF", ExecuteAction = () => Services.Extensibility.StudioAppContext.Instance.Themes.ApplyTheme("one-dark") },
            new() { Title = "Customization: Reset Theme to Defaults", Subtitle = "Clear color overrides and reset to active theme defaults", Category = "Customization", IconKind = "Restore", IconColorHex = "#E5534B", ExecuteAction = () => Services.Extensibility.StudioAppContext.Instance.Themes.ResetToDefaults() },
            new() { Title = "View: Zoom In (Increase Font Size)", Subtitle = "Increase notebook cell typography size", Category = "View", IconKind = "MagnifyPlusOutline", IconColorHex = "#75D59A", ShortcutHint = "Ctrl+=", ExecuteAction = ZoomIn },
            new() { Title = "View: Zoom Out (Decrease Font Size)", Subtitle = "Decrease notebook cell typography size", Category = "View", IconKind = "MagnifyMinusOutline", IconColorHex = "#58A6FF", ShortcutHint = "Ctrl+-", ExecuteAction = ZoomOut },
            new() { Title = "View: Reset Font Zoom", Subtitle = "Reset typography to default 100% (13px)", Category = "View", IconKind = "MagnifyScan", IconColorHex = "#D97706", ShortcutHint = "Ctrl+0", ExecuteAction = ResetZoom },
            new() { Title = "View: Toggle Primary Side Bar", Subtitle = "Expand or collapse activity sidebar", Category = "View", IconKind = "DockLeft", IconColorHex = "#58A6FF", ShortcutHint = "Ctrl+B", ExecuteAction = ToggleSideBar },
            new() { Title = "View: Show Explorer", Subtitle = "Browse workspace notebooks and scripts", Category = "Navigation", IconKind = "FolderMultipleOutline", IconColorHex = "#D97706", ShortcutHint = "Ctrl+Shift+E", ExecuteAction = () => SelectActivityBarItem(0) },
            new() { Title = "View: Show Outline", Subtitle = "Navigate cells in table of contents", Category = "Navigation", IconKind = "FormatListBulleted", IconColorHex = "#58A6FF", ShortcutHint = "Ctrl+Shift+O", ExecuteAction = () => SelectActivityBarItem(1) },
            new() { Title = "View: Show Live Variables", Subtitle = "Inspect session state and memory values", Category = "Navigation", IconKind = "VariableBox", IconColorHex = "#75D59A", ShortcutHint = "Ctrl+Shift+V", ExecuteAction = () => SelectActivityBarItem(2) },
            new() { Title = "Hub: Return to Workspace Manager", Subtitle = "Navigate back to Hub dashboard", Category = "Navigation", IconKind = "HomeOutline", IconColorHex = "#58A6FF", ExecuteAction = BackToHub }
        };

        foreach (var desc in Services.Extensibility.StudioAppContext.Instance.CommandPipeline.Descriptors)
        {
            cmds.Add(new QuickOpenItem
            {
                Title = $"{desc.Category ?? "Extension"}: {desc.Title}",
                Subtitle = desc.Id,
                Category = desc.Category ?? "Extension",
                IconKind = "ToyBrickOutline",
                IconColorHex = "#A371F7",
                ShortcutHint = desc.Shortcut ?? string.Empty,
                ExecuteAction = desc.Action
            });
        }

        QuickOpen.RegisterCommands(cmds);
    }

    public void RefreshQuickOpenDocuments()
    {
        var docs = new List<QuickOpenItem>();

        foreach (var tab in Tabs)
        {
            docs.Add(new QuickOpenItem
            {
                Title = tab.Title,
                Subtitle = tab.IsActive ? "Currently Active Notebook" : "Open Tab",
                Category = "Open Tabs",
                IconKind = "NotebookOutline",
                IconColorHex = "#D97706",
                Kind = QuickOpenItemKind.Document,
                ExecuteAction = () => SelectTab(tab)
            });
        }

        QuickOpen.RegisterDocuments(docs);
    }
}
