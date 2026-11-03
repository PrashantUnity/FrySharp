using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using PdfEditorApp.Plugins.CSharpEditor.Controls.Editor;
using PdfEditorApp.Plugins.CSharpEditor.Controls.Notebooks;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.Server;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;
using PdfEditorApp.Plugins.CSharpEditor.Services.Problems.Catalogs.Blind75;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Server;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Templates;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;
using PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Server;
using PdfEditorApp.Plugins.CSharpEditor.Views;
using PdfEditorApp.Plugins.CSharpEditor.Views.Server;

namespace PdfEditorApp.Plugins.CSharpEditor.Tools.UiSnapshots.Commands;

/// <summary>The two studios, with a Blind 75 problem (or any script file) open, optionally after running it.</summary>
internal static class StudioSnapshots
{
    // Activity Bar order: CSharpCodeStudioViewModel.SelectedActivityBarIndex.
    private static readonly string[] SideBarViews = { "explorer", "search", "debug", "nuget", "notes", "problems", "sourcecontrol" };

    // Bottom panel tab order: CSharpCodeStudioViewModel.SelectedBottomTabIndex.
    private static readonly string[] PanelTabs = { "results", "terminal", "problems", "tests", "debug" };

    // Notebook Activity Bar order: CSharpNotebookStudioViewModel.SelectedActivityBarIndex.
    private static readonly string[] NotebookSideBarViews = { "explorer", "outline", "variables", "search" };

    private static async Task SeedTree(LocalScriptStorageService storage, int folders)
    {
        for (int f = 1; f <= folders; f++)
        {
            string folder = await storage.CreateFolderAsync(null, $"Folder {f}");
            for (int i = 1; i <= 4; i++) await storage.CreateNewScriptAsync($"Script {f}.{i}", folderPath: folder);
            string nested = await storage.CreateFolderAsync(folder, "Nested");
            await storage.CreateNewScriptAsync($"Deep {f}", folderPath: nested);
        }

        for (int i = 1; i <= 3; i++) await storage.CreateNewScriptAsync($"Loose script {i}");
    }

    private static async Task SeedDemoWorkspaceFiles(LocalScriptStorageService storage)
    {
        var root = storage.ActiveWorkspaceRootPath;
        var assetsDir = Path.Combine(root, "assets");
        Directory.CreateDirectory(assetsDir);
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDir);
        var scriptsDir = Path.Combine(root, "scripts");
        Directory.CreateDirectory(scriptsDir);

        // 1. Image assets (binary PNG & JPG)
        var repoLogo = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "app-logo.png");
        var repoBanner = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "frysharp-thumbnail.jpg");

        if (File.Exists(repoLogo))
        {
            File.Copy(repoLogo, Path.Combine(assetsDir, "logo.png"), overwrite: true);
        }
        else
        {
            byte[] pngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
            await File.WriteAllBytesAsync(Path.Combine(assetsDir, "logo.png"), pngBytes);
        }

        if (File.Exists(repoBanner))
        {
            File.Copy(repoBanner, Path.Combine(assetsDir, "banner.jpg"), overwrite: true);
        }
        else
        {
            byte[] jpgBytes = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=");
            await File.WriteAllBytesAsync(Path.Combine(assetsDir, "banner.jpg"), jpgBytes);
        }

        // 2. Data files (.csv, .pdf)
        string csvContent = """
            id,metric,value,timestamp,status
            1,cpu_usage,42.5,2026-10-02T10:00:00Z,ok
            2,memory_usage,68.2,2026-10-02T10:01:00Z,ok
            3,disk_io,120.4,2026-10-02T10:02:00Z,warning
            """;
        await File.WriteAllTextAsync(Path.Combine(dataDir, "metrics.csv"), csvContent);

        byte[] pdfBytes = "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>endobj\nxref\n0 4\n0000000000 65535 f\n0000000009 00000 n\n0000000052 00000 n\n0000000101 00000 n\ntrailer<</Size 4/Root 1 0 R>>\nstartxref\n173\n%%EOF\n"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(dataDir, "report.pdf"), pdfBytes);

        // 3. Config & documentation in root (.json, .txt)
        string jsonContent = """
            {
              "project": "PdfEditorApp",
              "environment": "Production",
              "enableDiagnostics": true,
              "cacheSizeMb": 512
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), jsonContent);

        string notesContent = """
            Workspace Notes
            ===============
            - All data files, images, and configs are accessible in scripts.
            - Reference paths relative to workspace root (e.g. "data/metrics.csv", "assets/logo.png").
            """;
        await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), notesContent);

        // 4. Multi-language scripts (.py, .cs)
        string pythonContent = """
            import csv
            import os

            print("Processing workspace files...")
            with open("data/metrics.csv", mode="r") as f:
                reader = csv.DictReader(f)
                for row in reader:
                    print(f"Metric {row['metric']}: {row['value']} ({row['status']})")
            print("Done!")
            """;
        await File.WriteAllTextAsync(Path.Combine(scriptsDir, "process.py"), pythonContent);

        // 5. Documentation (.md)
        var docsDir = Path.Combine(root, "docs");
        Directory.CreateDirectory(docsDir);
        string guideContent = """
            # Analytics Dashboard Guide

            Welcome to the **FrySharp** workspace documentation.

            ## Overview
            This workspace includes integrated script automation, polyglot notebooks, and raw data assets:

            - **Assets**: Application logo (`assets/logo.png`) and banner (`assets/banner.jpg`).
            - **Data**: Performance metrics (`data/metrics.csv`) and PDF documentation (`data/report.pdf`).
            - **Scripts**: Python automation (`scripts/process.py`) and Roslyn algorithms.

            ### Quick Code Example
            ```csharp
            using System;
            using System.IO;

            var lines = File.ReadAllLines("data/metrics.csv");
            Console.WriteLine($"Loaded {lines.Length} metric lines.");
            ```

            > **Tip**: Toggle between **Preview Mode** and **Edit Mode** anytime using `Ctrl+Shift+V` or the toolbar button!
            """;
        await File.WriteAllTextAsync(Path.Combine(docsDir, "guide.md"), guideContent);
    }

    private static void ExpandAll(IEnumerable<ExplorerItemViewModel> items)
    {
        foreach (var item in items.Where(i => i.IsDirectory))
        {
            item.IsExpanded = true;
            ExpandAll(item.Children);
        }
    }

    private static void EnsureExtensionsLoaded(StudioLanguageServices languages, Options options) =>
        Snapshot.EnsureExtensionsLoaded(languages, options);

    /// <summary><c>studio n</c> or <c>studio --file path</c>: Code Studio with that script open.</summary>
    public static void CodeStudio(Options options)
    {
        string? file = options.Value("file");

        // Languages over a throwaway folder: the toolchains found are the machine's own, but nothing chosen or created
        // here reaches the user's settings. --python picks the interpreter for .py files.
        var languages = new StudioLanguageServices(Snapshot.TempFolder("languages"));
        EnsureExtensionsLoaded(languages, options);
        if (options.Value("python") is { } python) languages.Registry.Get(LanguageIds.Python)?.Toolchain?.Select(python);
        if (options.Value("dotnet") is { } dotnetPath) languages.ToolchainSettings.SetSelectedPath(LanguageIds.CSharp, dotnetPath);
        if (options.Value("csharp-engine") is { } csharpEngine)
        {
            var settings = languages.StudioSettings.GetSettings();
            settings.CSharpExecutionEngine = csharpEngine;
            languages.StudioSettings.SaveSettings(settings);
        }
        // --file-limit n: list at most n files, to see what the Explorer says about a folder that has more.
        string? wsOpt = options.Value("workspace");
        if (wsOpt == null && file != null && Path.IsPathRooted(file) && File.Exists(file))
        {
            wsOpt = Path.GetDirectoryName(file);
        }
        var storage = new LocalScriptStorageService(Snapshot.TempFolder("studio_app_data"), languages.Registry, options.Int("file-limit", 20_000));

        if (wsOpt != null && Directory.Exists(wsOpt))
        {
            Snapshot.Wait(storage.OpenExternalProjectAsync(wsOpt));
        }
        else
        {
            // --tree n: n folders (scripts and a nested folder in each) plus a few loose scripts, so the Explorer shows a real tree.
            if (options.Int("tree", 0) is > 0 and var treeFolders) Snapshot.Wait(SeedTree(storage, treeFolders));
            if (options.Flag("demo-files")) Snapshot.Wait(SeedDemoWorkspaceFiles(storage));
        }

        ScriptDocumentItem script;
        if (wsOpt != null && Directory.Exists(wsOpt))
        {
            var summaries = Snapshot.Wait(storage.LoadWorkspaceSummariesAsync());
            var first = summaries.FirstOrDefault();
            if (first != null)
            {
                script = Snapshot.Wait(storage.LoadScriptAsync(first.Id)) ?? new ScriptDocumentItem { Title = first.Title, IsEphemeral = true };
            }
            else
            {
                script = new ScriptDocumentItem { Title = "Empty Workspace", IsEphemeral = true };
            }
        }
        else
        {
            script = Blind75CatalogService.ConvertToScript(Blind75CatalogService.GetProblemByNumber(options.Problem(file != null ? 1 : 0))!);
        }

        // Throwaway progress too: --run-tests marks a Blind 75 problem solved when all its cases pass.
        var vm = new CSharpCodeStudioViewModel(
            script,
            storage,
            new RoslynCompilerService(),
            new ScriptExecutionEngine(),
            backToHubAction: () => { },
            backToHomeAction: () => { },
            blindProgress: new LocalBlindProgressService(Snapshot.TempFolder("blind75-progress")),
            languages: languages);

        if (file != null)
        {
            OpenSourceFile(vm, storage, file);
            var keepTab = vm.OpenTabs.FirstOrDefault(t => t.Id == vm.Script.Id);
            if (keepTab != null) Snapshot.Wait(vm.CloseOtherTabsAsync(keepTab));
        }
        else
        {
            Snapshot.Wait(vm.RefreshExplorerAsync());
        }

        vm.SelectedActivityBarIndex = IndexOf(SideBarViews, options.Value("sidebar") ?? (options.Value("search-text") != null ? "search" : file != null || options.Flag("demo-files") || options.Int("tree", 0) > 0 || wsOpt != null ? "explorer" : "notes"), "--sidebar");
        if (options.Int("tree", 0) > 0 || options.Flag("demo-files") || wsOpt != null) ExpandAll(vm.ExplorerRootItems);

        if (options.Flag("git-demo") || options.Flag("diff") || options.Value("sidebar") == "sourcecontrol")
        {
            var root = storage.ActiveWorkspaceRootPath;
            Snapshot.Wait(vm.Git.GitService.InitRepositoryAsync(root));
            var dummyFile = Path.Combine(root, "Calculator.cs");
            File.WriteAllText(dummyFile, "public class Calculator\n{\n}\n");
            Snapshot.Wait(vm.Git.GitService.StageFileAsync(root, "Calculator.cs"));
            Snapshot.Wait(vm.Git.GitService.CommitAsync(root, "Initial commit"));
            File.AppendAllText(dummyFile, "    public int Add(int a, int b) => a + b;\n");
            var untracked = Path.Combine(root, "NewFeature.cs");
            File.WriteAllText(untracked, "// New feature code\n");
            Snapshot.Wait(vm.Git.RefreshGitStatusAsync());
            vm.Git.GitCommitMessage = "feat(calculator): implement Add method and new feature";
            vm.Git.GitAheadCount = 1;

            if (options.Flag("diff"))
            {
                string sampleDiff = """
                    diff --git a/Services/Workspace/Git/GitService.cs b/Services/Workspace/Git/GitService.cs
                    --- a/Services/Workspace/Git/GitService.cs
                    +++ b/Services/Workspace/Git/GitService.cs
                    @@ -218,6 +218,12 @@ public async Task<GitCommandResult> InitRepositoryAsync
                         return RunGitAsync(workspaceRoot, ["init"], DefaultTimeout, ct);
                     }
                    +
                    +    public async Task<string?> GetFileHeadContentAsync(string workspaceRoot, string relativePath)
                    +    {
                    +        var res = await RunGitAsync(workspaceRoot, ["show", $"HEAD:{relativePath}"]);
                    +        return res.Success ? res.Output : null;
                    +    }
                    """;
                var diffDoc = PdfEditorApp.Plugins.CSharpEditor.Services.Workspace.Git.GitDiffParser.Parse(
                    "Services/Workspace/Git/GitService.cs",
                    isStaged: false,
                    sampleDiff,
                    null,
                    null);

                var syntheticDoc = new ScriptDocumentItem
                {
                    Id = "git-diff:Services/Workspace/Git/GitService.cs:working",
                    Title = "GitService.cs (Working Tree)",
                    SourceFilePath = Path.Combine(storage.ActiveWorkspaceRootPath, "Services/Workspace/Git/GitService.cs"),
                    IsEphemeral = true
                };
                var tab = new StudioTabItemViewModel(syntheticDoc, isActive: true)
                {
                    IsDiffTab = true,
                    DiffDocument = diffDoc,
                    LanguageIconKind = "SourceCommit",
                    LanguageIconColor = "#E2C08D"
                };
                vm.OpenTabs.Add(tab);
                Snapshot.Wait(vm.SwitchToTabAsync(tab));
            }
        }

        if (options.Flag("edit-notes") && vm.IsNotesPreviewMode) vm.ToggleNotesPreviewCommand.Execute(null);
        if (options.Flag("image-code")) vm.ShowImageCodeDrawer = true;
        if (options.Flag("edit-raw"))
        {
            if (vm.IsDocumentPreviewMode) vm.ToggleDocumentPreviewModeCommand.Execute(null);
        }
        else if (options.Flag("preview"))
        {
            if (!vm.IsDocumentPreviewMode) vm.ToggleDocumentPreviewModeCommand.Execute(null);
        }

        if (options.Value("zoom") is { } zoomStr && double.TryParse(zoomStr, System.Globalization.CultureInfo.InvariantCulture, out var zoomSize))
        {
            vm.EditorFontSize = zoomSize;
        }

        var codeView = new CSharpCodeStudioView { DataContext = vm };
        ActivitySnapshots.BusyIfAsked(codeView, options, PdfEditorApp.Plugins.CSharpEditor.Services.Activities.ActivityLocation.Editor, "Opening sales-2026.csv");
        var window = Snapshot.Show(codeView, options.Int("width", 1400), options.Int("height", 900));
        if (options.Value("zoom-keys") is { } zoomKeys)
        {
            ApplyZoomKeys(window, zoomKeys);
        }
        // Go to File searches the workspace index: let its walk finish first, so the picture shows the files.
        if (options.Value("quick-open-text") != null) Snapshot.Wait(storage.FileIndex.RebuildAsync(storage.ActiveWorkspaceRootPath));
        ShowQuickOpen(vm.QuickOpen, options);
        // --search-text <t> [--search-all]: typed into the Search panel (Find in Files with --search-all); waits for the results.
        if (options.Value("search-text") is { } searchText)
        {
            Snapshot.Wait(storage.FileIndex.RebuildAsync(storage.ActiveWorkspaceRootPath));
            vm.SearchAllFiles = options.Flag("search-all");
            vm.SearchQuery = searchText;
            Snapshot.WaitFor(() => !vm.IsSearching, TimeSpan.FromSeconds(60));
            Snapshot.Settle();
        }

        Task? stillRunning = null;
        if (options.Flag("run") && options.Flag("while-running"))
        {
            // --while-running: the picture is taken while the program runs (waiting for input, say), then it's stopped.
            stillRunning = vm.RunCodeCommand.ExecuteAsync(null);
            Snapshot.WaitFor(() => vm.IsAcceptingProgramInput || stillRunning.IsCompleted, TimeSpan.FromSeconds(30));
            Snapshot.WaitFor(() => false, TimeSpan.FromMilliseconds(500));
        }
        else if (options.Flag("run"))
        {
            var run = vm.RunCodeCommand.ExecuteAsync(null);
            // --stdin <text>: typed into the Terminal once the program waits for input, as a person would.
            if (options.Value("stdin") is { } input)
            {
                if (!Snapshot.WaitFor(() => vm.IsAcceptingProgramInput || run.IsCompleted, TimeSpan.FromSeconds(60)) || run.IsCompleted)
                {
                    throw new ArgumentException("--stdin: the program never waited for input.");
                }
                Snapshot.WaitFor(() => false, TimeSpan.FromMilliseconds(300)); // let its prompt arrive
                vm.ProgramInputText = input;
                Snapshot.Wait(vm.SendProgramInputCommand.ExecuteAsync(null));
            }
            Snapshot.Wait(run);
            Snapshot.Settle();
        }
        if (options.Value("debug") != null) PauseAt(vm, options.Int("debug", 1));
        // The Test Cases panel: Generate (Blind 75 problems), Run All, and the Add Test Case form.
        if (options.Flag("generate-tests")) vm.GenerateTestCasesCommand.Execute(null);
        if (options.Flag("run-tests"))
        {
            Snapshot.Wait(vm.RunAllTestCasesCommand.ExecuteAsync(null));
            Snapshot.Settle();
        }
        if (options.Flag("add-test")) vm.AddTestCaseCommand.Execute(null);
        if (options.Value("panel") is { } panel)
        {
            vm.IsBottomDeckExpanded = true;
            vm.SelectedBottomTabIndex = IndexOf(PanelTabs, panel, "--panel");
        }
        // The bottom panel keeps its own height (280px) however tall the window is; this is the splitter's position.
        if (options.Value("panel-height") != null)
        {
            vm.IsBottomDeckExpanded = true;
            vm.BottomDeckGridLength = new GridLength(options.Int("panel-height", 280));
        }
        if (options.Value("panel-dock") == "bottom")
        {
            vm.IsBottomDeckExpanded = true;
            vm.ToggleDeckPosition();
            vm.IsDeckDockedToRight = false;
        }
        else if (options.Value("panel-dock") == "right" || options.Flag("dock-right"))
        {
            vm.IsBottomDeckExpanded = true;
            vm.IsDeckDockedToRight = true;
            if (options.Value("panel-width") != null)
            {
                vm.RightDeckGridLength = new GridLength(options.Int("panel-width", 540));
            }
            else
            {
                vm.UpdateAdaptiveDeckWidth(options.Int("width", 1400));
            }
        }
        else
        {
            vm.UpdateAdaptiveDeckWidth(options.Int("width", 1400));
        }
        Snapshot.Settle();
        ShowQuickInfo(window, options);

        var name = options.Value("name") ?? (file != null ? $"studio_{Path.GetFileNameWithoutExtension(file)}" : $"studio_{options.Problem()}");
        Snapshot.Save(window, options, name);
        if (options.Value("menu") is { } menu)
        {
            // The toolchain menu lists what's installed, which takes a moment to look for.
            Snapshot.Wait(vm.RefreshToolchainsCommand.ExecuteAsync(null));
            SaveMenu(window, menu, name, selectedCell: null);
        }
        if (vm.IsDebugging) vm.StopDebug();
        if (stillRunning != null)
        {
            vm.StopCommand.Execute(null);
            Snapshot.Wait(stillRunning);
        }
    }

    private static void ApplyZoomKeys(Window window, string zoomKeys)
    {
        var target = window.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault() as InputElement
            ?? window.Content as InputElement
            ?? window;

        foreach (var action in zoomKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (action.Equals("in", StringComparison.OrdinalIgnoreCase))
            {
                target.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.OemPlus,
                    PhysicalKey = PhysicalKey.Equal,
                    KeyModifiers = KeyModifiers.Control,
                    Source = target
                });
            }
            else if (action.Equals("out", StringComparison.OrdinalIgnoreCase))
            {
                target.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.OemMinus,
                    PhysicalKey = PhysicalKey.Minus,
                    KeyModifiers = KeyModifiers.Control,
                    Source = target
                });
            }
            else if (action.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                target.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.D0,
                    PhysicalKey = PhysicalKey.Digit0,
                    KeyModifiers = KeyModifiers.Control,
                    Source = target
                });
            }
        }
        Snapshot.Settle();
    }

    // The file goes into the workspace under its own name and is opened from the Explorer, as a person would.
    private static void OpenSourceFile(CSharpCodeStudioViewModel vm, LocalScriptStorageService storage, string file)
    {
        var fullPath = Path.IsPathRooted(file) && File.Exists(file)
            ? file
            : Path.Combine(storage.ActiveWorkspaceRootPath, file);

        if (!File.Exists(fullPath) && File.Exists(file))
        {
            var copy = Path.Combine(storage.ActiveWorkspaceRootPath, Path.GetFileName(file));
            File.Copy(file, copy, overwrite: true);
            fullPath = copy;
        }

        Snapshot.Wait(vm.RefreshExplorerAsync());
        var fileName = Path.GetFileName(file);
        var item = FindItem(vm.ExplorerRootItems, fileName)
            ?? FindItem(vm.ExplorerRootItems, fullPath);

        if (item != null)
        {
            Snapshot.Wait(vm.SwitchToScriptAsync(item));
        }
    }

    private static ExplorerItemViewModel? FindItem(IEnumerable<ExplorerItemViewModel> items, string target)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.Name, target, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.FullPath, target, StringComparison.OrdinalIgnoreCase) ||
                item.FullPath.EndsWith(Path.DirectorySeparatorChar + target, StringComparison.OrdinalIgnoreCase) ||
                item.FullPath.EndsWith("/" + target, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
            var found = FindItem(item.Children, target);
            if (found != null) return found;
        }
        return null;
    }

    // --debug <line>: a breakpoint on that line, then Debug (F5), returning once the debugger has paused there. The
    // debug run itself only finishes when it's resumed, so this waits for the pause rather than for the command.
    private static void PauseAt(CSharpCodeStudioViewModel vm, int line)
    {
        vm.ToggleBreakpoint(line);
        _ = vm.DebugCodeCommand.ExecuteAsync(null);
        if (!Snapshot.WaitFor(() => vm.IsPaused, TimeSpan.FromSeconds(60)))
        {
            throw new ArgumentException($"--debug {line}: the debugger never paused there. Is line {line} a statement?");
        }
        Snapshot.Settle();
    }

    // --quick-info <text>: rest the mouse on the first occurrence of <text> in an editor (the script, or the first
    // notebook cell that has it), as a person would, and wait for the hover card: Quick Info, or the debugger's data
    // tip while paused (--debug). The analysis runs on a worker thread, and the first one reads the documentation files.
    private static void ShowQuickInfo(Window window, Options options)
    {
        if (options.Value("quick-info") is not { } target) return;

        var editor = window.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault(e => e.Text?.Contains(target, StringComparison.Ordinal) == true)
            ?? throw new ArgumentException($"--quick-info: '{target}' isn't in any editor.");
        int offset = editor.Text.IndexOf(target, StringComparison.Ordinal);

        // Scroll only when the text is off-screen, so a word near the edge stays there (the card then flips above it).
        var location = editor.Document.GetLocation(offset);
        var textView = editor.TextArea.TextView;
        textView.EnsureVisualLines();
        if (!textView.VisualLines.Any(l => l.FirstDocumentLine.LineNumber <= location.Line && location.Line <= l.LastDocumentLine.LineNumber))
        {
            editor.ScrollTo(location.Line, location.Column);
        }
        editor.BringIntoView();
        Snapshot.Settle();

        // Just inside the word's first character, halfway down its line (text view coordinates are document minus scroll).
        var word = textView.GetVisualPosition(new TextViewPosition(location), VisualYPosition.LineMiddle) - textView.ScrollOffset;
        var point = textView.TranslatePoint(word + new Vector(3, 0), window) ?? throw new InvalidOperationException("The editor isn't in the window.");
        window.MouseMove(point);

        if (!Snapshot.WaitFor(() => window.GetVisualDescendants().Any(c => c is QuickInfoTipControl or DebugHoverDataTipControl && c.IsEffectivelyVisible), TimeSpan.FromSeconds(20)))
        {
            Console.Error.WriteLine($"--quick-info: no hover card appeared for '{target}'.");
        }
        Snapshot.Settle();
    }

    /// <summary>
    /// <c>notebook n</c>: Notebook Studio with the problem's notebook; <c>notebook --python-demo</c>: a notebook of C# and
    /// Python cells. <c>--run</c> runs every cell first.
    /// </summary>
    public static void Notebook(Options options)
    {
        // Languages over a throwaway folder, as for Code Studio: --python picks the interpreter Python cells run with.
        var languages = new StudioLanguageServices(Snapshot.TempFolder("languages"));
        EnsureExtensionsLoaded(languages, options);
        if (options.Value("python") is { } python) languages.Registry.Get(LanguageIds.Python)?.Toolchain?.Select(python);

        var dartDemo = options.Flag("dart-demo");
        var pyDemo = options.Flag("python-demo");
        var jsDemo = options.Flag("js-demo") || options.Flag("polyglot-demo");
        var javaShare = options.Flag("java-share");
        var javaException = options.Flag("java-exception");
        var javaTable = options.Flag("java-table");
        var cppDemo = options.Flag("cpp-demo");
        var goDemo = options.Flag("go-demo");
        var fsharpDemo = options.Flag("fsharp-demo");
        var sqlDemo = options.Flag("sql-demo");
        var rustDemo = options.Flag("rust-demo");
        var templateQuery = options.Value("template");
        CodeTemplate? template = null;
        if (templateQuery != null)
        {
            template = CodeTemplateLibrary.GetTemplates().FirstOrDefault(t =>
                t.Id.Contains(templateQuery, StringComparison.OrdinalIgnoreCase) ||
                t.Title.Contains(templateQuery, StringComparison.OrdinalIgnoreCase));
        }

        string? file = options.Value("file");
        NotebookDocumentItem? fileNotebook = null;
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
        {
            try
            {
                var content = File.ReadAllText(file);
                fileNotebook = System.Text.Json.JsonSerializer.Deserialize<NotebookDocumentItem>(content, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UiSnapshots] Failed to read notebook '{file}': {ex.Message}");
            }
        }

        int number = (fileNotebook != null || template != null || dartDemo || pyDemo || jsDemo || javaShare || javaException || javaTable || cppDemo || goDemo || fsharpDemo || sqlDemo || rustDemo) ? 0 : options.Problem();
        var storage = new LocalScriptStorageService(Snapshot.TempFolder("notebooks"), languages.Registry);
        var notebookDoc = fileNotebook
            ?? (template != null
                ? Snapshot.Wait(storage.CreateNewNotebookAsync(template.Title, template.Id))
                : dartDemo ? DartDemoNotebook()
                : rustDemo ? RustDemoNotebook()
                : sqlDemo ? SqlDemoNotebook()
                : fsharpDemo ? FSharpDemoNotebook()
                : goDemo ? GoDemoNotebook()
                : cppDemo ? CppDemoNotebook()
                : javaException ? JavaExceptionDemoNotebook()
                : javaTable ? JavaTableDemoNotebook()
                : javaShare ? JavaShareDemoNotebook()
                : jsDemo ? PolyglotDemoNotebook()
                : pyDemo ? PythonDemoNotebook()
                : Blind75CatalogService.ConvertToNotebook(Blind75CatalogService.GetProblemByNumber(number)!));

        var vm = new CSharpNotebookStudioViewModel(
            notebookDoc,
            storage,
            new RoslynCompilerService(),
            new ScriptExecutionEngine(),
            backToHubAction: () => { },
            backToHomeAction: () => { },
            languages: languages);

        if (options.Value("sidebar") is { } sidebar)
        {
            vm.SelectedActivityBarIndex = IndexOf(NotebookSideBarViews, sidebar, "--sidebar");
            vm.IsSideBarVisible = true;
        }

        if (options.Value("zoom") is { } nbZoomStr && double.TryParse(nbZoomStr, System.Globalization.CultureInfo.InvariantCulture, out var nbZoomSize))
        {
            vm.EditorFontSize = nbZoomSize;
        }

        var notebookView = new CSharpNotebookStudioView { DataContext = vm };
        ActivitySnapshots.BusyIfAsked(notebookView, options, PdfEditorApp.Plugins.CSharpEditor.Services.Activities.ActivityLocation.Notebook, "Opening MachineLearningCode.frynb");
        var window = Snapshot.Show(notebookView, options.Int("width", 1400), options.Int("height", 900));
        if (options.Value("zoom-keys") is { } nbZoomKeys)
        {
            ApplyZoomKeys(window, nbZoomKeys);
        }
        ShowQuickOpen(vm.QuickOpen, options);
        try
        {
            var name = options.Value("name") ?? (template != null ? $"notebook_{template.Id}" : dartDemo ? "notebook_dart_demo" : sqlDemo ? "notebook_sql_demo" : fsharpDemo ? "notebook_fsharp_demo" : goDemo ? "notebook_go_demo" : cppDemo ? "notebook_cpp_demo" : javaException ? "notebook_java_exception" : javaTable ? "notebook_java_table" : javaShare ? "notebook_java_share_test" : jsDemo ? "notebook_polyglot_demo" : pyDemo ? "notebook_python_demo" : (file != null ? Path.GetFileNameWithoutExtension(file) : $"notebook_{number}"));
            if (options.Flag("run") && RunAll(vm, window, options, name)) return;

            // --cell <n>: the n-th cell (from 1) is selected, as a click would, so its toolbar shows.
            if (options.Int("cell", 0) is > 0 and var cell && vm.ActiveTab is { } tab)
            {
                tab.SelectCell(tab.Cells[Math.Min(cell, (int)tab.Cells.Count) - 1]);
                Snapshot.Settle();
            }

            if (options.Value("scroll-y") is { } scrollYStr && double.TryParse(scrollYStr, System.Globalization.CultureInfo.InvariantCulture, out var scrollY))
            {
                var nbScroller = window.GetVisualDescendants().OfType<NotebookCanvasScrollViewer>().FirstOrDefault();
                if (nbScroller != null)
                {
                    nbScroller.SetScrollOffset(new Avalonia.Vector(nbScroller.Offset.X, scrollY));
                    Snapshot.Settle();
                }
                else if (window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroller)
                {
                    scroller.Offset = new Avalonia.Vector(scroller.Offset.X, scrollY);
                    Snapshot.Settle();
                }
            }
            else if (options.Flag("scroll-to-end"))
            {
                var nbScroller = window.GetVisualDescendants().OfType<NotebookCanvasScrollViewer>().FirstOrDefault();
                if (nbScroller != null)
                {
                    nbScroller.SetScrollOffset(new Avalonia.Vector(nbScroller.Offset.X, Math.Max(0, nbScroller.Extent.Height - nbScroller.Viewport.Height)));
                    Snapshot.Settle();
                }
                else if (window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroller)
                {
                    scroller.Offset = new Avalonia.Vector(scroller.Offset.X, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
                    Snapshot.Settle();
                }
            }

            ShowQuickInfo(window, options);
            Snapshot.Save(window, options, name);
            if (options.Value("menu") is { } menu) SaveMenu(window, menu, name, vm.ActiveCell);
        }
        finally
        {
            // The Python kernel runs as a program of its own.
            foreach (var tab in vm.Tabs) tab.ShutdownKernels();
        }
    }

    // Runs every cell. A cell that asks for input (Python's input()) gets --stdin's text, or end of input; with
    // --while-running the picture is taken while it waits instead (true: it's saved, and the rest is stopped).
    private static bool RunAll(CSharpNotebookStudioViewModel vm, Window window, Options options, string name)
    {
        var run = vm.RunAllCellsAsync();
        while (Snapshot.WaitFor(() => run.IsCompleted || vm.Cells.Any(c => c.IsAwaitingInput), TimeSpan.FromSeconds(120)) && !run.IsCompleted)
        {
            var asking = vm.Cells.First(c => c.IsAwaitingInput);
            if (options.Flag("while-running"))
            {
                vm.ActiveTab?.SelectCell(asking);
                Snapshot.Settle();
                Snapshot.Save(window, options, name);
                vm.ActiveTab?.InterruptExecution();
                Snapshot.Wait(run);
                return true;
            }

            if (options.Value("stdin") is not { } answer)
            {
                asking.EndInput();
                continue;
            }

            // Typed into the cell's input box and sent with Enter, as a person would (the box takes the focus itself).
            Snapshot.Settle();
            window.KeyTextInput(answer);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Snapshot.Settle();
            if (asking.IsAwaitingInput) throw new InvalidOperationException("--stdin: typing into the cell's input box didn't send the answer.");
        }

        Snapshot.Wait(run);
        Snapshot.Settle();
        return false;
    }

    // --menu language|kernel: opens the selected cell's language menu or the kernel pill's and saves the picture again
    // as <name>_menu (and the menu on its own, when it opens in a window of its own).
    private static void SaveMenu(Window window, string menu, string name, NotebookCellViewModel? selectedCell)
    {
        menu = menu.ToLowerInvariant();
        var tip = menu switch
        {
            "language" => "The cell's language",
            "kernel" => "The notebook's kernels",
            "toolchain" => "Choose which installed toolchain",
            "mode" => "Select C# Execution Mode",
            "newfile" => "New File...",
            _ => throw new ArgumentException($"--menu is language, kernel (notebook), toolchain, mode or newfile (studio); not '{menu}'.")
        };
        // Every cell has a language menu (only the selected cell's toolbar is opaque), so it's the selected cell's.
        var button = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b =>
                         b.IsEffectivelyVisible && b.Flyout != null && ToolTip.GetTip(b) is string text && text.StartsWith(tip, StringComparison.Ordinal) &&
                         (menu != "language" || ReferenceEquals(b.DataContext, selectedCell)))
                     ?? throw new ArgumentException($"--menu {menu}: that menu's button isn't showing (for language, select a code cell with --cell).");

        button.Flyout!.ShowAt(button);
        Snapshot.Settle();
        Snapshot.Save(window, name + "_menu");
        if (button.Flyout is Flyout { Content: Visual content } && TopLevel.GetTopLevel(content) is { } popup && !ReferenceEquals(popup, window))
        {
            using var frame = popup.CaptureRenderedFrame();
            if (frame != null)
            {
                var path = Path.Combine(Snapshot.OutputFolder, name + "_menu_popup.png");
                frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                Console.WriteLine(path);
            }
        }
        button.Flyout.Hide();
    }

    // C# and Python cells side by side, sharing values both ways (#!share): numpy, a pandas table, a matplotlib figure
    // and input() (what each needs installed shows as an "Install" offer in the cell when it's missing).
    private static NotebookDocumentItem PythonDemoNotebook() => new()
    {
        Title = "CSharp and Python",
        Cells =
        {
            new NotebookCellItem
            {
                Type = CellType.Markdown,
                Source = """
                    ## C# and Python in one notebook
                    Each cell runs in its language's kernel: pick it from the cell's language menu, or start the cell with `#!python`.
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Source = """
                    var nums = new[] { 3, 1, 4, 1, 5, 9, 2, 6 };
                    nums.Sum()
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Python,
                Source = """
                    #!share --from csharp nums
                    import sys
                    import numpy as np

                    print("Python", sys.version.split()[0], "with numpy", np.__version__)
                    arr = np.array(nums)
                    arr.mean(), arr.std().round(3)
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Python,
                Source = """
                    import pandas as pd

                    frame = pd.DataFrame({"n": arr, "square": arr ** 2, "even": arr % 2 == 0})
                    squares = frame["square"]
                    frame
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Source = """
                    #!python
                    import matplotlib.pyplot as plt

                    plt.figure(figsize=(6, 2.4))
                    plt.plot(arr, marker="o")
                    plt.title("nums")
                    plt.show()
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Source = """
                    #!share --from python squares
                    $"C# got {squares.Length} squares from Python; the biggest is {squares.Max()}"
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Python,
                Source = """
                    name = input("Your name? ") or "there"
                    print(f"Hi {name}: the numbers add up to {arr.sum()}")
                    """
            }
        }
    };

    private static NotebookDocumentItem PolyglotDemoNotebook() => new()
    {
        Title = "Polyglot: C#, Python, JavaScript, Java & C++",
        Cells =
        {
            new NotebookCellItem
            {
                Type = CellType.Markdown,
                Source = """
                    ## Polyglot Notebook (C#, Python, JavaScript, Java & C++)
                    Each cell runs in its native runtime. Data shares seamlessly across all five languages with `#!share`.
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.CSharp,
                Source = """
                    var scores = new[] { 88, 95, 72, 91, 84 };
                    $"C# generated {scores.Length} test scores; average is {scores.Average():F1}"
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.JavaScript,
                Source = """
                    #!share --from csharp scores
                    console.log(`Node.js ${process.version} received ${scores.length} scores from C#!`);
                    const grades = scores.map(s => ({
                        score: s,
                        letter: s >= 90 ? 'A' : s >= 80 ? 'B' : 'C',
                        passed: s >= 80
                    }));
                    display(grades);
                    grades
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Python,
                Source = """
                    #!share --from js grades
                    passed = sum(1 for g in grades if g['passed'])
                    avg = sum(g['score'] for g in grades) / len(grades)
                    print(f"Python analyzed {len(grades)} grades from JavaScript:")
                    print(f"Passed: {passed}/{len(grades)} students. Mean score: {avg:.1f}")
                    grades
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Java,
                Source = """
                    #!share --from csharp scores
                    int sum = Arrays.stream(scores).sum();
                    double avg = Arrays.stream(scores).average().orElse(0.0);
                    System.out.println("Java JShell: sum = " + sum + ", avg = " + avg);
                    Map<String, Object> javaStats = Map.of("sum", sum, "avg", avg, "count", scores.length);
                    javaStats;
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Cpp,
                Source = """
                    #!share --from csharp scores
                    int maxScore = 0;
                    for (int s : scores) {
                        if (s > maxScore) maxScore = s;
                    }
                    std::cout << "C++ calculated maximum score: " << maxScore << std::endl;
                    fry::display::table(scores, "C++ Processed Scores");
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.JavaScript,
                Source = """
                    const stats = { count: scores.length, max: Math.max(...scores), min: Math.min(...scores) };
                    stats
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.CSharp,
                Source = """
                    #!share --from js stats
                    #!share --from java javaStats
                    $"C# received: JS Max={stats["max"]}, Java Sum={javaStats["sum"]}, Java Avg={javaStats["avg"]}"
                    """
            }
        }
    };

    private static NotebookDocumentItem CppDemoNotebook() => new()
    {
        Title = "C++20 Interactive Notebook",
        Cells =
        {
            new NotebookCellItem
            {
                Type = CellType.Markdown,
                Source = """
                    # C++20 Interactive Notebook
                    Interactive C++ cell execution with Clang/GCC/MSVC, `<fry/display.hpp>` visual dumps, and STL algorithms.
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Cpp,
                Source = """
                    #include <iostream>
                    #include <vector>
                    #include <numeric>

                    std::vector<int> numbers = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
                    int total = std::accumulate(numbers.begin(), numbers.end(), 0);
                    std::cout << "Sum of numbers: " << total << std::endl;
                    fry::dump(numbers, "Initial Vector");
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Cpp,
                Source = """
                    #include <algorithm>

                    std::vector<int> squares;
                    for (int n : numbers) {
                        squares.push_back(n * n);
                    }
                    std::cout << "Computed " << squares.size() << " squares." << std::endl;
                    Display::table(squares, "Squares of Numbers");
                    """
            }
        }
    };

    private static NotebookDocumentItem FSharpDemoNotebook() => new()
    {
        Title = "F# Interactive Polyglot Notebook",
        Cells =
        {
            new NotebookCellItem
            {
                Type = CellType.Markdown,
                Source = """
                    # F# Interactive Polyglot Notebook
                    Interactive F# script cells running via F# Interactive (`dotnet fsi`) with type-safe pipelines, pattern matching, and visual dumps.
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.FSharp,
                Source = """
                    printfn "🚀 Hello from F# in Notebook Studio!"

                    let numbers = [ 1 .. 10 ]
                    let sumOfSquares =
                        numbers
                        |> List.map (fun x -> x * x)
                        |> List.sum

                    printfn "Sum of squares (1..10): %d" sumOfSquares
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.FSharp,
                Source = """
                    type Shape =
                        | Circle of radius: float
                        | Rectangle of width: float * height: float

                    let describe shape =
                        match shape with
                        | Circle r -> sprintf "Circle with radius %.2f" r
                        | Rectangle (w, h) -> sprintf "Rectangle %g x %g" w h

                    let c = Circle 4.5
                    let r = Rectangle (3.0, 7.0)

                    printfn "%s" (describe c)
                    printfn "%s" (describe r)
                    """
            }
        }
    };

    private static NotebookDocumentItem GoDemoNotebook() => new()
    {
        Title = "Go Interactive Polyglot Notebook",
        Cells =
        {
            new NotebookCellItem
            {
                Type = CellType.Markdown,
                Source = """
                    # Go Interactive Polyglot Notebook
                    Interactive Go cell execution with concurrent goroutines, channels, and cross-kernel variable sharing.
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Go,
                Source = """
                    nums := []int{10, 20, 30, 40, 50}
                    total := 0
                    for _, v := range nums {
                        total += v
                    }
                    fmt.Printf("Go computed sum: %d\n", total)
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Go,
                Source = """
                    ch := make(chan string)
                    go func() {
                        ch <- "🚀 Hello from concurrent Go goroutine in Notebook Studio!"
                    }()
                    msg := <-ch
                    fmt.Println(msg)
                    """
            }
        }
    };

    private static NotebookDocumentItem DartDemoNotebook()
    {
        var samplePath = Path.Combine(Directory.GetCurrentDirectory(), "samples", "extensions", "dart-support", "samples", "dart_notebook_demo.csnb");
        if (!File.Exists(samplePath))
        {
            samplePath = Path.Combine(AppContext.BaseDirectory, "samples", "extensions", "dart-support", "samples", "dart_notebook_demo.csnb");
        }
        if (File.Exists(samplePath))
        {
            try
            {
                var nb = System.Text.Json.JsonSerializer.Deserialize<NotebookDocumentItem>(File.ReadAllText(samplePath), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (nb != null) return nb;
            }
            catch
            {
            }
        }

        return new NotebookDocumentItem
        {
            Title = "Dart Polyglot Notebook Showcase",
            Kernel = "dart",
            Cells =
            {
                new NotebookCellItem
                {
                    Type = CellType.Markdown,
                    Source = "# 🎯 Dart in FrySharp Interactive Notebooks\n\nThis notebook demonstrates interactive Dart 3 execution with persistent domain models, 2D charts, 3D parametric surfaces, algorithm visualizers, and interactive tables."
                },
                new NotebookCellItem
                {
                    Type = CellType.Code,
                    Language = "dart",
                    Source = """
                        class Item {
                          final String name;
                          final double price;
                          Item(this.name, this.price);
                          @override
                          String toString() => '$name (\$${price.toStringAsFixed(2)})';
                        }
                        List<Item> cart = [
                          Item('Dart Gopher Book', 29.99),
                          Item('Flutter Mug', 14.50),
                          Item('Mechanical Keyboard', 129.00),
                          Item('4K Monitor', 349.99),
                          Item('Wireless Mouse', 49.95)
                        ];
                        var total = cart.map((i) => i.price).reduce((a, b) => a + b);
                        print('Initialized cart with ${cart.length} items. Total: \$${total.toStringAsFixed(2)}');
                        """
                },
                new NotebookCellItem
                {
                    Type = CellType.Code,
                    Language = "dart",
                    Source = """
                        // 1. 2D Bar Chart of Product Pricing
                        Display.barChart(
                          cart.map((i) => {'Item': i.name, 'Price': i.price}).toList(),
                          'Product Pricing Overview'
                        );
                        """
                },
                new NotebookCellItem
                {
                    Type = CellType.Code,
                    Language = "dart",
                    Source = """
                        // 2. 3D Parametric Mathematical Surface
                        Display.surface(
                          (x, y) => sin(sqrt(x * x + y * y)) / (sqrt(x * x + y * y) + 0.1) * 3,
                          'Ripple Waveform (3D Surface)',
                          {'xMin': -6, 'xMax': 6, 'yMin': -6, 'yMax': 6, 'resolution': 28, 'colorMap': 'plasma'}
                        );
                        """
                },
                new NotebookCellItem
                {
                    Type = CellType.Code,
                    Language = "dart",
                    Source = """
                        // 3. Algorithm Array Visualizer with Pointers
                        Visualizer.array(
                          [14.50, 29.99, 49.95, 129.00, 349.99],
                          {'low': 0, 'mid': 2, 'high': 4},
                          'Binary Search Range Partition'
                        );
                        """
                },
                new NotebookCellItem
                {
                    Type = CellType.Code,
                    Language = "dart",
                    Source = """
                        // 4. Rich Interactive Data Table
                        Display.table(
                          cart.map((i) => {
                            'Product': i.name,
                            'Price': i.price,
                            'Category': i.price > 100 ? 'Hardware' : 'Merchandise'
                          }).toList(),
                          'Inventory Catalog'
                        );
                        """
                }
            }
        };
    }

    private static NotebookDocumentItem RustDemoNotebook() => new()
    {
        Title = "Rust Interactive Notebook",
        Cells =
        {
            new NotebookCellItem
            {
                Type = CellType.Markdown,
                Source = """
                    # Rust Interactive Notebook
                    Each cell is built with Cargo and run. Functions, types and imports stay for later cells; the last expression is shown.
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Rust,
                Source = """
                    #[derive(Debug)]
                    struct Planet {
                        name: &'static str,
                        moons: u32,
                    }

                    fn total_moons(planets: &[Planet]) -> u32 {
                        planets.iter().map(|p| p.moons).sum()
                    }
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Rust,
                Source = """
                    let planets = vec![
                        Planet { name: "Earth", moons: 1 },
                        Planet { name: "Mars", moons: 2 },
                        Planet { name: "Jupiter", moons: 95 },
                    ];
                    let total = total_moons(&planets);
                    println!("{} moons in total", total);
                    fry::share!(total);
                    planets
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Rust,
                Source = """
                    // shared values come back as typed variables
                    println!("{} moons, doubled: {}", total, total * 2);
                    (1..=5).map(|n| n * n).collect::<Vec<u32>>()
                    """
            }
        }
    };

    private static NotebookDocumentItem SqlDemoNotebook() => new()
    {
        Title = "SQL / SQLite Interactive Notebook",
        Cells =
        {
            new NotebookCellItem
            {
                Type = CellType.Markdown,
                Source = """
                    # SQL / SQLite Interactive Notebook
                    Interactive SQL cells running via `sqlite3` CLI — create tables, query results, and share data with other kernel languages.
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Sql,
                Source = """
                    -- Create a products table and populate it
                    CREATE TABLE IF NOT EXISTS products (
                        id    INTEGER PRIMARY KEY AUTOINCREMENT,
                        name  TEXT    NOT NULL,
                        price REAL    NOT NULL,
                        stock INTEGER DEFAULT 0
                    );

                    INSERT INTO products (name, price, stock) VALUES
                        ('Avalonia Widget',   29.99, 150),
                        ('Roslyn Compiler',   0.00,  999),
                        ('SQLite Extension',  9.99,  42),
                        ('F# Toolkit',        19.99, 75);

                    SELECT name, price, stock FROM products ORDER BY price DESC;
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Sql,
                Source = """
                    -- Aggregate queries: total value and low-stock items
                    SELECT
                        COUNT(*)        AS total_products,
                        SUM(price)      AS total_price,
                        AVG(price)      AS avg_price,
                        MAX(stock)      AS max_stock
                    FROM products;

                    -- Low-stock items (fewer than 100 units)
                    SELECT name, stock
                    FROM   products
                    WHERE  stock < 100
                    ORDER  BY stock ASC;
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Sql,
                Source = """
                    -- Share the products table into Python via #!share
                    #!share products
                    """
            }
        }
    };

    private static NotebookDocumentItem JavaShareDemoNotebook() => new()
    {
        Title = "Polyglot: Testing #!share with Java",
        Cells =
        {
            new NotebookCellItem
            {
                Type = CellType.Markdown,
                Source = """
                    ## Polyglot Share: Java Value Sharing Demo
                    Cross-language bidirectional sharing between C# Roslyn and Java JShell kernel with `#!share`.
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Java,
                Source = """
                    int[] scores = new int[] { 88, 95, 72, 91, 84 };
                    int sum = Arrays.stream(scores).sum();
                    System.out.println("Java JShell computed sum: " + sum);
                    sum;
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.CSharp,
                Source = """
                    #!share --from java scores
                    #!share --from java sum --as javaSum
                    $"C# received sum {javaSum} and scores [{string.Join(", ", scores)}] from Java JShell!"
                    """
            }
        }
    };

    private static NotebookDocumentItem JavaExceptionDemoNotebook() => new()
    {
        Title = "Java: Exception and Traceback Handling",
        Cells =
        {
            new NotebookCellItem
            {
                Type = CellType.Markdown,
                Source = """
                    ## Java Exception Handling
                    Verifying that runtime exceptions in Java JShell are caught, formatted, and displayed cleanly without bringing down the kernel.
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Java,
                Source = """
                    int divide(int a, int b) {
                        return a / b;
                    }
                    divide(10, 0);
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Java,
                Source = """
                    int recovery = divide(10, 2);
                    System.out.println("Kernel survived exception! recovery = " + recovery);
                    recovery;
                    """
            }
        }
    };

    private static NotebookDocumentItem JavaTableDemoNotebook() => new()
    {
        Title = "Java: Interactive Tables and Mime Displays",
        Cells =
        {
            new NotebookCellItem
            {
                Type = CellType.Markdown,
                Source = """
                    ## Java Rich Table Display
                    Calling `display(...)` in Java JShell to render rich interactive tables with column sorting in the notebook UI.
                    """
            },
            new NotebookCellItem
            {
                Type = CellType.Code,
                Language = LanguageIds.Java,
                Source = """
                    var rows = List.of(
                        Map.of("id", 101, "package", "com.frypdf.core", "stars", 450),
                        Map.of("id", 102, "package", "com.frypdf.editor", "stars", 920),
                        Map.of("id", 103, "package", "com.frypdf.csharp", "stars", 780)
                    );
                    display(rows);
                    "Rendered " + rows.size() + " packages in interactive table."
                    """
            }
        }
    };

    // --quick-open files|commands: the Ctrl+P / Ctrl+Shift+P palette over the studio.
    private static void ShowQuickOpen(QuickOpenViewModel quickOpen, Options options)
    {
        if (options.Value("quick-open") is not { } mode) return;
        quickOpen.Show(mode.Equals("commands", StringComparison.OrdinalIgnoreCase) ? QuickOpenMode.Commands : QuickOpenMode.Files);

        // --quick-open-text <text>: typed into the palette, e.g. to see Go to File's results.
        if (options.Value("quick-open-text") is { } text) quickOpen.SearchText = text;
        Snapshot.Settle();
    }

    public static void ServerStudio(Options options)
    {
        var doc = new FryServerDocumentItem
        {
            Title = "Order Processing Microservice",
            ServerConfig = new FryServerConfiguration
            {
                Port = 5000,
                ApiPrefix = "/api",
                EnableCors = true,
                AllowPrivateNetwork = true
            },
            Cells =
            {
                new FryServerCellItem
                {
                    Type = FryServerCellType.Startup,
                    Title = "Database Seed & State Init",
                    Source = "State[\"orders\"] = new List<Dictionary<string, object>>\n{\n    new() { [\"id\"] = 101, [\"customer\"] = \"Alice Smith\", [\"total\"] = 149.99, [\"status\"] = \"Shipped\" },\n    new() { [\"id\"] = 102, [\"customer\"] = \"Bob Jones\", [\"total\"] = 89.50, [\"status\"] = \"Processing\" }\n};\nConsole.WriteLine(\"Server seed initialized with 2 mock orders.\");"
                },
                new FryServerCellItem
                {
                    Type = FryServerCellType.Endpoint,
                    Method = "GET",
                    Route = "/orders",
                    Title = "List Orders",
                    Source = "var orders = State[\"orders\"] as List<Dictionary<string, object>>;\nreturn Results.Ok(orders);"
                },
                new FryServerCellItem
                {
                    Type = FryServerCellType.Endpoint,
                    Method = "GET",
                    Route = "/orders/{id}",
                    Title = "Get Order by ID",
                    Source = "var id = PathParams.GetInt(\"id\");\nvar orders = State[\"orders\"] as List<Dictionary<string, object>>;\nvar found = orders?.FirstOrDefault(o => (int)o[\"id\"] == id);\nif (found == null) return Results.NotFound(new { error = $\"Order {id} not found\" });\nreturn Results.Ok(found);",
                    TestHarness = new FryServerTestHarnessItem
                    {
                        PathParams = new Dictionary<string, string> { ["id"] = "101" }
                    }
                },
                new FryServerCellItem
                {
                    Type = FryServerCellType.Endpoint,
                    Method = "POST",
                    Route = "/orders",
                    Title = "Create New Order",
                    Source = "var body = await Body.AsJsonAsync<Dictionary<string, object>>();\nvar orders = State[\"orders\"] as List<Dictionary<string, object>>;\nvar newOrder = new Dictionary<string, object> { [\"id\"] = 103, [\"customer\"] = body?[\"customer\"]?.ToString() ?? \"New Customer\", [\"total\"] = 99.0, [\"status\"] = \"Pending\" };\norders?.Add(newOrder);\nreturn Results.Created($\"/api/orders/{newOrder[\"id\"]}\", newOrder);"
                }
            }
        };

        int port = options.Int("port", 5000);
        doc.ServerConfig.Port = port;

        var languages = new StudioLanguageServices(Snapshot.TempFolder("languages"));
        var storage = new LocalScriptStorageService(Snapshot.TempFolder("server_scripts"), languages.Registry);
        // --workspace <folder>: the Explorer shows that folder (as when the studio has a workspace open).
        if (options.Value("workspace") is { } serverWorkspace) Snapshot.Wait(storage.OpenExternalProjectAsync(serverWorkspace));
        var compiler = new RoslynServerCompilationService();
        var portService = new PortAvailabilityService();
        var engine = new FryHttpListenerServerEngine(portService, compiler);

        var vm = new FryServerStudioViewModel(
            document: doc,
            filePath: null,
            engine: engine,
            portService: portService,
            storageService: storage,
            backToHubAction: () => { });

        if (options.Flag("conflict"))
        {
            vm.HasPortConflict = true;
            vm.SuggestedPort = port + 1;
        }

        if (options.Flag("traffic"))
        {
            vm.SelectActivityBarItem(1);
        }
        else if (options.Flag("explorer"))
        {
            vm.SelectActivityBarItem(2);
        }
        else if (options.Flag("no-sidebar") || options.Flag("collapsed-sidebar"))
        {
            vm.IsSideBarVisible = false;
        }

        if (options.Flag("run") || options.Flag("running"))
        {
            Snapshot.Wait(vm.StartServerAsync());
            Snapshot.Settle();

            if (vm.Cells.Count > 2)
            {
                Snapshot.Wait(vm.Cells[2].SendTestRequestAsync());
                Snapshot.Settle();
            }
        }

        var window = Snapshot.Show(new FryServerStudioView { DataContext = vm }, options.Int("width", 1400), options.Int("height", 1100));
        Snapshot.Settle();
        var name = options.Value("name") ?? (options.Flag("run") ? "server_studio_running" : "server_studio_demo");
        Snapshot.Save(window, options, name);

        if (vm.IsServerRunning)
        {
            Snapshot.Wait(vm.StopServerAsync());
        }
    }

    private static int IndexOf(string[] names, string name, string option)
    {
        int index = Array.FindIndex(names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : throw new ArgumentException($"{option} is one of {string.Join(", ", names)}; not '{name}'.");
    }
}
