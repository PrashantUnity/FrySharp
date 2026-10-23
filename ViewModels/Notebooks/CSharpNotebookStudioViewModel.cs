using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Controls.Editor;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.Server;
using PdfEditorApp.Plugins.CSharpEditor.Services.Activities;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks;

public partial class CSharpNotebookStudioViewModel : ObservableObject, IPageLifecycle
{
    // ── Infrastructure ────────────────────────────────────────────────────────

    private readonly IScriptStorageService _storageService;
    private readonly StudioLanguageServices _languages;
    private readonly RoslynCompilerService _compilerService;
    private readonly ScriptExecutionEngine _executionEngine;
    private readonly Action _backToHubAction;
    private readonly Action? _backToHomeAction;
    private readonly Action<ScriptDocumentItem>? _openScriptAction;
    private readonly Action<FryServerDocumentItem>? _openServerAction;
    private readonly Action? _navigateToDocsAction;
    private readonly Action? _navigateToSettingsAction;
    private readonly Func<int> _getTimeoutSeconds;

    public Common.QuickOpenViewModel QuickOpen { get; } = new();

    private readonly ObservableCollection<NotebookCellViewModel> _emptyCells = new();
    private readonly ObservableCollection<NotebookVariableInfo> _emptyVariables = new();

    // ── Active tab ────────────────────────────────────────────────────────────

    [ObservableProperty]
    private NotebookTabViewModel? _activeTab;

    [ObservableProperty]
    private NotebookDocumentItem _notebook = null!; // Always set by the constructor from a non-nullable parameter.

    // ── Running work ──────────────────────────────────────────────────────────

    // Where this studio reports work the user may wait for (opening a notebook, a folder); the host decides what to show.
    private readonly IActivityService _activities;

    // Opening notebook B while notebook A is still loading cancels A, so A can never take the canvas away from B.
    private readonly LatestOperation _documentOpen = new();

    // ── Activity bar / sidebar ────────────────────────────────────────────────

    [ObservableProperty]
    private int _selectedActivityBarIndex = 0;

    [ObservableProperty]
    private bool _isSideBarVisible = true;

    [ObservableProperty]
    private Avalonia.Controls.GridLength _sideBarGridLength = new(270, Avalonia.Controls.GridUnitType.Pixel);

    private double _savedSideBarWidth = 270;

    partial void OnIsSideBarVisibleChanged(bool value)
    {
        if (value)
        {
            SideBarGridLength = new Avalonia.Controls.GridLength(_savedSideBarWidth > 120 ? _savedSideBarWidth : 270, Avalonia.Controls.GridUnitType.Pixel);
        }
        else
        {
            if (SideBarGridLength.IsAbsolute && SideBarGridLength.Value > 120)
            {
                _savedSideBarWidth = SideBarGridLength.Value;
            }
            SideBarGridLength = new Avalonia.Controls.GridLength(0, Avalonia.Controls.GridUnitType.Pixel);
        }

        UpdateToolVisibilityFlags();
    }

    [ObservableProperty]
    private string _sideBarTitle = "EXPLORER";

    // ── Search ────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _searchText = string.Empty;

    public bool IsExplorerActive => SelectedActivityBarIndex == 0;
    public bool IsOutlineActive  => SelectedActivityBarIndex == 1;
    public bool IsVariablesActive => SelectedActivityBarIndex == 2;
    public bool IsSearchActive   => SelectedActivityBarIndex == 3;
    public bool IsSourceControlActive => SelectedActivityBarIndex == 6;
    public bool IsDefaultSidebarCloseVisible => !IsExplorerActive && !IsSourceControlActive;

    public event Action<NotebookCellViewModel>? RequestScrollToCell;

    partial void OnSelectedActivityBarIndexChanged(int value)
    {
        UpdateSideBarTitle();

        OnPropertyChanged(nameof(IsExplorerActive));
        OnPropertyChanged(nameof(IsOutlineActive));
        OnPropertyChanged(nameof(IsVariablesActive));
        OnPropertyChanged(nameof(IsSearchActive));
        OnPropertyChanged(nameof(IsSourceControlActive));
        OnPropertyChanged(nameof(IsDefaultSidebarCloseVisible));
        OnPropertyChanged(nameof(OutlineCells));
        OnPropertyChanged(nameof(SearchPanelCells));

        if (value == 2 && IsSideBarVisible)
        {
            ActiveTab?.UpdateVariables();
        }
        else if (value == 6 && IsSideBarVisible)
        {
            _ = RefreshGitStatusAsync();
        }

        UpdateToolVisibilityFlags();
    }

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
                1 => "OUTLINE",
                2 => "LIVE VARIABLES",
                3 => "SEARCH",
                6 => GetSourceControlSideBarTitle(),
                _ => "EXPLORER"
            };
        }
    }

    private void UpdateToolVisibilityFlags()
    {
        IsExplorerOpen = IsSideBarVisible && IsExplorerActive;
        IsOutlineOpen = IsSideBarVisible && IsOutlineActive;
        IsVariableInspectorOpen = IsSideBarVisible && IsVariablesActive;
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredCells));
        OnPropertyChanged(nameof(SearchPanelCells));
        OnPropertyChanged(nameof(SearchResultsCount));
    }

    public IEnumerable<NotebookCellViewModel> FilteredCells
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return Cells;
            return Cells.Where(c => (c.Source ?? string.Empty).Contains((string)SearchText, StringComparison.OrdinalIgnoreCase) ||
                                    (c.OutputText ?? string.Empty).Contains((string)SearchText, StringComparison.OrdinalIgnoreCase));
        }
    }

    public int SearchResultsCount => FilteredCells.Count();

    private static readonly IReadOnlyList<NotebookCellViewModel> NoCells = Array.Empty<NotebookCellViewModel>();

    // The Outline and Search panels each list every cell, and a hidden panel still builds its whole list: for a long
    // notebook that is hundreds of rows nobody sees. They are handed the cells only while they are showing.
    public IEnumerable<NotebookCellViewModel> OutlineCells => IsOutlineActive ? Cells : NoCells;

    public IEnumerable<NotebookCellViewModel> SearchPanelCells => IsSearchActive ? FilteredCells : NoCells;

    private readonly ConditionalWeakTable<ObservableCollection<NotebookCellViewModel>, NotebookCanvasRows> _canvasRows = new();

    /// <summary>What the canvas draws: a header row, then a row per cell of the active notebook (a virtualized list, see <see cref="NotebookCanvasRows"/>).</summary>
    public RangeObservableCollection<object> CanvasRows => _canvasRows.GetValue(Cells, cells => new NotebookCanvasRows(cells)).Rows;

    // ── Sidebar toggle commands ───────────────────────────────────────────────

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

    [RelayCommand]
    public void ToggleSideBar()
    {
        IsSideBarVisible = !IsSideBarVisible;
    }

    // ── Editor zoom ───────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercentageText))]
    private double _editorFontSize = EditorZoomController.DefaultFontSize;

    [ObservableProperty]
    private bool _isSyntaxHighlightingEnabled = true;

    [ObservableProperty]
    private bool _isAutoCompletionEnabled = true;

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

    // ── Panel visibility flags ────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isVariableInspectorOpen = false;

    [ObservableProperty]
    private bool _isOutlineOpen = false;

    [ObservableProperty]
    private bool _isExplorerOpen = true;

    [ObservableProperty]
    private string _workspaceName = "WORKSPACE";

    [ObservableProperty]
    private bool _isWorkspaceExpanded = true;

    [ObservableProperty]
    private bool _isOutlineExpanded = false;

    [ObservableProperty]
    private bool _isTimelineExpanded = false;

    // ── Collections ───────────────────────────────────────────────────────────

    public ObservableCollection<NotebookTabViewModel> Tabs { get; } = new();
    public ObservableCollection<ExplorerItemViewModel> ExplorerRootItems { get; } = new();

    private ExplorerRowList? _explorerRows;

    /// <summary>The Explorer's visible rows as one flat list: what the (virtualized) Explorer binds to.</summary>
    public ExplorerRowList ExplorerRows => _explorerRows ??= new ExplorerRowList(ExplorerRootItems);

    /// <summary>True when the workspace folder holds more files than the Explorer lists; the panel then says so rather than looking complete.</summary>
    [ObservableProperty]
    private bool _isExplorerTruncated;

    public string ExplorerTruncationText =>
        $"Showing the first {_storageService.WorkspaceFileLimit:N0} files. This folder has more: press Ctrl+P to open any file by name, or open a subfolder.";

    // ── Active-tab property bridges ───────────────────────────────────────────

    public bool HasActiveTab => ActiveTab != null;
    public bool HasNoTabs    => ActiveTab == null;

    public ObservableCollection<NotebookCellViewModel> Cells     => ActiveTab?.Cells     ?? _emptyCells;
    public ObservableCollection<NotebookVariableInfo>  Variables => ActiveTab?.Variables ?? _emptyVariables;

    public NotebookCellViewModel? ActiveCell     => ActiveTab?.ActiveCell;
    public bool                  IsExecuting     => ActiveTab?.IsExecuting ?? false;
    public string                KernelName      => ActiveTab?.KernelName ?? ".NET (C#)";
    public IReadOnlyList<NotebookKernelStatusItem> ActiveKernels => ActiveTab?.ActiveKernels ?? Array.Empty<NotebookKernelStatusItem>();

    public string CompilerStatusText
    {
        get => ActiveTab?.KernelStatusText ?? "Kernel Ready";
        set
        {
            if (ActiveTab != null)
            {
                ActiveTab.KernelStatusText = value;
                OnPropertyChanged(nameof(CompilerStatusText));
            }
        }
    }

    public string WorkspaceExpansionArrow => IsWorkspaceExpanded ? "⌵" : ">";

    public string BreadcrumbFolder      => ActiveTab?.BreadcrumbFolder ?? "Library";
    public string BreadcrumbDocument    => ActiveTab?.BreadcrumbDocument ?? "Untitled.frynb";
    public string ActiveCellBadgeText   => ActiveTab?.ActiveCellBadgeText ?? "Notebook Root";
    public string ActiveCellTypeIcon    => ActiveTab?.ActiveCellTypeIcon ?? "CodeBraces";
    public string ActiveCellTypeColorKey => ActiveTab?.ActiveCellTypeColorKey ?? "DsPrimaryBrush";

    public string BreadcrumbText    => $"{BreadcrumbFolder} › {BreadcrumbDocument} › {ActiveCellBadgeText}";
    public string DocumentTabTitle  => ActiveTab?.Title ?? "Notebook.frynb";

    partial void OnIsWorkspaceExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(WorkspaceExpansionArrow));
    }

    partial void OnActiveTabChanged(NotebookTabViewModel? oldValue, NotebookTabViewModel? newValue)
    {
        if (oldValue != null)
        {
            oldValue.PropertyChanged -= OnActiveTabPropertyChanged;
            oldValue.IsActive = false;
        }

        if (newValue != null)
        {
            newValue.IsActive = true;
            newValue.PropertyChanged += OnActiveTabPropertyChanged;
            Notebook = newValue.Notebook;
        }

        NotifyActiveTabProperties();
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NotebookTabViewModel.ActiveCell) or
            nameof(NotebookTabViewModel.ActiveCellBadgeText) or
            nameof(NotebookTabViewModel.ActiveCellTypeIcon) or
            nameof(NotebookTabViewModel.ActiveCellTypeColorKey))
        {
            OnPropertyChanged(nameof(ActiveCell));
            OnPropertyChanged(nameof(ActiveCellBadgeText));
            OnPropertyChanged(nameof(ActiveCellTypeIcon));
            OnPropertyChanged(nameof(ActiveCellTypeColorKey));
            OnPropertyChanged(nameof(BreadcrumbText));
        }
        else if (e.PropertyName == nameof(NotebookTabViewModel.KernelStatusText))
        {
            OnPropertyChanged(nameof(CompilerStatusText));
        }
        else if (e.PropertyName == nameof(NotebookTabViewModel.KernelName))
        {
            OnPropertyChanged(nameof(KernelName));
        }
        else if (e.PropertyName == nameof(NotebookTabViewModel.ActiveKernels))
        {
            OnPropertyChanged(nameof(ActiveKernels));
        }
        else if (e.PropertyName == nameof(NotebookTabViewModel.IsExecuting))
        {
            OnPropertyChanged(nameof(IsExecuting));
        }
        else if (e.PropertyName == nameof(NotebookTabViewModel.Title))
        {
            OnPropertyChanged(nameof(BreadcrumbDocument));
            OnPropertyChanged(nameof(DocumentTabTitle));
            OnPropertyChanged(nameof(BreadcrumbText));
        }
        else if (e.PropertyName == nameof(NotebookTabViewModel.FolderName))
        {
            OnPropertyChanged(nameof(BreadcrumbFolder));
            OnPropertyChanged(nameof(BreadcrumbText));
        }
        else if (e.PropertyName == nameof(NotebookTabViewModel.Variables))
        {
            OnPropertyChanged(nameof(Variables));
        }
    }

    private void NotifyActiveTabProperties()
    {
        OnPropertyChanged(nameof(HasActiveTab));
        OnPropertyChanged(nameof(HasNoTabs));
        OnPropertyChanged(nameof(Cells));
        OnPropertyChanged(nameof(CanvasRows));
        OnPropertyChanged(nameof(OutlineCells));
        OnPropertyChanged(nameof(SearchPanelCells));
        OnPropertyChanged(nameof(Variables));
        OnPropertyChanged(nameof(ActiveCell));
        OnPropertyChanged(nameof(IsExecuting));
        OnPropertyChanged(nameof(KernelName));
        OnPropertyChanged(nameof(ActiveKernels));
        OnPropertyChanged(nameof(CompilerStatusText));
        OnPropertyChanged(nameof(BreadcrumbFolder));
        OnPropertyChanged(nameof(BreadcrumbDocument));
        OnPropertyChanged(nameof(ActiveCellBadgeText));
        OnPropertyChanged(nameof(ActiveCellTypeIcon));
        OnPropertyChanged(nameof(ActiveCellTypeColorKey));
        OnPropertyChanged(nameof(BreadcrumbText));
        OnPropertyChanged(nameof(DocumentTabTitle));
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public CSharpNotebookStudioViewModel(
        NotebookDocumentItem notebook,
        IScriptStorageService storageService,
        RoslynCompilerService compilerService,
        ScriptExecutionEngine executionEngine,
        Action backToHubAction,
        Action? backToHomeAction = null,
        Func<int>? getTimeoutSeconds = null,
        Action<ScriptDocumentItem>? openScriptAction = null,
        Action? navigateToDocsAction = null,
        StudioLanguageServices? languages = null,
        Action? navigateToSettingsAction = null,
        Action<FryServerDocumentItem>? openServerAction = null,
        IActivityService? activities = null)
    {
        _activities = activities ?? NullActivityService.Instance;
        _languages = languages ?? StudioLanguageServices.Default;
        _notebook = notebook;
        _storageService = storageService;
        _compilerService = compilerService;
        _executionEngine = executionEngine;
        _backToHubAction = backToHubAction;
        _backToHomeAction = backToHomeAction;
        _openScriptAction = openScriptAction;
        _openServerAction = openServerAction;
        _navigateToDocsAction = navigateToDocsAction;
        _navigateToSettingsAction = navigateToSettingsAction;
        _getTimeoutSeconds = getTimeoutSeconds ?? (() => 0);

        var initialTab = CreateTab(_notebook, "Library", $"{notebook.Title}.frynb");

        ConfigureNotebookTab(initialTab);
        Tabs.Add(initialTab);
        SelectTab(initialTab);

        var initialSettings = _languages.StudioSettings.GetSettings();
        _editorFontSize = EditorZoomController.Clamp(initialSettings.FontSize);
        _isSyntaxHighlightingEnabled = initialSettings.EnableSyntaxHighlighting;
        _isAutoCompletionEnabled = initialSettings.EnableAutoCompletion;
        _languages.StudioSettings.SettingsChanged += OnStudioSettingsChanged;

        InitializeQuickOpenCommands();
        RefreshQuickOpenDocuments();

        // Go to File finds any file of the workspace, not just the open tabs.
        QuickOpen.FileSearch = new WorkspaceFileSearch(() => _storageService.FileIndex, () => _storageService.ActiveWorkspaceRootPath, OpenWorkspaceFileAsync).Search;
        // The Explorer is listed later, off the engine's start-up path: see ExplorerRefresh.

        _storageService.ActiveWorkspaceChanged += () => Dispatcher.UIThread.Post(() => RefreshExplorerAsync().FireAndForget(_activities, "Listing the workspace"));
        // Files changed outside the studio: catch up now if this page is on screen, else on the next visit (OnActivated).
        _storageService.ExternalChangeDetected += () => Dispatcher.UIThread.Post(() =>
        {
            if (_isPageActive) RefreshExplorerIfStaleAsync().FireAndForget(_activities, "Listing the workspace");
        });

        InitializeExtensibilityBridge();
        InitializeGitSupport();
    }

    private void OnStudioSettingsChanged(StudioSettings s)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Math.Abs(EditorFontSize - s.FontSize) > 0.05)
            {
                EditorFontSize = EditorZoomController.Clamp(s.FontSize);
            }
            IsSyntaxHighlightingEnabled = s.EnableSyntaxHighlighting;
            IsAutoCompletionEnabled = s.EnableAutoCompletion;
        });
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    [RelayCommand]
    public void BackToHub()
    {
        _ = SaveAsync();
        _backToHubAction.Invoke();
    }

    [RelayCommand]
    public void BackToHome()
    {
        _ = SaveAsync();
        _backToHomeAction?.Invoke();
    }

    [RelayCommand]
    public void NavigateToDocs()
    {
        _ = SaveAsync();
        _navigateToDocsAction?.Invoke();
    }

    [RelayCommand]
    public void NavigateToSettings()
    {
        _ = SaveAsync();
        _navigateToSettingsAction?.Invoke();
    }

    // ── Explorer panel toggle commands ────────────────────────────────────────

    [RelayCommand]
    public void ToggleWorkspaceExpand()
    {
        IsWorkspaceExpanded = !IsWorkspaceExpanded;
    }

    [RelayCommand]
    public void ToggleExplorer()
    {
        IsExplorerOpen = !IsExplorerOpen;
    }

    [RelayCommand]
    public void ToggleOutline()
    {
        SelectActivityBarItem(1);
    }

    [RelayCommand]
    public void ToggleVariableInspector()
    {
        SelectActivityBarItem(2);
    }

    [RelayCommand]
    public void ToggleOutlineExpanded()
    {
        IsOutlineExpanded = !IsOutlineExpanded;
        IsOutlineOpen = IsOutlineExpanded;
    }

    [RelayCommand]
    public void ToggleTimelineExpanded()
    {
        IsTimelineExpanded = !IsTimelineExpanded;
    }
}
