using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;

public partial class CSharpCodeStudioViewModel
{
    private Common.StudioTabItemViewModel CreateTab(ScriptDocumentItem document, bool isActive = false)
    {
        var sourceLanguage = (document.SourceFilePath != null || !string.Equals(document.LanguageId, PdfEditorApp.Plugins.CSharpEditor.Services.Languages.LanguageIds.CSharp, StringComparison.OrdinalIgnoreCase))
            ? _languages.LanguageOf(document)
            : null;

        var iconKind = sourceLanguage?.IconKind;
        var iconColor = sourceLanguage?.AccentHex;

        if (sourceLanguage == null || sourceLanguage.Id == PdfEditorApp.Plugins.CSharpEditor.Services.Languages.LanguageIds.Text)
        {
            var ext = Path.GetExtension(document.SourceFilePath ?? document.Title);
            if (!string.IsNullOrEmpty(ext))
            {
                var (extIcon, extColor) = Explorer.ExplorerItemViewModel.IconForExtension(ext);
                iconKind = extIcon;
                iconColor = extColor;
            }
        }

        return new Common.StudioTabItemViewModel(document, isActive)
        {
            LanguageIconKind = iconKind,
            LanguageIconColor = iconColor,
            OnSelect = t =>
            {
                // Picking a tab wins over a file that is still loading.
                _fileOpen.CancelCurrent();
                SafeTabAction(() => SwitchToTabAsync(t), "Switching tab");
            },
            OnClose = t => SafeTabAction(() => CloseTabAsync(t), "Closing tab"),
            OnCloseOthers = t => SafeTabAction(() => CloseOtherTabsAsync(t), "Closing tabs"),
            OnCloseToTheRight = t => SafeTabAction(() => CloseTabsToTheRightAsync(t), "Closing tabs"),
            OnCloseAll = _ => SafeTabAction(() => CloseAllTabsAsync(), "Closing tabs"),
            OnCopyPath = t => CopyTabPath(t),
            OnRevealInExplorer = t => RevealTabInExplorer(t)
        };
    }

    // Tab clicks are fire-and-forget; a failure is shown to the user instead of only reaching the debug log.
    private void SafeTabAction(Func<Task> action, string actionName) =>
        Services.Activities.ActivityExtensions.FireAndForget(action(), _activities, actionName);

    // A tab switch puts the tab's lists back: a list that can take them at once raises one change, not one per item (a
    // tab with hundreds of problems or outputs redrew its panel hundreds of times).
    private static void CopyItems<T>(ICollection<T> target, IEnumerable<T> source)
    {
        if (target is RangeObservableCollection<T> range)
        {
            range.ReplaceAll(source as IReadOnlyList<T> ?? source.ToList());
            return;
        }

        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    public async Task SwitchToTabAsync(Common.StudioTabItemViewModel tab)
    {
        if (tab.Id == Script.Id && tab.IsActive) return;

        // 1. Save state of current active tab
        var currentTab = OpenTabs.FirstOrDefault(t => t.Id == Script.Id);
        if (currentTab != null)
        {
            currentTab.Document.Code = Code;
            currentTab.Document.Notes = Notes;
            currentTab.ConsoleHeader = ConsoleHeader;
            currentTab.ConsoleBody = ConsoleBody;
            currentTab.ConsoleFooter = ConsoleFooter;
            currentTab.ConsoleExitCode = ConsoleExitCode;
            currentTab.ConsoleOutput = ConsoleOutput;
            currentTab.ExecutionTimeText = ExecutionTimeText;
            currentTab.CompilerStatusText = CompilerStatusText;
            currentTab.PausedLine = CurrentPausedLine;
            currentTab.IsExecuting = IsExecuting;
            currentTab.IsDebugging = IsDebugging;
            currentTab.IsPaused = IsPaused;
            currentTab.SelectedBottomTabIndex = SelectedBottomTabIndex;
            currentTab.ImageZoomFactor = ImageZoomFactor;
            currentTab.ImageFitToWindow = ImageFitToWindow;
            currentTab.ShowImageCodeDrawer = ShowImageCodeDrawer;
            currentTab.IsDocumentPreviewMode = IsDocumentPreviewMode;
            currentTab.CsvTable = ActiveCsvTable;
            currentTab.CsvDimensionsSummary = CsvDimensionsSummary;
            currentTab.CsvDelimiterSummary = CsvDelimiterSummary;
            CopyItems(currentTab.Diagnostics, Diagnostics);
            CopyItems(currentTab.DumpResults, DumpResults);
            CopyItems(currentTab.RichOutputs, RichOutputs);
            CopyItems(currentTab.Locals, Locals);
            CopyItems(currentTab.CallStack, CallStack);
        }

        // 2. Mark active flags
        foreach (var t in OpenTabs)
        {
            t.IsActive = (t.Id == tab.Id);
        }

        // 3. Restore target tab state into active studio context
        Script = tab.Document;
        _isRestoringTabState = true;
        try
        {
            Code = tab.Document.Code ?? string.Empty;
            Notes = tab.Document.Notes;
        }
        finally
        {
            _isRestoringTabState = false;
        }

        UpdateImageStateForDocument(tab.Document);
        UpdatePreviewStateForDocument(tab.Document, tab.IsDocumentPreviewMode);
        UpdateDiffStateForTab(tab);

        IsNotesPreviewMode = !string.IsNullOrWhiteSpace(Notes);
        SelectedLanguageModeIndex = tab.Document.ExecutionMode switch
        {
            "Program" => 1,
            "Expression" => 2,
            _ => 0
        };

        ConsoleHeader = tab.ConsoleHeader;
        ConsoleBody = tab.ConsoleBody;
        ConsoleFooter = tab.ConsoleFooter;
        ConsoleExitCode = tab.ConsoleExitCode;
        ConsoleOutput = tab.ConsoleOutput;
        ExecutionTimeText = tab.ExecutionTimeText;
        CompilerStatusText = tab.CompilerStatusText;
        CurrentPausedLine = tab.PausedLine;
        IsExecuting = tab.IsExecuting;
        IsDebugging = tab.IsDebugging;
        IsPaused = tab.IsPaused;
        SelectedBottomTabIndex = tab.SelectedBottomTabIndex;
        IsAcceptingProgramInput = (tab.ActiveRun is { AcceptsInput: true } || tab.InProcessStdin != null) && SupportsStandardInput;

        CopyItems(Diagnostics, tab.Diagnostics);
        CopyItems(DumpResults, tab.DumpResults);
        CopyItems(RichOutputs, tab.RichOutputs);
        CopyItems(Locals, tab.Locals);
        CopyItems(CallStack, tab.CallStack);
        CopyItems(TestCases, tab.Document.TestCases);

        Breakpoints.Clear();
        foreach (var bpLine in tab.Document.Breakpoints)
        {
            Breakpoints.Add(new BreakpointItem { LineNumber = bpLine, IsEnabled = true });
        }

        RequestSwitchTabDocument?.Invoke(tab);
        RequestSyncBreakpoints?.Invoke(Breakpoints.Where(b => b.IsEnabled).Select(b => b.LineNumber));
        RequestSetPausedLine?.Invoke(CurrentPausedLine > 0 ? CurrentPausedLine : -1);
        RequestReloadEditorText?.Invoke();

        ErrorCount = Diagnostics.Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        WarningCount = Diagnostics.Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);

        if (Diagnostics.Count == 0 && !string.IsNullOrWhiteSpace(Code))
        {
            TriggerDiagnosticsCheck();
        }

        UpdateChangedOnDiskNotice();

        // The tree only needs rebuilding if the workspace changed (a script created in the Hub, say), not on every switch.
        await RefreshExplorerIfStaleAsync();

        PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.HookRegistry.InvokeDocumentOpened(
            new Services.Extensibility.Editor.StudioDocumentContextAdapter(this));
    }

    // ── Unsaved changes ────────────────────────────────────────────────────────────────────────────────────────────

    private static bool IsUnsaved(Common.StudioTabItemViewModel tab) =>
        tab.IsDirty && !tab.Document.IsReadOnly && !tab.Id.StartsWith("git-diff:", StringComparison.OrdinalIgnoreCase);

    /// <summary>The titles of the open documents with edits that aren't saved (closing the studio asks about them).</summary>
    public IReadOnlyList<string> UnsavedDocumentTitles => OpenTabs.Where(IsUnsaved).Select(t => t.Title).ToList();

    /// <summary>Saves every open document that has unsaved edits. False when one of them couldn't be saved.</summary>
    public async Task<bool> SaveAllAsync()
    {
        var allSaved = true;
        foreach (var tab in OpenTabs.Where(IsUnsaved).ToList())
        {
            allSaved &= await SaveTabAsync(tab);
        }

        return allSaved;
    }

    /// <summary>
    /// Before tabs close: if any has unsaved edits, asks whether to save them, drop them, or keep the tabs open. False when
    /// the tabs must stay (the user cancelled, or a save failed). With nobody to ask (tests, tools) the tabs close as before.
    /// </summary>
    private async Task<bool> ConfirmCloseAsync(IEnumerable<Common.StudioTabItemViewModel> closing)
    {
        var unsaved = closing.Where(IsUnsaved).ToList();
        if (unsaved.Count == 0) return true;

        var answer = await StudioQuestions.AskToSaveAsync(_dialogs, unsaved.Select(t => t.Title).ToList());
        if (answer is null or StudioQuestions.DontSave) return true;
        if (answer != StudioQuestions.Save) return false;

        foreach (var tab in unsaved)
        {
            if (!await SaveTabAsync(tab)) return false;
        }

        return true;
    }

    public async Task CloseTabAsync(Common.StudioTabItemViewModel tab)
    {
        if (!await ConfirmCloseAsync([tab])) return;

        if (OpenTabs.Count <= 1)
        {
            var freshScript = new ScriptDocumentItem
            {
                Title = "Untitled Script",
                Code = "// Welcome to FrySharp\nConsole.WriteLine(\"Hello, World!\");\n",
                Notes = string.Empty
            };
            var freshTab = CreateTab(freshScript, isActive: true);
            OpenTabs.Add(freshTab);
            await SwitchToTabAsync(freshTab);
        }

        int index = OpenTabs.IndexOf(tab);
        if (index >= 0)
        {
            StopTabRun(tab);
            OpenTabs.Remove(tab);
            if (tab.IsActive && OpenTabs.Count > 0)
            {
                var nextTab = (index < OpenTabs.Count) ? OpenTabs[index] : OpenTabs[^1];
                await SwitchToTabAsync(nextTab);
            }
        }
        RefreshQuickOpenDocuments();
    }

    public async Task CloseOtherTabsAsync(Common.StudioTabItemViewModel tab)
    {
        if (OpenTabs.Count <= 1) return;
        var toRemove = OpenTabs.Where(t => t != tab).ToList();
        if (!await ConfirmCloseAsync(toRemove)) return;
        foreach (var t in toRemove)
        {
            StopTabRun(t);
            OpenTabs.Remove(t);
        }
        if (!tab.IsActive)
        {
            await SwitchToTabAsync(tab);
        }
        RefreshQuickOpenDocuments();
    }

    public async Task CloseTabsToTheRightAsync(Common.StudioTabItemViewModel tab)
    {
        int index = OpenTabs.IndexOf(tab);
        if (index < 0 || index >= OpenTabs.Count - 1) return;

        var toRemove = OpenTabs.Skip(index + 1).ToList();
        if (!await ConfirmCloseAsync(toRemove)) return;
        foreach (var t in toRemove)
        {
            StopTabRun(t);
            OpenTabs.Remove(t);
        }
        if (!tab.IsActive && !OpenTabs.Any(t => t.IsActive))
        {
            await SwitchToTabAsync(tab);
        }
        RefreshQuickOpenDocuments();
    }

    public async Task CloseAllTabsAsync()
    {
        if (!await ConfirmCloseAsync(OpenTabs.ToList())) return;

        var freshScript = new ScriptDocumentItem
        {
            Title = "Untitled Script",
            Code = "// Welcome to FrySharp\nConsole.WriteLine(\"Hello, World!\");\n",
            Notes = string.Empty
        };
        var freshTab = CreateTab(freshScript, isActive: true);
        foreach (var t in OpenTabs) StopTabRun(t);
        OpenTabs.Clear();
        OpenTabs.Add(freshTab);
        await SwitchToTabAsync(freshTab);
        RefreshQuickOpenDocuments();
    }

    public void CopyTabPath(Common.StudioTabItemViewModel tab)
    {
        try
        {
            var path = tab.Document.SourceFilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                var relative = _storageService.GetWorkspaceRelativePath(tab.Id);
                if (!string.IsNullOrWhiteSpace(relative) && !string.IsNullOrWhiteSpace(_storageService.ActiveWorkspaceRootPath))
                {
                    path = Path.Combine(_storageService.ActiveWorkspaceRootPath, relative.Replace('/', Path.DirectorySeparatorChar));
                }
                else if (!string.IsNullOrWhiteSpace(_storageService.ActiveWorkspaceRootPath) && !string.IsNullOrWhiteSpace(tab.Document.Title))
                {
                    path = Path.Combine(_storageService.ActiveWorkspaceRootPath, tab.Document.Title);
                }
                else
                {
                    path = !string.IsNullOrEmpty(tab.Document.Title) ? tab.Document.Title : "Untitled Script";
                }
            }
            _ = CopyTextToClipboardAsync(path);
            CompilerStatusText = $"Copied path to clipboard: {path}";
        }
        catch
        {
        }
    }

    public void RevealTabInExplorer(Common.StudioTabItemViewModel tab)
    {
        SelectedActivityBarIndex = 0; // Explorer
        IsSideBarVisible = true;
        HighlightExplorerItem(tab.Id, tab.Document.Title, tab.Document.SourceFilePath);
    }

    [RelayCommand]
    public void ShowQuickOpen(string? mode)
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

    [RelayCommand]
    public void ShowGoToLine() => ShowQuickOpen("line");

    [RelayCommand]
    public async Task ExportScriptToCsAsync()
    {
        if (Script == null || Script.SourceFilePath != null) return;
        var content = DocumentExportService.ExportScriptToCs(Script);
        await CopyTextToClipboardAsync(content);
        ConsoleOutput += $"\n[Export] Script '{Script.Title}' exported to standalone C# source (.cs) and copied to clipboard!\n";
    }

    [RelayCommand]
    public async Task ExportScriptToCsxAsync()
    {
        if (Script == null || Script.SourceFilePath != null) return;
        var content = DocumentExportService.ExportScriptToCsx(Script);
        await CopyTextToClipboardAsync(content);
        ConsoleOutput += $"\n[Export] Script '{Script.Title}' exported to C# Script (.csx) and copied to clipboard!\n";
    }

    private static async Task CopyTextToClipboardAsync(string text)
    {
        try
        {
            var clipboard = Avalonia.Application.Current?.ApplicationLifetime switch
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

    private void InitializeQuickOpenCommands()
    {
        var cmds = new List<QuickOpenItem>
        {
            new() { Title = "Run: Execute Script", Subtitle = "Compile and execute active script without debugging", Category = "Run", IconKind = "Play", IconColorHex = "#75D59A", ShortcutHint = "Ctrl+F5", ExecuteAction = () => _ = RunCodeCommand.ExecuteAsync(null) },
            new() { Title = "Debug: Start Debugging", Subtitle = "Compile with instrumentation and debug", Category = "Debug", IconKind = "BugPlayOutline", IconColorHex = "#58A6FF", ShortcutHint = "F5", ExecuteAction = () => _ = DebugCodeCommand.ExecuteAsync(null) },
            new() { Title = "Debug: Step Over", Subtitle = "Step to next statement", Category = "Debug", IconKind = "DebugStepOver", IconColorHex = "#D97706", ShortcutHint = "F10", ExecuteAction = StepOver },
            new() { Title = "Debug: Step Into", Subtitle = "Step into expression or function", Category = "Debug", IconKind = "DebugStepInto", IconColorHex = "#D97706", ShortcutHint = "F11", ExecuteAction = StepInto },
            new() { Title = "Debug: Stop Debugging", Subtitle = "Terminate active debug session", Category = "Debug", IconKind = "Stop", IconColorHex = "#E5534B", ShortcutHint = "Shift+F5", ExecuteAction = StopDebug },
            new() { Title = "File: Save Active Script", Subtitle = "Persist current script changes to storage", Category = "File", IconKind = "ContentSaveOutline", IconColorHex = "#58A6FF", ShortcutHint = "Ctrl+S", ExecuteAction = () => _ = SaveCommand.ExecuteAsync(null) },
            new() { Title = "File: New Script", Subtitle = "Create a new C# script tab", Category = "File", IconKind = "FilePlusOutline", IconColorHex = "#58A6FF", ShortcutHint = "Ctrl+N", ExecuteAction = () => _ = NewScriptCommand.ExecuteAsync(null) },
            new() { Title = "File: New Interactive Notebook", Subtitle = "Create a new interactive notebook (.frynb)", Category = "File", IconKind = "NotebookOutline", IconColorHex = "#FB923C", ExecuteAction = () => _ = NewNotebookCommand.ExecuteAsync(null) },
            new() { Title = "File: New API Server", Subtitle = "Create a new ASP.NET Core API server (.fryserver)", Category = "File", IconKind = "ServerNetwork", IconColorHex = "#2DD4BF", ExecuteAction = () => _ = NewServerCommand.ExecuteAsync(null) },
            new() { Title = "File: Close Active Tab", Subtitle = "Close the currently focused script tab", Category = "Tabs", IconKind = "Close", IconColorHex = "#E5534B", ShortcutHint = "Ctrl+W", ExecuteAction = () => { var a = OpenTabs.FirstOrDefault(t => t.IsActive); if (a != null) _ = CloseTabAsync(a); } },
            new() { Title = "File: Close Other Tabs", Subtitle = "Close all tabs except the active one", Category = "Tabs", IconKind = "CloseBoxMultipleOutline", IconColorHex = "#E5534B", ExecuteAction = () => { var a = OpenTabs.FirstOrDefault(t => t.IsActive); if (a != null) _ = CloseOtherTabsAsync(a); } },
            new() { Title = "File: Close All Tabs", Subtitle = "Close all open script tabs", Category = "Tabs", IconKind = "CloseCircleMultipleOutline", IconColorHex = "#E5534B", ExecuteAction = () => _ = CloseAllTabsAsync() },
            new() { Title = "Format: Format Document", Subtitle = "Format C# code using Roslyn syntax normalizer", Category = "Editor", IconKind = "FormatPaint", IconColorHex = "#75D59A", ShortcutHint = "Shift+Alt+F", ExecuteAction = FormatCode },
            new() { Title = "Preferences: Open User Customization Script (init.csx)", Subtitle = "Open ~/.frysharp/init.csx in editor tab for live customization", Category = "Preferences", IconKind = "FileCodeOutline", IconColorHex = "#A371F7", ExecuteAction = () => _ = OpenUserInitScriptAsync() },
            new() { Title = "Preferences: Open Workspace Customization Script (.frysharp/init.csx)", Subtitle = "Open workspace .frysharp/init.csx in editor tab", Category = "Preferences", IconKind = "FileCodeOutline", IconColorHex = "#A371F7", ExecuteAction = () => _ = OpenWorkspaceInitScriptAsync() },
            new() { Title = "Customization: Apply Active Tab as Customization", Subtitle = "Compile and execute active editor tab live in the studio extensibility engine", Category = "Customization", IconKind = "FlashOutline", IconColorHex = "#A371F7", ShortcutHint = "Ctrl+Alt+R", ExecuteAction = () => _ = ApplyActiveTabAsCustomizationAsync() },
            new() { Title = "Customization: Reload Customizations (~/.frysharp/init.csx)", Subtitle = "Recompile and apply user customization script and theme tokens", Category = "Customization", IconKind = "Refresh", IconColorHex = "#A371F7", ShortcutHint = "Ctrl+Shift+R", ExecuteAction = () => _ = ReloadCustomizationsAsync() },
            new() { Title = "Customization: Theme - Dark+ (Default)", Subtitle = "Apply Dark+ standard modern palette", Category = "Customization", IconKind = "PaletteOutline", IconColorHex = "#2F81F7", ExecuteAction = () => Services.Extensibility.StudioAppContext.Instance.Themes.ApplyTheme("dark-plus") },
            new() { Title = "Customization: Theme - Light+ (Default)", Subtitle = "Apply Light+ standard modern palette", Category = "Customization", IconKind = "PaletteOutline", IconColorHex = "#0969DA", ExecuteAction = () => Services.Extensibility.StudioAppContext.Instance.Themes.ApplyTheme("light-plus") },
            new() { Title = "Customization: Theme - Dracula Pro", Subtitle = "Apply Dracula vibrant purple palette", Category = "Customization", IconKind = "PaletteOutline", IconColorHex = "#BD93F9", ExecuteAction = () => Services.Extensibility.StudioAppContext.Instance.Themes.ApplyTheme("dracula") },
            new() { Title = "Customization: Theme - Cyberpunk Neon", Subtitle = "Apply Cyberpunk electric yellow & neon cyan palette", Category = "Customization", IconKind = "PaletteOutline", IconColorHex = "#FFE600", ExecuteAction = () => Services.Extensibility.StudioAppContext.Instance.Themes.ApplyTheme("cyberpunk") },
            new() { Title = "Customization: Theme - Monokai Classic", Subtitle = "Apply Monokai high-contrast warm palette", Category = "Customization", IconKind = "PaletteOutline", IconColorHex = "#A6E22E", ExecuteAction = () => Services.Extensibility.StudioAppContext.Instance.Themes.ApplyTheme("monokai") },
            new() { Title = "Customization: Theme - One Dark Pro", Subtitle = "Apply Atom One Dark iconic balanced dark palette", Category = "Customization", IconKind = "PaletteOutline", IconColorHex = "#61AFEF", ExecuteAction = () => Services.Extensibility.StudioAppContext.Instance.Themes.ApplyTheme("one-dark") },
            new() { Title = "Customization: Reset Theme to Defaults", Subtitle = "Clear color overrides and reset to active theme defaults", Category = "Customization", IconKind = "Restore", IconColorHex = "#E5534B", ExecuteAction = () => Services.Extensibility.StudioAppContext.Instance.Themes.ResetToDefaults() },
            new() { Title = "Editor: Toggle Word Wrap", Subtitle = "Toggle soft line wrapping in editor canvas", Category = "View", IconKind = "Wrap", IconColorHex = "#58A6FF", ShortcutHint = "Alt+Z", ExecuteAction = ToggleWordWrap },
            new() { Title = "View: Zoom In (Increase Font Size)", Subtitle = "Increase editor and terminal font size", Category = "View", IconKind = "MagnifyPlusOutline", IconColorHex = "#75D59A", ShortcutHint = "Ctrl+=", ExecuteAction = ZoomIn },
            new() { Title = "View: Zoom Out (Decrease Font Size)", Subtitle = "Decrease editor and terminal font size", Category = "View", IconKind = "MagnifyMinusOutline", IconColorHex = "#58A6FF", ShortcutHint = "Ctrl+-", ExecuteAction = ZoomOut },
            new() { Title = "View: Reset Font Zoom", Subtitle = "Reset typography to default 100% (13px)", Category = "View", IconKind = "MagnifyScan", IconColorHex = "#D97706", ShortcutHint = "Ctrl+0", ExecuteAction = ResetZoom },
            new() { Title = "View: Toggle Primary Side Bar", Subtitle = "Expand or collapse the primary activity sidebar", Category = "View", IconKind = "DockLeft", IconColorHex = "#58A6FF", ShortcutHint = "Ctrl+B", ExecuteAction = ToggleSideBar },
            new() { Title = "View: Toggle Bottom Panel", Subtitle = "Expand or collapse problems & output deck", Category = "View", IconKind = "DockBottom", IconColorHex = "#58A6FF", ShortcutHint = "Ctrl+J", ExecuteAction = ToggleBottomDeck },
            new() { Title = "View: Go to Line...", Subtitle = "Jump to specific line number in the active editor", Category = "Navigation", IconKind = "RayStartArrow", IconColorHex = "#75D59A", ShortcutHint = "Ctrl+G", ExecuteAction = ShowGoToLine },
            new() { Title = "View: Show Explorer", Subtitle = "Focus project explorer in side bar", Category = "Navigation", IconKind = "FolderMultipleOutline", IconColorHex = "#D97706", ShortcutHint = "Ctrl+Shift+E", ExecuteAction = () => SelectActivityBarItem(0) },
            new() { Title = "View: Show Search in Script", Subtitle = "Focus text search and replace panel", Category = "Navigation", IconKind = "Magnify", IconColorHex = "#58A6FF", ShortcutHint = "Ctrl+Shift+F", ExecuteAction = () => SelectActivityBarItem(1) },
            new() { Title = "View: Show Debug Panel", Subtitle = "Focus breakpoints, call stack, and locals", Category = "Navigation", IconKind = "BugPlayOutline", IconColorHex = "#58A6FF", ShortcutHint = "Ctrl+Shift+D", ExecuteAction = () => SelectActivityBarItem(2) },
            new() { Title = "View: Show NuGet Packages", Subtitle = "Browse and install NuGet package references", Category = "Navigation", IconKind = "PackageVariantClosed", IconColorHex = "#58A6FF", ShortcutHint = "Ctrl+Shift+X", ExecuteAction = () => SelectActivityBarItem(3) },
            new() { Title = "View: Show Problems", Subtitle = "Focus Roslyn compiler diagnostics list", Category = "Navigation", IconKind = "AlertCircleOutline", IconColorHex = "#E5534B", ShortcutHint = "Ctrl+Shift+M", ExecuteAction = () => ShowProblemsTabCommand.Execute(null) },
            new() { Title = "Export: Export as Standalone C# File (.cs)", Subtitle = "Copy clean C# code to clipboard with headers", Category = "Export", IconKind = "ExportVariant", IconColorHex = "#75D59A", ExecuteAction = () => _ = ExportScriptToCsAsync() },
            new() { Title = "Export: Export as C# Script (.csx)", Subtitle = "Copy Roslyn script with #r NuGet directives to clipboard", Category = "Export", IconKind = "ExportVariant", IconColorHex = "#75D59A", ExecuteAction = () => _ = ExportScriptToCsxAsync() },
            new() { Title = "Output: Clear Console", Subtitle = "Clear terminal execution stdout/stderr buffer", Category = "Terminal", IconKind = "Broom", IconColorHex = "#8B949E", ExecuteAction = ClearConsole },
            new() { Title = "Output: Clear Problems", Subtitle = "Clear active compiler error & warning list", Category = "Diagnostics", IconKind = "Broom", IconColorHex = "#8B949E", ExecuteAction = () => Diagnostics.Clear() },
            new() { Title = "Breakpoints: Clear All", Subtitle = "Remove all active breakpoints from current script", Category = "Debug", IconKind = "CloseCircleOutline", IconColorHex = "#E5534B", ExecuteAction = ClearAllBreakpoints },
            new() { Title = "Hub: Return to Workspace Manager", Subtitle = "Navigate back to Hub dashboard", Category = "Navigation", IconKind = "HomeOutline", IconColorHex = "#58A6FF", ExecuteAction = BackToHub }
        };

        foreach (var desc in PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.CommandPipeline.Descriptors)
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

        foreach (var tab in OpenTabs)
        {
            docs.Add(new QuickOpenItem
            {
                Title = tab.Title,
                Subtitle = tab.IsActive ? "Currently Active Script" : "Open Tab",
                Category = "Open Tabs",
                IconKind = "FileCodeOutline",
                IconColorHex = "#58A6FF",
                Kind = QuickOpenItemKind.Document,
                ExecuteAction = () => SafeTabAction(() => SwitchToTabAsync(tab), "QuickOpenSwitchTab")
            });
        }

        foreach (var tmpl in _allTemplates)
        {
            docs.Add(new QuickOpenItem
            {
                Title = tmpl.Title,
                Subtitle = $"Starter Template • {tmpl.Category}",
                Category = "Templates",
                IconKind = "CodeTags",
                IconColorHex = "#75D59A",
                Kind = QuickOpenItemKind.Template,
                ExecuteAction = () => InsertTemplate(tmpl)
            });
        }

        QuickOpen.RegisterDocuments(docs);
    }

    private void AddOrActivateTab(ScriptDocumentItem document)
    {
        var existing = OpenTabs.FirstOrDefault(t => t.Id == document.Id);
        if (existing != null)
        {
            foreach (var t in OpenTabs)
            {
                t.IsActive = (t.Id == document.Id);
            }
        }
        else
        {
            foreach (var t in OpenTabs)
            {
                t.IsActive = false;
            }
            var newTab = CreateTab(document, isActive: true);
            OpenTabs.Add(newTab);
        }
    }

    public async Task UpdateActiveScriptAsync(ScriptDocumentItem script)
    {
        var existing = OpenTabs.FirstOrDefault(t => t.Id == script.Id);
        if (existing != null)
        {
            await SwitchToTabAsync(existing);
            return;
        }

        var newTab = CreateTab(script, isActive: false);
        OpenTabs.Add(newTab);
        await SwitchToTabAsync(newTab);
    }
}
