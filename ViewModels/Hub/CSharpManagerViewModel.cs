using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.Server;
using PdfEditorApp.Plugins.CSharpEditor.Services.Activities;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;
using PdfEditorApp.Plugins.CSharpEditor.Services.Server;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Templates;
using PdfEditorApp.Plugins.CSharpEditor.Services.Toolchains;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Hub;

public partial class CSharpManagerViewModel : ObservableObject, IPageLifecycle
{
    private readonly IScriptStorageService _storageService;
    private readonly Action<ScriptDocumentItem>? _openScriptAction;
    private readonly Action<NotebookDocumentItem>? _openNotebookAction;
    private readonly Action<FryServerDocumentItem>? _openServerAction;
    private readonly IFryServerRegistry _serverRegistry;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly IActivityService _activities;

    // Opening one item and then another before the first has loaded: only the last one opens.
    private readonly LatestOperation _open = new();

    // The first list load happens while the studio is starting: it may cover the Hub with the branded card (see LoadWorkspaceItemsAsync).
    private bool _hasLoadedOnce;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _selectedTypeFilter = "All";

    [ObservableProperty]
    private string _selectedSortOption = "Recently Modified";

    /// <summary>
    /// The workspace list is being (re)loaded. Shown as the Hub's progress line; it never blocks a click: creating or
    /// opening something while the list refreshes works (those are gated by <see cref="IsLaunching"/> only).
    /// </summary>
    [ObservableProperty]
    private bool _isRefreshingList;

    [ObservableProperty]
    private bool _hasFilteredItems = true;

    [ObservableProperty]
    private bool _hasSearchQuery;

    [ObservableProperty]
    private int _totalScripts;

    [ObservableProperty]
    private int _totalNotebooks;

    [ObservableProperty]
    private int _filteredItemCount;

    // Safe to read the actual Avalonia theme here: this view model is constructed directly on the UI
    // thread (see CSharpStudioHostViewModel's constructor comment "Show Manager immediately").
    [ObservableProperty]
    private bool _isDarkTheme = ThemeService.IsDark;

    public string ThemeToggleLabel => IsDarkTheme ? "Light Theme" : "Dark Theme";

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = ThemeService.ToggleTheme();
        OnPropertyChanged(nameof(ThemeToggleLabel));
    }

    // Starts on the workspace list, like the dashboard view below it, so the sidebar highlights what's shown.
    [ObservableProperty]
    private string _selectedNavSection = "Workspace";

    [ObservableProperty]
    private string _activeDashboardView = "Workspaces";

    public bool IsWorkspacesTabActive => ActiveDashboardView == "Workspaces";

    /// <summary>No scripts or notebooks at all (a first run), as opposed to none matching the search or filter.</summary>
    public bool IsWorkspaceEmpty => AllItems.Count == 0;
    public bool IsTemplatesTabActive => ActiveDashboardView == "Templates";

    [ObservableProperty]
    private CodeTemplate? _selectedTemplate;

    [ObservableProperty]
    private WorkspaceItemSummary? _selectedWorkspaceItem;

    [ObservableProperty]
    private bool _isLaunching;

    [ObservableProperty]
    private WorkspaceItemKind? _pendingCreateKind;

    [ObservableProperty]
    private string _newItemName = string.Empty;

    [ObservableProperty]
    private string? _selectedFolderPath;

    [ObservableProperty]
    private string? _locationWarning;

    [ObservableProperty]
    private string? _statusBannerMessage;

    [ObservableProperty]
    private bool _hasStatusBannerMessage;

    [ObservableProperty]
    private bool _isStatusBannerError;

    [ObservableProperty]
    private bool _isSystemStatusRailVisible = true;

    private bool _userExplicitlyToggledRail;

    [RelayCommand]
    private void ToggleSystemStatusRail()
    {
        _userExplicitlyToggledRail = true;
        IsSystemStatusRailVisible = !IsSystemStatusRailVisible;
    }

    public void UpdateAdaptiveRail(double width)
    {
        if (width <= 0) return;
        if (!_userExplicitlyToggledRail)
        {
            IsSystemStatusRailVisible = width >= 1100;
        }
    }

    private string? _pendingTemplateId;

    public bool IsCreatePromptOpen => PendingCreateKind.HasValue;
    public string CreatePromptTitle => PendingCreateKind == WorkspaceItemKind.Notebook ? "New Notebook" : (PendingCreateKind == WorkspaceItemKind.Server ? "New API Server" : "New Script");
    public bool IsCreatingNotebook => PendingCreateKind == WorkspaceItemKind.Notebook;
    public bool IsCreatingScript => PendingCreateKind == WorkspaceItemKind.Script;
    public string SelectedFolderDisplay => string.IsNullOrEmpty(SelectedFolderPath) ? "Workspace root" : SelectedFolderPath;

    public ObservableCollection<Services.Languages.ILanguageDefinition> AvailableCreateLanguages { get; } = new();

    [ObservableProperty]
    private Services.Languages.ILanguageDefinition? _selectedCreateLanguage;

    public string LibraryRootPath => _storageService.LibraryRootPath;
    public string ActiveWorkspaceRootPath => _storageService.ActiveWorkspaceRootPath;

    public ObservableCollection<WorkspaceItemSummary> AllItems { get; } = new();

    // What the list draws: at most _visibleItemLimit rows (see ApplyFilter), replaced in one step rather than item by item.
    public RangeObservableCollection<object> FilteredItems { get; } = new();
    public ObservableCollection<CodeTemplate> StarterTemplates { get; } = new();

    public IEnumerable<CodeTemplate> ScriptTemplates => FilterTemplates(StarterTemplates.Where(t => !t.IsNotebook));
    public IEnumerable<CodeTemplate> NotebookTemplates => FilterTemplates(StarterTemplates.Where(t => t.IsNotebook));

    public ObservableCollection<string> TypeFilters { get; } = new()
    {
        "All", "Workspaces", "Notebooks", "Scripts", "Servers", "Pinned"
    };

    public ObservableCollection<string> SortOptions { get; } = new()
    {
        "Recently Modified", "Title (A-Z)", "Execution Count"
    };

    private readonly Action? _navigateToHomeAction;
    private readonly Action? _navigateToDocsAction;
    private readonly Action? _navigateToBlindProblemsAction;
    private readonly Action<string?>? _navigateToSettingsAction;

    [RelayCommand]
    public void NavigateToDocs()
    {
        _navigateToDocsAction?.Invoke();
    }

    [RelayCommand]
    public void NavigateToBlindProblems()
    {
        _navigateToBlindProblemsAction?.Invoke();
    }

    [RelayCommand]
    public void NavigateToSettings(string? category = null)
    {
        _navigateToSettingsAction?.Invoke(category);
    }

    public bool IsAllFilterActive => SelectedTypeFilter == "All";
    public bool IsNotebooksFilterActive => SelectedTypeFilter == "Notebooks";
    public bool IsScriptsFilterActive => SelectedTypeFilter == "Scripts";
    public bool IsPinnedFilterActive => SelectedTypeFilter == "Pinned";
    public int PinnedCount => PinnedItems.Count;

    public bool IsWorkspaceSectionActive => SelectedNavSection == "Workspace";
    public bool IsTemplatesSectionActive => SelectedNavSection == "Templates";

    public bool IsWorkspaceAllNavActive => IsWorkspaceSectionActive && IsAllFilterActive;
    public bool IsScriptsNavActive => IsWorkspaceSectionActive && IsScriptsFilterActive;
    public bool IsNotebooksNavActive => IsWorkspaceSectionActive && IsNotebooksFilterActive;

    public bool HasSelection => SelectedTemplate != null || SelectedWorkspaceItem != null;
    public bool IsShowingTemplate => SelectedTemplate != null;
    public bool IsShowingItem => SelectedWorkspaceItem != null;

    private readonly HashSet<string> _pinnedItemIds = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<WorkspaceItemSummary> PinnedItems { get; } = new();
    public ObservableCollection<WorkspaceItemSummary> UnpinnedItems { get; } = new();

    private string PinnedWorkspacesFilePath =>
        Path.Combine(Path.GetDirectoryName(LibraryRootPath) ?? LibraryRootPath, "pinned_workspaces.json");

    public WorkspaceItemSummary? PinnedItem1 => PinnedItems.Count > 0 ? PinnedItems[0] : null;
    public WorkspaceItemSummary? PinnedItem2 => PinnedItems.Count > 1 ? PinnedItems[1] : null;
    public WorkspaceItemSummary? PinnedItem3 => PinnedItems.Count > 2 ? PinnedItems[2] : null;
    public bool HasPinnedItem1 => PinnedItem1 != null;
    public bool HasPinnedItem2 => PinnedItem2 != null;
    public bool HasPinnedItem3 => PinnedItem3 != null;
    public bool HasAnyPinnedItems => PinnedItems.Count > 0;
    public bool HasUnpinnedItems => UnpinnedItems.Count > 0;

    public WorkspaceItemSummary? RecentNotebook => PinnedItem1?.IsNotebook == true ? PinnedItem1
        : (PinnedItem2?.IsNotebook == true ? PinnedItem2 : AllItems.FirstOrDefault(i => i.IsNotebook));

    public WorkspaceItemSummary? RecentScript => PinnedItem1?.IsScript == true ? PinnedItem1
        : (PinnedItem2?.IsScript == true ? PinnedItem2 : AllItems.FirstOrDefault(i => i.IsScript));

    public bool HasRecentNotebook => RecentNotebook != null;
    public bool HasRecentScript => RecentScript != null;

    public string RecentNotebookTitle => RecentNotebook?.Title ?? "customer_churn_model.ipynb";
    public string RecentNotebookPath => RecentNotebook?.DisplayLocation ?? "~/library/";
    public string RecentNotebookTime => RecentNotebook?.FormattedLastModified ?? "2 hours ago";

    public string RecentScriptTitle => RecentScript?.Title ?? "migrate_users_v2.linq";
    public string RecentScriptPath => RecentScript?.DisplayLocation ?? "~/library/";
    public string RecentScriptTime => RecentScript?.FormattedLastModified ?? "Yesterday";

    private long _diskStorageBytes;
    private int _diskStorageFileCount;
    private readonly DispatcherTimer? _telemetryTimer;

    public string RoslynEngineTitle => "Local Roslyn Engine";
    /// <summary>The C# engine's real state, set by the studio host as it starts (shown in the Hub's status panel).</summary>
    [ObservableProperty]
    private string _roslynEngineStatus = "Starting • Roslyn 4.12 & C# 13";

    // The status dot: green only once the engine is ready (it used to be green while it said "Starting").
    public bool IsRoslynEngineReady => RoslynEngineStatus.StartsWith("Ready", StringComparison.Ordinal);
    public bool IsRoslynEngineFailed => RoslynEngineStatus.StartsWith("Failed", StringComparison.Ordinal);
    public bool IsRoslynEngineStarting => !IsRoslynEngineReady && !IsRoslynEngineFailed;

    partial void OnRoslynEngineStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsRoslynEngineReady));
        OnPropertyChanged(nameof(IsRoslynEngineFailed));
        OnPropertyChanged(nameof(IsRoslynEngineStarting));
    }
    public bool IsRoslynEngineActive => true;

    /// <summary>A STUDIO ENVIRONMENT row per language that runs with an installed toolchain (Python): found, or how to install it.</summary>
    public ObservableCollection<Common.ToolchainStatusItem> ToolchainStatuses { get; } = new();

    private readonly Services.Languages.LanguageRegistry _languagesRegistry;
    private Task? _toolchainCheck;

    private readonly object _syncToolchainsGate = new();

    private void OnLanguagesRegistryChanged()
    {
        void Sync()
        {
            try
            {
                SyncToolchainStatuses();
                SyncCreateLanguages();
                _ = CheckToolchainsAsync(lookAgain: false);
            }
            catch
            {
                // Background registry updates must not fault external language registrations
            }
        }

        if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess()) Sync();
        else Dispatcher.UIThread.Post(Sync);
    }

    private void SyncCreateLanguages()
    {
        lock (_syncToolchainsGate)
        {
            var allLangs = _languagesRegistry.All.Where(l => l != null && (l.Storage == LanguageStorageKind.SourceFile || l.IsNamed(LanguageIds.CSharp))).ToList();
            var existing = AvailableCreateLanguages
                .Where(l => l?.Id != null)
                .Select(l => l.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var toRemove = AvailableCreateLanguages.Where(l => l == null || !allLangs.Any(a => a.IsNamed(l.Id))).ToList();
            foreach (var r in toRemove) AvailableCreateLanguages.Remove(r);

            foreach (var l in allLangs)
            {
                if (l.Id != null && !existing.Contains(l.Id)) AvailableCreateLanguages.Add(l);
            }

            SelectedCreateLanguage ??= AvailableCreateLanguages.FirstOrDefault(l => l?.IsNamed(LanguageIds.CSharp) == true) ?? AvailableCreateLanguages.FirstOrDefault();
        }
    }

    private void ReloadTemplates()
    {
        void Sync()
        {
            var existingIds = StarterTemplates
                .Where(t => t?.Id != null)
                .Select(t => t.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var all = CodeTemplateLibrary.GetTemplates();
            var toRemove = StarterTemplates.Where(t => t == null || !all.Any(a => a?.Id == t.Id)).ToList();
            foreach (var r in toRemove) StarterTemplates.Remove(r);
            foreach (var t in all)
            {
                if (t?.Id != null && !existingIds.Contains(t.Id)) StarterTemplates.Add(t);
            }
            OnPropertyChanged(nameof(ScriptTemplates));
            OnPropertyChanged(nameof(NotebookTemplates));
        }

        if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess()) Sync();
        else Dispatcher.UIThread.Post(Sync);
    }

    private void SyncToolchainStatuses()
    {
        lock (_syncToolchainsGate)
        {
            var existing = ToolchainStatuses
                .Where(t => t?.Language?.Id != null)
                .Select(t => t.Language.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var registered = _languagesRegistry.All.Where(l => l?.Toolchain != null && l.Id != null).ToList();

            // Remove any no longer registered
            var toRemove = ToolchainStatuses.Where(t => t == null || t.Language == null || !registered.Any(r => r.IsNamed(t.Language.Id))).ToList();
            foreach (var item in toRemove) ToolchainStatuses.Remove(item);

            // Add newly registered
            foreach (var language in registered)
            {
                if (existing.Contains(language.Id)) continue;
                if (language.Toolchain is { } provider)
                {
                    ToolchainStatuses.Add(new Common.ToolchainStatusItem(language, provider));
                }
            }
        }
    }

    /// <summary>
    /// Looks for each language's toolchain in the background (the Hub calls this when it's shown). <paramref name="lookAgain"/>
    /// forgets what was found before, e.g. after installing Python.
    /// </summary>
    public Task RefreshToolchainStatusesAsync(bool lookAgain = false)
    {
        if (_toolchainCheck is { IsCompleted: false } running) return running;
        return _toolchainCheck = CheckToolchainsAsync(lookAgain);
    }

    [RelayCommand]
    private Task LookAgainForToolchainsAsync() => RefreshToolchainStatusesAsync(lookAgain: true);

    private async Task CheckToolchainsAsync(bool lookAgain)
    {
        var workspace = _storageService.ActiveWorkspaceRootPath;
        foreach (var item in ToolchainStatuses.ToList())
        {
            if (lookAgain) item.ShowChecking();
            ToolchainResolution resolution;
            try
            {
                resolution = await Task.Run(async () =>
                {
                    if (lookAgain) item.Provider.Refresh();
                    return await item.Provider.ResolveAsync(new ToolchainQuery(workspace, workspace)).ConfigureAwait(false);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CSharpEditorPlugin] Couldn't look for {item.Provider.ToolName}: {ex.Message}");
                resolution = ToolchainResolution.NotFound(new MissingToolchainGuidance($"Couldn't look for {item.Provider.ToolName}", ex.Message, Array.Empty<string>()));
            }

            void Show() => item.Show(resolution);
            if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess()) Show();
            else Dispatcher.UIThread.Post(Show);
        }
    }

    public string StorageEngineTitle => "Document Storage";
    public string StorageEngineStatus => Directory.Exists(LibraryRootPath)
        ? $"Connected • {_diskStorageFileCount} docs on disk"
        : "Connected • Library Active";
    public bool IsStorageEngineActive => true;

    public string MemoryUsageText
    {
        get
        {
            try
            {
                var wsBytes = Process.GetCurrentProcess().WorkingSet64;
                var sysBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                var wsMb = wsBytes / (1024.0 * 1024.0);
                var sysGb = Math.Max(1.0, sysBytes / (1024.0 * 1024.0 * 1024.0));

                if (wsMb >= 1024.0)
                {
                    return $"{wsMb / 1024.0:F2} GB / {sysGb:F0} GB";
                }
                return $"{wsMb:F0} MB / {sysGb:F0} GB";
            }
            catch
            {
                var bytes = GC.GetTotalMemory(false);
                var mb = bytes / (1024.0 * 1024.0);
                return $"{mb:F0} MB Heap";
            }
        }
    }

    public double MemoryUsagePercent
    {
        get
        {
            try
            {
                var wsBytes = Process.GetCurrentProcess().WorkingSet64;
                var wsMb = wsBytes / (1024.0 * 1024.0);
                return Math.Clamp((wsMb / 2048.0) * 100.0, 4.0, 100.0);
            }
            catch
            {
                return 6.0;
            }
        }
    }

    public string StorageUsageText
    {
        get
        {
            if (_diskStorageBytes <= 0)
            {
                return $"{TotalScripts + TotalNotebooks} docs";
            }
            if (_diskStorageBytes < 1024)
            {
                return $"{_diskStorageBytes} B";
            }
            if (_diskStorageBytes < 1024 * 1024)
            {
                return $"{_diskStorageBytes / 1024.0:F1} KB";
            }
            return $"{_diskStorageBytes / (1024.0 * 1024.0):F2} MB";
        }
    }

    public double StorageUsagePercent
    {
        get
        {
            // An empty library shows an empty bar; any content at all shows at least a sliver.
            if (_diskStorageBytes <= 0) return 0.0;
            return Math.Clamp((_diskStorageBytes / (1024.0 * 1024.0 * 50.0)) * 100.0, 4.0, 100.0);
        }
    }

    public bool IsLocalServerRunning => true;

    private async Task UpdateDiskStorageAsync()
    {
        var (bytes, count) = await Task.Run(() =>
        {
            long b = 0;
            int c = 0;
            try
            {
                if (!string.IsNullOrEmpty(LibraryRootPath) && Directory.Exists(LibraryRootPath))
                {
                    var dir = new DirectoryInfo(LibraryRootPath);
                    foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        if (file.Name.StartsWith(".")) continue;
                        b += file.Length;
                        c++;
                    }
                }
            }
            catch { }
            return (b, c);
        });

        _diskStorageBytes = bytes;
        _diskStorageFileCount = count;
        OnPropertyChanged(nameof(StorageUsageText));
        OnPropertyChanged(nameof(StorageUsagePercent));
        OnPropertyChanged(nameof(StorageEngineStatus));
    }

    [RelayCommand]
    public async Task OpenRecentNotebookAsync()
    {
        if (RecentNotebook != null)
        {
            await OpenItemAsync(RecentNotebook);
        }
        else
        {
            await CreateNewNotebookAsync();
        }
    }

    [RelayCommand]
    public async Task OpenRecentScriptAsync()
    {
        if (RecentScript != null)
        {
            await OpenItemAsync(RecentScript);
        }
        else
        {
            await CreateNewScriptAsync();
        }
    }

    [RelayCommand]
    public async Task TogglePinAsync(WorkspaceItemSummary? item)
    {
        if (item == null) return;
        if (_pinnedItemIds.Contains(item.Id))
        {
            _pinnedItemIds.Remove(item.Id);
        }
        else
        {
            _pinnedItemIds.Add(item.Id);
        }
        await SavePinnedStateAsync();
        SyncPinnedItems();
    }

    [RelayCommand]
    public async Task PinItemAsync(WorkspaceItemSummary? item)
    {
        if (item == null) return;
        if (_pinnedItemIds.Add(item.Id))
        {
            await SavePinnedStateAsync();
            SyncPinnedItems();
        }
    }

    [RelayCommand]
    public async Task UnpinItemAsync(WorkspaceItemSummary? item)
    {
        if (item == null) return;
        if (_pinnedItemIds.Remove(item.Id))
        {
            await SavePinnedStateAsync();
            SyncPinnedItems();
        }
    }

    [RelayCommand]
    public async Task OpenPinnedItem1Async()
    {
        if (PinnedItem1 != null)
        {
            await OpenItemAsync(PinnedItem1);
        }
        else
        {
            await CreateNewNotebookAsync();
        }
    }

    [RelayCommand]
    public async Task OpenPinnedItem2Async()
    {
        if (PinnedItem2 != null)
        {
            await OpenItemAsync(PinnedItem2);
        }
        else
        {
            await CreateNewScriptAsync();
        }
    }

    [RelayCommand]
    public async Task OpenPinnedItem3Async()
    {
        if (PinnedItem3 != null)
        {
            await OpenItemAsync(PinnedItem3);
        }
        else
        {
            SetActiveDashboardView("Templates");
        }
    }

    [RelayCommand]
    public async Task RefreshWorkspaceAsync()
    {
        await LoadWorkspaceItemsAsync();
    }

    private async Task LoadPinnedStateAsync()
    {
        try
        {
            if (File.Exists(PinnedWorkspacesFilePath))
            {
                var json = await File.ReadAllTextAsync(PinnedWorkspacesFilePath);
                var ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
                if (ids != null)
                {
                    _pinnedItemIds.Clear();
                    foreach (var id in ids) _pinnedItemIds.Add(id);
                }
            }
        }
        catch { }

        if (_pinnedItemIds.Count == 0 && AllItems.Count > 0)
        {
            var nb = AllItems.FirstOrDefault(i => i.IsNotebook);
            if (nb != null) _pinnedItemIds.Add(nb.Id);
            var sc = AllItems.FirstOrDefault(i => i.IsScript && i.Id != nb?.Id);
            if (sc != null) _pinnedItemIds.Add(sc.Id);
            await SavePinnedStateAsync();
        }

        SyncPinnedItems();
    }

    private async Task SavePinnedStateAsync()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_pinnedItemIds.ToList(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(PinnedWorkspacesFilePath, json);
        }
        catch { }
    }

    private void SyncPinnedItems()
    {
        PinnedItems.Clear();
        UnpinnedItems.Clear();

        foreach (var item in AllItems)
        {
            item.IsPinned = _pinnedItemIds.Contains(item.Id);
            if (item.IsPinned)
            {
                PinnedItems.Add(item);
            }
            else
            {
                UnpinnedItems.Add(item);
            }
        }

        OnPropertyChanged(nameof(PinnedItem1));
        OnPropertyChanged(nameof(PinnedItem2));
        OnPropertyChanged(nameof(PinnedItem3));
        OnPropertyChanged(nameof(HasPinnedItem1));
        OnPropertyChanged(nameof(HasPinnedItem2));
        OnPropertyChanged(nameof(HasPinnedItem3));
        OnPropertyChanged(nameof(HasAnyPinnedItems));
        OnPropertyChanged(nameof(HasUnpinnedItems));
        OnPropertyChanged(nameof(PinnedCount));
        OnPropertyChanged(nameof(RecentNotebook));
        OnPropertyChanged(nameof(RecentScript));
        OnPropertyChanged(nameof(RecentNotebookTitle));
        OnPropertyChanged(nameof(RecentNotebookPath));
        OnPropertyChanged(nameof(RecentNotebookTime));
        OnPropertyChanged(nameof(RecentScriptTitle));
        OnPropertyChanged(nameof(RecentScriptPath));
        OnPropertyChanged(nameof(RecentScriptTime));
    }

    public CSharpManagerViewModel(
        IScriptStorageService storageService,
        Action<ScriptDocumentItem>? openScriptAction = null,
        Action<NotebookDocumentItem>? openNotebookAction = null,
        Action? navigateToHomeAction = null,
        Action? navigateToDocsAction = null,
        Action? navigateToBlindProblemsAction = null,
        StudioLanguageServices? languages = null,
        Action<string?>? navigateToSettingsAction = null,
        Action<FryServerDocumentItem>? openServerAction = null,
        IFryServerRegistry? serverRegistry = null,
        IActivityService? activities = null)
    {
        _storageService = storageService;
        _activities = activities ?? NullActivityService.Instance;
        _serverRegistry = serverRegistry ?? FryServerRegistry.Shared;
        _serverRegistry.RunningServersChanged += OnRunningServersChanged;
        _languagesRegistry = (languages ?? StudioLanguageServices.Default).Registry;
        SyncToolchainStatuses();
        SyncCreateLanguages();
        _languagesRegistry.Changed += OnLanguagesRegistryChanged;
        CodeTemplateLibrary.Changed += ReloadTemplates;
        _openScriptAction = openScriptAction;
        _openNotebookAction = openNotebookAction;
        _openServerAction = openServerAction;
        _navigateToHomeAction = navigateToHomeAction;
        _navigateToDocsAction = navigateToDocsAction;
        _navigateToBlindProblemsAction = navigateToBlindProblemsAction;
        _navigateToSettingsAction = navigateToSettingsAction;
        SyncRunningServers();

        foreach (var t in CodeTemplateLibrary.GetTemplates())
        {
            StarterTemplates.Add(t);
        }

        UpdateDiskStorageAsync().FireAndForget(_activities, "Measuring the library");

        try
        {
            _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _telemetryTimer.Tick += (s, e) =>
            {
                OnPropertyChanged(nameof(MemoryUsageText));
                OnPropertyChanged(nameof(MemoryUsagePercent));
            };
            _telemetryTimer.Start();
        }
        catch
        {
        }

        LoadWorkspaceItemsAsync().FireAndForget(_activities, "Loading the workspace list");

        _storageService.ActiveWorkspaceChanged += () => Dispatcher.UIThread.Post(() => LoadWorkspaceItemsAsync().FireAndForget(_activities, "Loading the workspace list"));
        // Files changed outside the studio: catch up now if the Hub is on screen, else on the next visit (OnActivated).
        _storageService.ExternalChangeDetected += () => Dispatcher.UIThread.Post(() =>
        {
            if (_isPageActive) ReloadIfStaleAsync().FireAndForget(_activities, "Refreshing the workspace list");
        });
    }

    // The Hub stays alive while another page is shown; its 3 s memory readout has nothing to update then.
    private bool _isPageActive = true;

    public void OnActivated()
    {
        _isPageActive = true;

        // Other pages (or other programs) may have created, saved or deleted items while the Hub was hidden.
        ReloadIfStaleAsync().FireAndForget(_activities, "Refreshing the workspace list");

        if (_telemetryTimer is not { IsEnabled: false }) return;
        OnPropertyChanged(nameof(MemoryUsageText));
        OnPropertyChanged(nameof(MemoryUsagePercent));
        _telemetryTimer.Start();
    }

    public void OnDeactivated()
    {
        _isPageActive = false;
        _telemetryTimer?.Stop();
    }

    [RelayCommand]
    private void NavigateToHome()
    {
        _navigateToHomeAction?.Invoke();
    }

    // The storage's ContentVersion the list was last loaded at (-1: never). Returning to the Hub reloads only if it moved.
    private long _loadedContentVersion = -1;
    private Task? _pendingReload;

    /// <summary>
    /// Reloads the workspace list if anything in the workspace changed since the last load (a save, a new script, a
    /// rename), and does nothing otherwise. A reload already running is joined, not repeated.
    /// </summary>
    public Task ReloadIfStaleAsync()
    {
        if (_storageService.ContentVersion == _loadedContentVersion) return Task.CompletedTask;
        return _pendingReload is { IsCompleted: false } running ? running : _pendingReload = LoadWorkspaceItemsAsync();
    }

    public async Task LoadWorkspaceItemsAsync()
    {
        // The very first load runs while the studio starts: if it is slow the branded card covers the Hub, but only for
        // a few seconds; after that the list keeps loading behind the Hub's progress line.
        var options = _hasLoadedOnce
            ? new ActivityOptions("Refreshing the workspace list", ActivityLocation.Hub)
            : new ActivityOptions("Loading your workspace", ActivityLocation.Hub) { Blocking = true, YieldAfter = TimeSpan.FromSeconds(3) };
        using var activity = _activities.Start(options);
        await _loadLock.WaitAsync();
        try
        {
            IsRefreshingList = true;
            // Taken before the scan: a change that lands while it runs leaves the list stale, so the next check reloads.
            var version = _storageService.ContentVersion;
            var list = await _storageService.LoadWorkspaceSummariesAsync();
            AllItems.Clear();
            foreach (var item in list)
            {
                AllItems.Add(item);
            }

            UpdateStats();
            await UpdateDiskStorageAsync();
            await LoadPinnedStateAsync();
            await LoadRecentWorkspacesAsync();
            ApplyFilter();
            _loadedContentVersion = version;
            _hasLoadedOnce = true;
        }
        finally
        {
            IsRefreshingList = false;
            _loadLock.Release();
        }
    }

    private void UpdateStats()
    {
        TotalScripts = AllItems.Count(i => i.IsScript);
        TotalNotebooks = AllItems.Count(i => i.IsNotebook);
        OnPropertyChanged(nameof(TotalServers));
        OnPropertyChanged(nameof(IsWorkspaceEmpty));

        OnPropertyChanged(nameof(RecentNotebook));
        OnPropertyChanged(nameof(RecentScript));
        OnPropertyChanged(nameof(RecentServer));
        OnPropertyChanged(nameof(HasRecentNotebook));
        OnPropertyChanged(nameof(HasRecentScript));
        OnPropertyChanged(nameof(HasRecentServer));
        OnPropertyChanged(nameof(RecentNotebookTitle));
        OnPropertyChanged(nameof(RecentNotebookPath));
        OnPropertyChanged(nameof(RecentNotebookTime));
        OnPropertyChanged(nameof(RecentScriptTitle));
        OnPropertyChanged(nameof(RecentScriptPath));
        OnPropertyChanged(nameof(RecentScriptTime));
        OnPropertyChanged(nameof(RecentServerTitle));
        OnPropertyChanged(nameof(RecentServerPath));
        OnPropertyChanged(nameof(RecentServerTime));
        OnPropertyChanged(nameof(MemoryUsageText));
        OnPropertyChanged(nameof(MemoryUsagePercent));
        OnPropertyChanged(nameof(StorageUsageText));
        OnPropertyChanged(nameof(StorageUsagePercent));
        OnPropertyChanged(nameof(StorageEngineStatus));
    }

    partial void OnSearchQueryChanged(string value)
    {
        _visibleItemLimit = ItemPageSize;
        ApplyRecentWorkspacesFilter();
        _filterPause?.Cancel();
        if (AllItems.Count <= 300)
        {
            ApplyFilter();
            return;
        }

        var pause = _filterPause = new CancellationTokenSource();
        _ = ApplyFilterAfterPauseAsync(pause.Token);
    }

    partial void OnSelectedTypeFilterChanged(string value)
    {
        _visibleItemLimit = ItemPageSize;
        OnPropertyChanged(nameof(IsAllFilterActive));
        OnPropertyChanged(nameof(IsWorkspacesFilterActive));
        OnPropertyChanged(nameof(IsNotebooksFilterActive));
        OnPropertyChanged(nameof(IsScriptsFilterActive));
        OnPropertyChanged(nameof(IsServersFilterActive));
        OnPropertyChanged(nameof(IsPinnedFilterActive));
        OnPropertyChanged(nameof(IsWorkspaceAllNavActive));
        OnPropertyChanged(nameof(IsScriptsNavActive));
        OnPropertyChanged(nameof(IsNotebooksNavActive));
        OnPropertyChanged(nameof(IsServersNavActive));
        OnPropertyChanged(nameof(IsLibraryListVisible));
        ApplyRecentWorkspacesFilter();
        ApplyFilter();
    }
    partial void OnSelectedSortOptionChanged(string value)
    {
        _visibleItemLimit = ItemPageSize;
        ApplyFilter();
    }

    partial void OnActiveDashboardViewChanged(string value)
    {
        OnPropertyChanged(nameof(IsWorkspacesTabActive));
        OnPropertyChanged(nameof(IsTemplatesTabActive));
        if (value == "Templates")
        {
            SelectedNavSection = "Templates";
        }
        else
        {
            SelectedNavSection = "Workspace";
        }
    }

    [RelayCommand]
    public void SetActiveDashboardView(string view)
    {
        ActiveDashboardView = view;
    }

    partial void OnSelectedNavSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsWorkspaceSectionActive));
        OnPropertyChanged(nameof(IsTemplatesSectionActive));
        OnPropertyChanged(nameof(IsWorkspaceAllNavActive));
        OnPropertyChanged(nameof(IsScriptsNavActive));
        OnPropertyChanged(nameof(IsNotebooksNavActive));
        if (value == "Templates")
        {
            ActiveDashboardView = "Templates";
        }
        else if (value == "Workspace")
        {
            ActiveDashboardView = "Workspaces";
        }
    }

    partial void OnSelectedTemplateChanged(CodeTemplate? oldValue, CodeTemplate? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null)
        {
            newValue.IsSelected = true;
            SelectedWorkspaceItem = null;
        }
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsShowingTemplate));
    }

    partial void OnSelectedWorkspaceItemChanged(WorkspaceItemSummary? value)
    {
        if (value != null) SelectedTemplate = null;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsShowingItem));
    }

    partial void OnPendingCreateKindChanged(WorkspaceItemKind? value)
    {
        OnPropertyChanged(nameof(IsCreatePromptOpen));
        OnPropertyChanged(nameof(CreatePromptTitle));
        OnPropertyChanged(nameof(IsCreatingNotebook));
        OnPropertyChanged(nameof(IsCreatingScript));
        OnPropertyChanged(nameof(IsCreatingServer));
    }

    partial void OnSelectedFolderPathChanged(string? value) => OnPropertyChanged(nameof(SelectedFolderDisplay));

    [RelayCommand]
    private void SetSelectedTypeFilter(string filter)
    {
        SelectedTypeFilter = filter;
        SelectedNavSection = "Workspace";
        ActiveDashboardView = "Workspaces";
    }

    [RelayCommand]
    private void SetSelectedNavSection(string section)
    {
        if (section == "Docs")
        {
            NavigateToDocs();
            return;
        }

        SelectedNavSection = section;
        if (section == "Templates")
        {
            ActiveDashboardView = "Templates";
        }
        else
        {
            ActiveDashboardView = "Workspaces";
        }
    }

    [RelayCommand]
    private async Task SelectTemplateAsync(CodeTemplate template)
    {
        if (template == null) return;
        if (ReferenceEquals(SelectedTemplate, template))
        {
            await LaunchTemplateAsync(template);
            return;
        }
        SelectedTemplate = template;
    }

    [RelayCommand]
    private void SelectWorkspaceItem(WorkspaceItemSummary item)
    {
        SelectedWorkspaceItem = item;
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
    }

    [RelayCommand]
    private void ResetFilters()
    {
        SearchQuery = string.Empty;
        SelectedTypeFilter = "All";
    }

    private void ApplyFilter()
    {
        var rows = new List<object>();

        var query = SearchQuery.Trim().ToLowerInvariant();
        HasSearchQuery = !string.IsNullOrEmpty(query);
        RefreshFilteredTemplates();
        var typeFilter = SelectedTypeFilter;

        var matches = AllItems.Where(item =>
        {
            if (typeFilter == "Scripts" && !item.IsScript) return false;
            if (typeFilter == "Notebooks" && !item.IsNotebook) return false;
            if (typeFilter == "Servers" && !item.IsServer) return false;
            if (typeFilter == "Pinned" && !item.IsPinned) return false;

            if (string.IsNullOrEmpty(query)) return true;

            return item.Title.ToLowerInvariant().Contains((string)query) ||
                   item.Description.ToLowerInvariant().Contains((string)query) ||
                   item.Category.ToLowerInvariant().Contains((string)query) ||
                   item.ExecutionMode.ToLowerInvariant().Contains((string)query) ||
                   item.DisplayLocation.ToLowerInvariant().Contains((string)query);
        });

        matches = SelectedSortOption switch
        {
            "Title (A-Z)" => matches.OrderBy(x => x.Title),
            "Execution Count" => matches.OrderByDescending(x => x.ExecutionCount).ThenByDescending(x => x.LastModified),
            _ => matches.OrderByDescending(x => x.LastModified)
        };

        var matchList = matches.ToList();

        // At most one workspace root is ever active at a time, so all external matches belong to the
        // same opened folder and share a single group header (subfolders differ in FolderPath but not
        // in which workspace they belong to).
        var emittedExternalHeader = false;

        foreach (var match in matchList)
        {
            if (!match.IsExternal)
            {
                rows.Add(match);
                continue;
            }

            if (emittedExternalHeader) continue;
            emittedExternalHeader = true;

            var groupMembers = matchList.Where(m => m.IsExternal).ToList();

            rows.Add(new WorkspaceGroupHeaderViewModel
            {
                Title = match.ExternalWorkspaceName,
                FolderPath = _storageService.ActiveWorkspaceRootPath,
                ItemCount = groupMembers.Count
            });

            foreach (var member in groupMembers)
            {
                rows.Add(member);
            }
        }

        // Every card is a heavy control: drawing thousands takes seconds and gigabytes. Draw the first ones (the list is
        // sorted, so those are the ones wanted most) and offer the rest with "Show more"; searching covers them all.
        var shown = rows.Count > _visibleItemLimit ? rows.GetRange(0, _visibleItemLimit) : rows;
        FilteredItems.ReplaceAll(shown);
        HiddenItemCount = rows.Count - shown.Count;

        HasFilteredItems = matchList.Count > 0;
        FilteredItemCount = matchList.Count;
    }

    private const int ItemPageSize = 100;
    private int _visibleItemLimit = ItemPageSize;

    /// <summary>How many matching items are not drawn yet.</summary>
    [ObservableProperty]
    private int _hiddenItemCount;

    public bool HasHiddenItems => HiddenItemCount > 0;

    public string HiddenItemsText => $"Show {Math.Min((int)ItemPageSize, (int)HiddenItemCount)} more  ·  {HiddenItemCount:N0} not shown";

    partial void OnHiddenItemCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasHiddenItems));
        OnPropertyChanged(nameof(HiddenItemsText));
    }

    [RelayCommand]
    private void ShowMoreItems()
    {
        _visibleItemLimit += ItemPageSize;
        ApplyFilter();
    }

    // Re-filtering after every key is instant for a small library but not for thousands of items, so a big library
    // waits for a short pause in typing.
    private CancellationTokenSource? _filterPause;

    private async Task ApplyFilterAfterPauseAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(200, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!token.IsCancellationRequested) ApplyFilter();
        });
    }

    [RelayCommand]
    public async Task OpenItemAsync(WorkspaceItemSummary item)
    {
        if (item == null) return;

        var token = _open.Begin();
        using var activity = _activities.Start(new ActivityOptions($"Opening {item.Title}", ActivityLocation.Hub), token);
        if (item.IsServer)
        {
            var server = await Task.Run(() => _storageService.LoadServerDocumentAsync(item.Id), token);
            if (server != null && !token.IsCancellationRequested)
            {
                _openServerAction?.Invoke(server);
            }
        }
        else if (item.IsNotebook)
        {
            var nb = await Task.Run(() => _storageService.LoadNotebookAsync(item.Id), token);
            if (nb != null && !token.IsCancellationRequested)
            {
                _openNotebookAction?.Invoke(nb);
            }
        }
        else
        {
            var sc = await Task.Run(() => _storageService.LoadScriptAsync(item.Id), token);
            if (sc != null && !token.IsCancellationRequested)
            {
                _openScriptAction?.Invoke(sc);
            }
        }
    }

    [RelayCommand]
    public async Task CreateNewScriptAsync(string? templateId = null)
    {
        if (IsLaunching) return;
        await OpenCreatePromptAsync(WorkspaceItemKind.Script, templateId, "New Automation Script");
    }

    [RelayCommand]
    public async Task CreateNewNotebookAsync(string? templateId = null)
    {
        if (IsLaunching) return;
        await OpenCreatePromptAsync(WorkspaceItemKind.Notebook, templateId, "New Interactive Notebook");
    }

    private Task OpenCreatePromptAsync(WorkspaceItemKind kind, string? templateId, string defaultName)
    {
        _pendingTemplateId = templateId;
        var template = StarterTemplates.FirstOrDefault(t => t.Id == templateId);
        NewItemName = template?.Title ?? defaultName;
        SelectedFolderPath = null;
        LocationWarning = null;
        if (kind == WorkspaceItemKind.Script)
        {
            if (!string.IsNullOrEmpty(template?.LanguageId))
            {
                SelectedCreateLanguage = AvailableCreateLanguages.FirstOrDefault(l => l.IsNamed(template.LanguageId));
            }
            SelectedCreateLanguage ??= AvailableCreateLanguages.FirstOrDefault(l => l.IsNamed(LanguageIds.CSharp)) ?? AvailableCreateLanguages.FirstOrDefault();
        }
        PendingCreateKind = kind;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void CancelCreatePrompt()
    {
        PendingCreateKind = null;
    }

    [RelayCommand]
    public async Task ConfirmCreateAsync()
    {
        if (PendingCreateKind is not { } kind) return;
        if (IsLaunching) return;

        using (_activities.Start(new ActivityOptions($"Creating {(string.IsNullOrWhiteSpace(NewItemName) ? "document" : NewItemName.Trim())}", ActivityLocation.Hub)))
        {
            IsLaunching = true;
            try
            {
                var folderPath = SelectedFolderPath;
                var title = string.IsNullOrWhiteSpace(NewItemName)
                    ? (kind == WorkspaceItemKind.Notebook ? "New Interactive Notebook" : (kind == WorkspaceItemKind.Server ? "New API Server" : "New Automation Script"))
                    : NewItemName.Trim();

                if (kind == WorkspaceItemKind.Notebook)
                {
                    await CreateNewNotebookCoreAsync(_pendingTemplateId, folderPath, title);
                }
                else if (kind == WorkspaceItemKind.Server)
                {
                    await CreateNewServerCoreAsync(_pendingTemplateId, folderPath, title);
                }
                else
                {
                    if (SelectedCreateLanguage != null && !SelectedCreateLanguage.IsNamed("csharp"))
                    {
                        var lang = SelectedCreateLanguage;
                        var ext = lang.FileExtensions.FirstOrDefault() ?? ".txt";
                        var rawName = string.IsNullOrWhiteSpace(NewItemName) ? $"New {lang.DisplayName} Script" : NewItemName.Trim();
                        var fileName = rawName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? rawName : rawName + ext;
                        var newScript = await _storageService.CreateNewSourceFileAsync(lang.Id, fileName, folderPath, lang.NewFileTemplate);
                        if (newScript != null)
                        {
                            // Open first: the list catching up must not delay the document the user asked for.
                            _openScriptAction?.Invoke(newScript);
                            await LoadWorkspaceItemsAsync();
                        }
                    }
                    else
                    {
                        await CreateNewScriptCoreAsync(_pendingTemplateId, folderPath, title);
                    }
                }
            }
            finally
            {
                IsLaunching = false;
                PendingCreateKind = null;
            }
        }
    }

    private async Task CreateNewScriptCoreAsync(string? templateId, string? folderPath = null, string? explicitTitle = null)
    {
        var template = StarterTemplates.FirstOrDefault(t => t.Id == templateId);
        var title = explicitTitle ?? template?.Title ?? "New Automation Script";

        if (AllItems.Any(i => i.IsScript && string.Equals(i.Title, title, StringComparison.OrdinalIgnoreCase)))
        {
            title = $"{title} (Copy)";
        }

        var newScript = await _storageService.CreateNewScriptAsync(title, templateId, folderPath);
        // Open first: the list catching up must not delay the document the user asked for.
        _openScriptAction?.Invoke(newScript);
        await LoadWorkspaceItemsAsync();
    }

    private async Task CreateNewNotebookCoreAsync(string? templateId, string? folderPath = null, string? explicitTitle = null)
    {
        var template = StarterTemplates.FirstOrDefault(t => t.Id == templateId);
        var title = explicitTitle ?? template?.Title ?? "New Interactive Notebook";

        if (AllItems.Any(i => i.IsNotebook && string.Equals(i.Title, title, StringComparison.OrdinalIgnoreCase)))
        {
            title = $"{title} (Copy)";
        }

        var newNb = await _storageService.CreateNewNotebookAsync(title, templateId, folderPath);
        _openNotebookAction?.Invoke(newNb);
        await LoadWorkspaceItemsAsync();
    }

    [RelayCommand]
    public async Task LaunchTemplateAsync(CodeTemplate template)
    {
        if (template == null) return;
        if (IsLaunching) return;

        using (_activities.Start(new ActivityOptions($"Opening {template.Title}", ActivityLocation.Hub)))
        {
            IsLaunching = true;
            try
            {
                var existing = AllItems.FirstOrDefault(i =>
                    (template.Kind == WorkspaceItemKind.Notebook && i.IsNotebook || template.Kind == WorkspaceItemKind.Script && i.IsScript) &&
                    (string.Equals(i.Id, template.Id, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(i.Title, template.Title, StringComparison.OrdinalIgnoreCase)));

                if (existing != null)
                {
                    await OpenItemAsync(existing);
                    return;
                }

                if (template.Kind == WorkspaceItemKind.Notebook)
                {
                    await CreateNewNotebookCoreAsync(template.Id);
                }
                else
                {
                    await CreateNewScriptCoreAsync(template.Id);
                }
            }
            finally
            {
                IsLaunching = false;
            }
        }
    }

    [RelayCommand]
    public async Task DeleteItemAsync(WorkspaceItemSummary item)
    {
        if (item == null) return;

        if (ReferenceEquals(SelectedWorkspaceItem, item))
        {
            SelectedWorkspaceItem = null;
        }

        AllItems.Remove(item);
        if (_pinnedItemIds.Remove(item.Id))
        {
            await SavePinnedStateAsync();
            SyncPinnedItems();
        }

        await _storageService.DeleteItemAsync(item.Id);
        UpdateStats();
        await UpdateDiskStorageAsync();

        ApplyFilter();
    }

    [RelayCommand]
    public void DismissStatusBanner()
    {
        HasStatusBannerMessage = false;
        StatusBannerMessage = null;
    }

    [RelayCommand]
    public async Task OpenExistingProjectAsync(string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (IsLaunching) return;

        // Switching workspace: the one place where nothing else makes sense meanwhile, so it may show the card (with Cancel).
        using (var activity = _activities.Start(new ActivityOptions("Opening folder", ActivityLocation.Window)
               {
                   Detail = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : path,
                   Blocking = true,
               }))
        {
            IsLaunching = true;
            try
            {
                var result = await Task.Run(() => _storageService.OpenExternalProjectAsync(path));
                if (result.Success)
                {
                    await LoadWorkspaceItemsAsync();
                    StatusBannerMessage = result.Message;
                    IsStatusBannerError = false;
                    HasStatusBannerMessage = true;

                    WorkspaceCustomizationLoader.LoadInBackground(_storageService.ActiveWorkspaceRootPath, _activities);

                    if (!string.IsNullOrEmpty(result.PrimaryDocumentId))
                    {
                        if (result.PrimaryDocumentKind == WorkspaceItemKind.Server)
                        {
                            var server = await Task.Run(async () => await _storageService.LoadServerDocumentAsync(result.PrimaryDocumentId));
                            if (server != null)
                            {
                                _openServerAction?.Invoke(server);
                            }
                        }
                        else if (result.PrimaryDocumentKind == WorkspaceItemKind.Notebook)
                        {
                            var nb = await Task.Run(async () => await _storageService.LoadNotebookAsync(result.PrimaryDocumentId));
                            if (nb != null)
                            {
                                _openNotebookAction?.Invoke(nb);
                            }
                        }
                        else
                        {
                            var sc = await Task.Run(async () => await _storageService.LoadScriptAsync(result.PrimaryDocumentId));
                            if (sc != null)
                            {
                                _openScriptAction?.Invoke(sc);
                            }
                        }
                    }
                }
                else
                {
                    StatusBannerMessage = result.Message;
                    IsStatusBannerError = true;
                    HasStatusBannerMessage = true;
                }
            }
            catch (OperationCanceledException)
            {
                // The user cancelled the switch.
            }
            catch (Exception ex)
            {
                StatusBannerMessage = $"Failed to open project: {ex.Message}";
                IsStatusBannerError = true;
                HasStatusBannerMessage = true;
            }
            finally
            {
                IsLaunching = false;
            }
        }
    }
}
