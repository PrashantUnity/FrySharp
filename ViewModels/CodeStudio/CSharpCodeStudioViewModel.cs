using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Controls.Editor;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.Server;
using PdfEditorApp.Plugins.CSharpEditor.Services.Activities;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Debugging;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Kernels;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;
using PdfEditorApp.Plugins.CSharpEditor.Services.Problems.Catalogs.Blind75;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Templates;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;

public partial class CSharpCodeStudioViewModel : ObservableObject, IExplorerNewFileHost, IPageLifecycle
{
    private readonly IScriptStorageService _storageService;
    private readonly RoslynCompilerService _compilerService;
    public RoslynCompilerService CompilerService => _compilerService;
    private readonly ScriptExecutionEngine _executionEngine;
    private readonly ScriptDebuggerService _debuggerService;
    private readonly NotebookExecutionKernel _kernel;
    private readonly Action _backToHubAction;
    private readonly Action? _backToHomeAction;
    private readonly Action<NotebookDocumentItem>? _openNotebookAction;
    private readonly Action<FryServerDocumentItem>? _openServerAction;
    private readonly Action? _navigateToDocsAction;
    private readonly Action? _navigateToSettingsAction;
    private readonly StudioLanguageServices _languages;

    /// <summary>The languages this studio opens, runs and creates files of.</summary>
    public StudioLanguageServices Languages => _languages;

    /// <summary>The active document's language; C# for .frycs documents.</summary>
    public ILanguageDefinition ActiveLanguage => _languages.LanguageOf(Script);

    public Common.QuickOpenViewModel QuickOpen { get; } = new();
    public event Action<int>? RequestGoToLine;

    // Where this studio reports work the user may wait for (opening a file, a folder); the host decides what to show.
    private readonly IActivityService _activities;

    // Opening file B while file A is still loading cancels A: a slow A can never take the editor away from B.
    private readonly LatestOperation _fileOpen = new();

    // Two opens of one file at once (a double click, Go to File racing the Explorer) read it once.
    private readonly SingleFlight<string, ScriptDocumentItem?> _scriptLoads = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercentageText))]
    private double _editorFontSize = EditorZoomController.DefaultFontSize;

    public string ZoomPercentageText => EditorZoomController.FormatPercentage(EditorFontSize);

    [RelayCommand]
    public void ZoomIn()
    {
        EditorFontSize = EditorZoomController.ZoomIn(EditorFontSize);
        EditorZoomController.ScheduleSave(_languages.StudioSettings, EditorFontSize);
    }

    [RelayCommand]
    public void ZoomOut()
    {
        EditorFontSize = EditorZoomController.ZoomOut(EditorFontSize);
        EditorZoomController.ScheduleSave(_languages.StudioSettings, EditorFontSize);
    }

    [RelayCommand]
    public void ResetZoom()
    {
        EditorFontSize = EditorZoomController.Reset();
        EditorZoomController.ScheduleSave(_languages.StudioSettings, EditorFontSize);
    }

    public void ApplyFontSize(double fontSize)
    {
        EditorFontSize = EditorZoomController.Clamp(fontSize);
    }

    [ObservableProperty]
    private int _indentationSize = 4;

    public string IndentationStatusText => $"Spaces: {IndentationSize}";

    // Defaults to the app's startup theme (Dark, per App.axaml); ToggleTheme keeps this in sync from
    // then on. Must not read Application.Current.ActualThemeVariant here — this view model is
    // constructed off the UI thread (see CSharpStudioHostViewModel.InitializeCompilerAsync), and
    // Avalonia's styled-property getters assert UI-thread access.
    [ObservableProperty]
    private bool _isDarkTheme = true;

    [RelayCommand]
    public void ToggleTheme()
    {
        IsDarkTheme = ThemeService.ToggleTheme();
    }

    public string LanguageModeStatusText => UseExternalDotNetRunner
        ? "C# (.NET CLI)"
        : SelectedLanguageModeIndex switch
        {
            1 => "C# Program",
            2 => "C# Expression",
            _ => "C# Statements"
        };

    [RelayCommand]
    public void ToggleIndentation()
    {
        IndentationSize = IndentationSize == 4 ? 2 : 4;
        OnPropertyChanged(nameof(IndentationStatusText));
    }

    [RelayCommand]
    public void SetLanguageMode(string? modeIndexStr)
    {
        if (SupportsExecutionModes && int.TryParse(modeIndexStr, out var idx) && idx >= 0 && idx <= 2)
        {
            SelectedLanguageModeIndex = idx;
        }
    }

    public Action? ToggleAiComposerAction { get; set; }

    [RelayCommand]
    public void ToggleAiComposer()
    {
        ToggleAiComposerAction?.Invoke();
    }

    private CancellationTokenSource? _diagnosticsCts;
    private CancellationTokenSource? _executionCts;
    private int _executionRunId;
    private readonly Func<int> _getTimeoutSeconds;
    private readonly Action<Action> _postToUiThread;
    private readonly IBlindProgressService _blindProgress;

    /// <summary>Where a Blind 75 problem is marked solved when all its cases pass (the host shares it with the Blind 75 page).</summary>
    internal IBlindProgressService BlindProgress => _blindProgress;

    [ObservableProperty]
    private ScriptDocumentItem _script;

    [ObservableProperty]
    private string _code = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    /// <summary>
    /// True while the Scratchpad shows the notes rendered as markdown, false while they're being edited. A document's
    /// notes open rendered (a Blind 75 problem's statement reads like a page); an empty scratchpad opens ready to type.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingNotes))]
    private bool _isNotesPreviewMode;

    public bool IsEditingNotes => !IsNotesPreviewMode;

    /// <summary>Raised when the user switches the notes to editing, so the view can put the cursor in them.</summary>
    public event Action? RequestFocusNotes;

    [RelayCommand]
    private void ToggleNotesPreview()
    {
        IsNotesPreviewMode = !IsNotesPreviewMode;
        if (IsEditingNotes) RequestFocusNotes?.Invoke();
    }

    // ── VS Code Multi-Tab Document Strip ──
    public ObservableCollection<Common.StudioTabItemViewModel> OpenTabs { get; } = new();

    // ── VS Code Layout: Activity Bar & Primary Side Bar ──
    // 0=Explorer, 1=Search, 2=Debug, 3=NuGet, 4=Scratchpad, 5=Problems
    [ObservableProperty]
    private int _selectedActivityBarIndex = 0;

    [ObservableProperty]
    private bool _isSideBarVisible = true;

    [ObservableProperty]
    private Avalonia.Controls.GridLength _sideBarGridLength = new(290, Avalonia.Controls.GridUnitType.Pixel);

    private double _savedSideBarWidth = 290;

    partial void OnIsSideBarVisibleChanged(bool value)
    {
        if (value)
        {
            SideBarGridLength = new Avalonia.Controls.GridLength(_savedSideBarWidth > 120 ? _savedSideBarWidth : 290, Avalonia.Controls.GridUnitType.Pixel);
        }
        else
        {
            if (SideBarGridLength.IsAbsolute && SideBarGridLength.Value > 120)
            {
                _savedSideBarWidth = SideBarGridLength.Value;
            }
            SideBarGridLength = new Avalonia.Controls.GridLength(0, Avalonia.Controls.GridUnitType.Pixel);
        }
    }

    [ObservableProperty]
    private string _sideBarTitle = "EXPLORER";

    [ObservableProperty]
    private int _selectedLeftTabIndex = -1;

    public bool IsExplorerActive => SelectedActivityBarIndex == 0;
    public bool IsSearchActive => SelectedActivityBarIndex == 1;
    public bool IsDebugActive => SelectedActivityBarIndex == 2;
    public bool IsDependenciesActive => SelectedActivityBarIndex == 3;
    public bool IsScratchpadActive => SelectedActivityBarIndex == 4;
    public bool IsProblemsActive => SelectedActivityBarIndex == 5;
    public bool IsSourceControlActive => SelectedActivityBarIndex == 6;
    public bool IsDefaultSidebarCloseVisible => SelectedActivityBarIndex is not (0 or 1 or 2 or 6);

    private void UpdateSideBarTitle()
    {
        if (SelectedActivityBarIndex == 0)
        {
            var wsName = _storageService.IsExternalWorkspaceActive
                ? Path.GetFileName(_storageService.ActiveWorkspaceRootPath.TrimEnd('/', '\\'))
                : null;
            SideBarTitle = string.IsNullOrWhiteSpace(wsName) ? "EXPLORER" : $"EXPLORER: {wsName.ToUpperInvariant()}";
        }
        else
        {
            SideBarTitle = SelectedActivityBarIndex switch
            {
                1 => "SEARCH",
                2 => "RUN AND DEBUG",
                3 => "DEPENDENCIES & NUGET",
                4 => "SCRATCHPAD & NOTES",
                5 => "PROBLEMS",
                6 => GetSourceControlSideBarTitle(),
                _ => "EXPLORER"
            };
        }
    }

    partial void OnSelectedActivityBarIndexChanged(int value)
    {
        UpdateSideBarTitle();

        OnPropertyChanged(nameof(IsExplorerActive));
        OnPropertyChanged(nameof(IsSearchActive));
        OnPropertyChanged(nameof(IsDebugActive));
        OnPropertyChanged(nameof(IsDependenciesActive));
        OnPropertyChanged(nameof(IsScratchpadActive));
        OnPropertyChanged(nameof(IsProblemsActive));
        OnPropertyChanged(nameof(IsSourceControlActive));
        OnPropertyChanged(nameof(IsDefaultSidebarCloseVisible));

        if (value == 6)
        {
            _ = RefreshGitStatusAsync();
        }
    }

    partial void OnSelectedLeftTabIndexChanged(int value)
    {
        switch (value)
        {
            case 0:
                SelectedActivityBarIndex = 4; // Scratchpad & Notes
                IsSideBarVisible = true;
                break;
            case 1:
                SelectedActivityBarIndex = 3; // Dependencies & NuGet
                IsSideBarVisible = true;
                break;
            case 2:
                SelectedBottomTabIndex = 3; // Test Cases
                IsBottomDeckExpanded = true;
                break;
        }
    }

    [ObservableProperty]
    private int _selectedLanguageModeIndex = 0;

    [ObservableProperty]
    private int _caretLine = 1;

    [ObservableProperty]
    private int _caretColumn = 1;

    public event Action? RequestReloadEditorText;
    public event Action<Common.StudioTabItemViewModel>? RequestSwitchTabDocument;
    public event Action<int, int>? RequestNavigateToCaret;

    public ObservableCollection<string> LanguageModes { get; } = new()
    {
        "C# Statements",
        "C# Program (Main)",
        "C# Expression"
    };

    public ExecutionLanguageMode CurrentLanguageMode => SelectedLanguageModeIndex switch
    {
        1 => ExecutionLanguageMode.Program,
        2 => ExecutionLanguageMode.Expression,
        _ => ExecutionLanguageMode.Statements
    };

    public CSharpCodeStudioViewModel(
        ScriptDocumentItem script,
        IScriptStorageService storageService,
        RoslynCompilerService compilerService,
        ScriptExecutionEngine executionEngine,
        Action backToHubAction,
        Action? backToHomeAction = null,
        Func<int>? getTimeoutSeconds = null,
        Action<NotebookDocumentItem>? openNotebookAction = null,
        Action? navigateToDocsAction = null,
        Action<Action>? postToUiThread = null,
        IBlindProgressService? blindProgress = null,
        StudioLanguageServices? languages = null,
        Action? navigateToSettingsAction = null,
        Action<FryServerDocumentItem>? openServerAction = null,
        IActivityService? activities = null)
    {
        _script = script;
        _activities = activities ?? NullActivityService.Instance;
        _languages = languages ?? StudioLanguageServices.Default;
        _storageService = storageService;
        _compilerService = compilerService;
        _executionEngine = executionEngine;
        _debuggerService = new ScriptDebuggerService(_compilerService, _executionEngine);
        _backToHubAction = backToHubAction;
        _backToHomeAction = backToHomeAction;
        _openNotebookAction = openNotebookAction;
        _openServerAction = openServerAction;
        _navigateToDocsAction = navigateToDocsAction;
        _navigateToSettingsAction = navigateToSettingsAction;
        _getTimeoutSeconds = getTimeoutSeconds ?? (() => 0);
        _postToUiThread = postToUiThread ?? RunOnUiThread;
        _blindProgress = blindProgress ?? new LocalBlindProgressService();
        _kernel = new NotebookExecutionKernel();
        WatchTestCases();

        // The Results tab's empty hint depends on both lists it draws.
        DumpResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoResults));
        RichOutputs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoResults));

        _code = script.Code;
        _notes = script.Notes;
        _isNotesPreviewMode = !string.IsNullOrWhiteSpace(script.Notes);
        _selectedLanguageModeIndex = script.ExecutionMode switch
        {
            "Program" => 1,
            "Expression" => 2,
            _ => 0
        };

        foreach (var r in _compilerService.AvailableReferences)
        {
            References.Add(new Common.AssemblyReferenceViewModel(r));
        }

        foreach (var tc in script.TestCases)
        {
            TestCases.Add(tc);
        }

        Breakpoints.Clear();
        foreach (var bpLine in script.Breakpoints)
        {
            Breakpoints.Add(new BreakpointItem { LineNumber = bpLine, IsEnabled = true });
        }

        _allTemplates.AddRange(CodeTemplateLibrary.GetTemplates());
        RefreshFilteredTemplates();

        OpenTabs.Add(CreateTab(script, isActive: true));
        UpdateImageStateForDocument(script);
        UpdatePreviewStateForDocument(script);

        QuickOpen.RequestGoToLine += line => RequestGoToLine?.Invoke(line);
        InitializeQuickOpenCommands();
        RefreshQuickOpenDocuments();

        // Go to File finds any file of the workspace, not just the open tabs.
        QuickOpen.FileSearch = new WorkspaceFileSearch(() => _storageService.FileIndex, () => _storageService.ActiveWorkspaceRootPath, OpenWorkspaceFileAsync).Search;

        var initialSettings = _languages.StudioSettings.GetSettings();
        _editorFontSize = EditorZoomController.Clamp(initialSettings.FontSize);
        _useExternalDotNetRunner = string.Equals(initialSettings.CSharpExecutionEngine, "external", StringComparison.OrdinalIgnoreCase);
        _isSyntaxHighlightingEnabled = initialSettings.EnableSyntaxHighlighting;
        _isAutoCompletionEnabled = initialSettings.EnableAutoCompletion;
        _languages.StudioSettings.SettingsChanged += OnStudioSettingsChanged;
        _languages.Registry.Changed += OnLanguagesRegistryChanged;

        TriggerDiagnosticsCheck();
        // The Explorer is listed later, off the engine's start-up path: see ExplorerRefresh.

        _storageService.ActiveWorkspaceChanged += () => Dispatcher.UIThread.Post(() =>
        {
            UpdateSideBarTitle();
            RefreshExplorerAsync().FireAndForget(_activities, "Listing the workspace");
        });
        // What a workspace search listed belongs to the folder that was open: search the new one for the same text.
        _storageService.ActiveWorkspaceChanged += () => _postToUiThread(() =>
        {
            if (SearchAllFiles && !string.IsNullOrEmpty(SearchQuery)) ExecuteSearch();
        });
        // Files changed outside the studio: catch up now if this page is on screen, else on the next visit (OnActivated).
        _storageService.ExternalChangeDetected += () => Dispatcher.UIThread.Post(() =>
        {
            if (_isPageActive) RefreshExplorerIfStaleAsync().FireAndForget(_activities, "Listing the workspace");
        });
        OnActiveLanguageChanged();
        InitializeNuGetPackages();
        InitializeExtensibilityBridge();
        InitializeGitSupport();
    }

    private void OnLanguagesRegistryChanged()
    {
        _postToUiThread(() =>
        {
            _newFileOptions = null;
            OnPropertyChanged(nameof(NewFileOptions));

            if (Script.SourceFilePath != null)
            {
                OnActiveLanguageChanged();
            }
        });
    }

    private void OnStudioSettingsChanged(StudioSettings s)
    {
        _postToUiThread(() =>
        {
            if (Math.Abs(EditorFontSize - s.FontSize) > 0.05)
            {
                EditorFontSize = EditorZoomController.Clamp(s.FontSize);
            }
            SetCSharpRunner(s.CSharpExecutionEngine);
            IsSyntaxHighlightingEnabled = s.EnableSyntaxHighlighting;
            IsAutoCompletionEnabled = s.EnableAutoCompletion;
        });
    }

    // True while a tab's own code and notes are put back into the studio on a switch: that is not an edit, so it must
    // not mark the tab modified or move its modified time.
    private bool _isRestoringTabState;

    partial void OnCodeChanged(string value)
    {
        Script.Code = value;
        if (!_isRestoringTabState) Script.LastModified = DateTime.UtcNow;
        var activeTab = OpenTabs.FirstOrDefault(t => t.Id == Script.Id);
        if (activeTab != null)
        {
            if (!_isRestoringTabState) activeTab.IsDirty = true;
            activeTab.Document.Code = value;
        }
        TriggerDiagnosticsCheck();
        RefreshDocumentNuGetPackages();
        // A tab switch restores its own table; other changes rebuild it after a pause, off the UI thread when big.
        if (IsActiveDocumentCsv && IsDocumentPreviewMode && !_isRestoringTabState)
        {
            RefreshActiveDocumentPreview(debounce: true);
        }
    }

    partial void OnNotesChanged(string value)
    {
        Script.Notes = value;
        if (!_isRestoringTabState) Script.LastModified = DateTime.UtcNow;
    }

    partial void OnSelectedLanguageModeIndexChanged(int value)
    {
        Script.ExecutionMode = value switch
        {
            1 => "Program",
            2 => "Expression",
            _ => "Statements"
        };
        OnPropertyChanged(nameof(LanguageModeStatusText));
        OnPropertyChanged(nameof(LanguageStatusText));
        TriggerDiagnosticsCheck();
    }

    public void SetCaretPosition(int line, int col)
    {
        CaretLine = line;
        CaretColumn = col;
    }

    [RelayCommand]
    private Task SaveAsync() => SaveDocumentAsync(userAsked: true);

    /// <summary>
    /// Saves the active document. <paramref name="userAsked"/> is false for the saves the studio makes on its own
    /// (switching files, leaving the studio): those never write over a source file another program changed meanwhile.
    /// </summary>
    private async Task SaveDocumentAsync(bool userAsked)
    {
        if (Script.IsReadOnly || Script.Id.StartsWith("git-diff:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (userAsked)
        {
            Script.IsEphemeral = false;
        }

        Script.Code = Code;
        Script.Notes = Notes;
        Script.TestCases = TestCases.ToList();
        Script.LastModified = DateTime.UtcNow;
        bool saved;
        if (Script.SourceFilePath != null)
        {
            saved = await _storageService.SaveSourceFileAsync(Script, overwriteChangesOnDisk: userAsked);
            CompilerStatusText = saved
                ? "Saved"
                : userAsked
                    ? $"⚠️ Couldn't save {Script.Title}: check the folder's permissions"
                    : $"⚠️ {Script.Title} changed on disk, so it wasn't saved over. Ctrl+S saves your version.";
        }
        else
        {
            saved = await _storageService.SaveScriptAsync(Script);
            CompilerStatusText = saved ? "Saved" : "⚠️ Save failed — check disk space/permissions";
        }

        if (!saved) return;

        var activeTab = OpenTabs.FirstOrDefault(t => t.Id == Script.Id);
        if (activeTab != null)
        {
            activeTab.IsDirty = false;
            activeTab.NotifyTitleChanged();
        }

        PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.HookRegistry.InvokeDocumentSaved(
            new Services.Extensibility.Editor.StudioDocumentContextAdapter(this));
    }

    [RelayCommand]
    private void BackToHub()
    {
        _ = SaveDocumentAsync(userAsked: false);
        _backToHubAction.Invoke();
    }

    [RelayCommand]
    private void BackToHome()
    {
        _ = SaveDocumentAsync(userAsked: false);
        _backToHomeAction?.Invoke();
    }

    [RelayCommand]
    private void NavigateToDocs()
    {
        _ = SaveDocumentAsync(userAsked: false);
        _navigateToDocsAction?.Invoke();
    }

    [RelayCommand]
    public void NavigateToSettings()
    {
        _ = SaveDocumentAsync(userAsked: false);
        _navigateToSettingsAction?.Invoke();
    }

    [RelayCommand]
    private void SetLeftTab(string index)
    {
        if (int.TryParse(index, out var idx))
        {
            SelectedLeftTabIndex = idx;
        }
    }

    [RelayCommand]
    public void ToggleSideBar()
    {
        IsSideBarVisible = !IsSideBarVisible;
    }

    [RelayCommand]
    public void SelectActivityBarItem(string? indexStr)
    {
        if (int.TryParse(indexStr, out var index))
        {
            SelectActivityBarItem(index);
        }
    }

    public void SelectActivityBarItem(int index)
    {
        if (SelectedActivityBarIndex == index)
        {
            IsSideBarVisible = !IsSideBarVisible;
        }
        else
        {
            SelectedActivityBarIndex = index;
            IsSideBarVisible = true;
        }
    }
}
