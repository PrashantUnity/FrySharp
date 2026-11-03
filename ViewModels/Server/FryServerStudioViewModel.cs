using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.Server;
using PdfEditorApp.Plugins.CSharpEditor.Services;
using PdfEditorApp.Plugins.CSharpEditor.Services.Server;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Server;

public partial class FryServerStudioViewModel : ObservableObject, IDisposable
{
    private IFryHttpServerEngine _engine;
    private readonly IFryServerRegistry _registry;
    private readonly IPortAvailabilityService _portService;
    private readonly IScriptStorageService? _storageService;
    private readonly Action? _backToHubAction;
    private readonly Action? _backToHomeAction;

    [ObservableProperty]
    private GridLength _sideBarGridLength = new(270, GridUnitType.Pixel);

    private double _savedSideBarWidth = 270;

    [ObservableProperty]
    private GridLength _bottomDeckGridLength = new(180, GridUnitType.Pixel);

    private double _savedBottomDeckHeight = 180;

    public int EndpointCount => Cells.Count(c => c.IsEndpoint);
    public int MiddlewareCount => Cells.Count(c => c.Type == FryServerCellType.Middleware);
    public int StartupCount => Cells.Count(c => c.Type == FryServerCellType.Startup);
    public int ScenarioCount => Cells.Count(c => c.Type == FryServerCellType.Scenario);
    public double AverageLatencyMs => TrafficLog.Count > 0 ? TrafficLog.Average(t => t.ElapsedMilliseconds) : 0;

    [ObservableProperty]
    private FryServerDocumentItem _document;

    [ObservableProperty]
    private string _documentTitle = "API Server";

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private FryServerCellViewModel? _activeCell;

    public ObservableCollection<FryServerCellViewModel> Cells { get; } = new();
    public ObservableCollection<FryServerTrafficLogItem> TrafficLog { get; } = new();

    // 5-Zone VS Code Layout properties
    [ObservableProperty]
    private int _selectedActivityBarIndex = 0; // 0 = Endpoints Outline, 1 = Traffic Logs, 2 = Explorer, 3 = Running Servers

    [ObservableProperty]
    private bool _isSideBarVisible = true;

    partial void OnIsSideBarVisibleChanged(bool value)
    {
        if (value)
        {
            SideBarGridLength = new GridLength(_savedSideBarWidth > 120 ? _savedSideBarWidth : 270, GridUnitType.Pixel);
        }
        else
        {
            if (SideBarGridLength.IsAbsolute && SideBarGridLength.Value > 120)
            {
                _savedSideBarWidth = SideBarGridLength.Value;
            }
            SideBarGridLength = new GridLength(0, GridUnitType.Pixel);
        }
    }

    [ObservableProperty]
    private string _sideBarTitle = "OUTLINE";

    public bool IsEndpointsOutlineActive => SelectedActivityBarIndex == 0;
    public bool IsTrafficActive => SelectedActivityBarIndex == 1;
    public bool IsExplorerActive => SelectedActivityBarIndex == 2;
    public bool IsServersActive => SelectedActivityBarIndex == 3;

    [RelayCommand]
    public void SelectActivityBarItem(string? indexStr)
    {
        if (int.TryParse(indexStr, out var idx))
        {
            SelectActivityBarItem(idx);
        }
    }

    public void SelectActivityBarItem(int index)
    {
        if (SelectedActivityBarIndex == index && IsSideBarVisible)
        {
            IsSideBarVisible = false;
            return;
        }
        SelectedActivityBarIndex = index;
        IsSideBarVisible = true;
        SideBarTitle = index switch
        {
            0 => "OUTLINE",
            1 => "TRAFFIC LOGS",
            2 => "EXPLORER",
            3 => "RUNNING SERVERS",
            _ => "SIDEBAR"
        };
        OnPropertyChanged(nameof(IsEndpointsOutlineActive));
        OnPropertyChanged(nameof(IsTrafficActive));
        OnPropertyChanged(nameof(IsExplorerActive));
        OnPropertyChanged(nameof(IsServersActive));
    }

    [ObservableProperty]
    private bool _isBottomPanelVisible = true;

    partial void OnIsBottomPanelVisibleChanged(bool value)
    {
        if (value)
        {
            BottomDeckGridLength = new GridLength(_savedBottomDeckHeight > 60 ? _savedBottomDeckHeight : 180, GridUnitType.Pixel);
        }
        else
        {
            if (BottomDeckGridLength.IsAbsolute && BottomDeckGridLength.Value > 60)
            {
                _savedBottomDeckHeight = BottomDeckGridLength.Value;
            }
            BottomDeckGridLength = new GridLength(0, GridUnitType.Pixel);
        }
    }

    [ObservableProperty]
    private int _selectedBottomPanelIndex = 0; // 0 = Traffic Log, 1 = Output, 2 = Problems

    [ObservableProperty]
    private double _editorFontSize = 13.0;

    public string BreadcrumbFolder => "Workspace";
    public string BreadcrumbDocument => $"{DocumentTitle}.fryserver";

    public string ActiveCellBadgeText => ActiveCell != null
        ? (ActiveCell.IsEndpoint ? $"{ActiveCell.Method} {ActiveCell.Route}" : $"{ActiveCell.TypeBadgeText}: {ActiveCell.Title}")
        : "Overview";

    public string ActiveCellTypeIcon => ActiveCell?.Type switch
    {
        FryServerCellType.Endpoint => "Api",
        FryServerCellType.Startup => "DatabasePlus",
        FryServerCellType.Middleware => "FilterVariant",
        FryServerCellType.Scenario => "TestTube",
        FryServerCellType.Background => "ClockOutline",
        FryServerCellType.Markdown => "FileDocumentOutline",
        _ => "ServerNetwork"
    };

    /// <summary>The breadcrumb icon's theme colour: the active cell's badge colour, or the accent.</summary>
    public string ActiveCellTypeColorKey => ActiveCell?.TypeBadgeFgKey ?? "DsPrimaryBrush";

    partial void OnActiveCellChanged(FryServerCellViewModel? oldValue, FryServerCellViewModel? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnActiveCellPropertyChanged;
        if (newValue != null) newValue.PropertyChanged += OnActiveCellPropertyChanged;
        OnPropertyChanged(nameof(ActiveCellBadgeText));
        OnPropertyChanged(nameof(ActiveCellTypeIcon));
        OnPropertyChanged(nameof(ActiveCellTypeColorKey));
    }

    // The active cell's method or type changed: the breadcrumb shows it at once.
    private void OnActiveCellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FryServerCellViewModel.TypeBadgeFgKey))
        {
            OnPropertyChanged(nameof(ActiveCellBadgeText));
            OnPropertyChanged(nameof(ActiveCellTypeIcon));
            OnPropertyChanged(nameof(ActiveCellTypeColorKey));
        }
    }

    partial void OnDocumentTitleChanged(string value)
    {
        OnPropertyChanged(nameof(BreadcrumbDocument));
    }

    public IFryHttpServerEngine Engine => _engine;

    public FryServerStudioViewModel(
        FryServerDocumentItem? document = null,
        string? filePath = null,
        IFryHttpServerEngine? engine = null,
        IPortAvailabilityService? portService = null,
        IScriptStorageService? storageService = null,
        Action? backToHubAction = null,
        Action? backToHomeAction = null,
        IFryServerRegistry? registry = null,
        Action<ScriptDocumentItem>? openScriptAction = null,
        Action<NotebookDocumentItem>? openNotebookAction = null,
        PdfEditorApp.Plugins.CSharpEditor.Services.Common.IStudioDialogService? dialogs = null)
    {
        _dialogs = dialogs;
        _portService = portService ?? new PortAvailabilityService();
        _registry = registry ?? FryServerRegistry.Shared;
        _storageService = storageService;
        _backToHubAction = backToHubAction;
        _backToHomeAction = backToHomeAction;

        var doc = document ?? CreateDefaultDocument();
        _document = doc;
        _filePath = filePath ?? string.Empty;
        _documentTitle = string.IsNullOrWhiteSpace(doc.Title) ? "API Server" : doc.Title;

        _portInput = doc.ServerConfig.Port;
        _hostInput = doc.ServerConfig.Host;
        _apiPrefixInput = doc.ServerConfig.ApiPrefix;
        _corsEnabled = doc.ServerConfig.EnableCors;

        // Check if this document is already running in the registry!
        var existing = _registry.GetServer(doc.Id);
        if (existing != null)
        {
            _engine = existing.Engine;
            _isServerRunning = existing.IsRunning;
            _boundPort = existing.BoundPort;
            _baseUrl = existing.BaseUrl;
            _serverStatusText = $"Listening on :{existing.BoundPort}";
        }
        else
        {
            _engine = engine ?? new FryHttpListenerServerEngine(_portService);
        }

        _engine.StateChanged += OnServerStateChanged;
        _engine.RequestProcessed += OnRequestProcessed;
        _registry.RunningServersChanged += OnRunningServersChanged;

        PopulateCells();
        SyncRunningServers();
        _ = CheckPortAvailabilityAsync();

        _openScriptAction = openScriptAction;
        _openNotebookAction = openNotebookAction;
        if (storageService != null) Explorer = CreateExplorer(storageService);
    }

    private void PopulateCells()
    {
        Cells.Clear();
        foreach (var cellItem in Document.Cells)
        {
            var cellVm = new FryServerCellViewModel(cellItem, _engine);
            Cells.Add(cellVm);
        }

        ActiveCell = Cells.FirstOrDefault();
        NotifyCellCounts();
    }

    public void NotifyCellCounts()
    {
        OnPropertyChanged(nameof(EndpointCount));
        OnPropertyChanged(nameof(MiddlewareCount));
        OnPropertyChanged(nameof(StartupCount));
        OnPropertyChanged(nameof(ScenarioCount));
    }

    private static FryServerDocumentItem CreateDefaultDocument()
    {
        return new FryServerDocumentItem
        {
            Title = "API Server",
            ServerConfig = new FryServerConfiguration
            {
                Host = "localhost",
                Port = 5000,
                ApiPrefix = "/api",
                EnableCors = true
            },
            Cells = new()
            {
                new FryServerCellItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Type = FryServerCellType.Endpoint,
                    Title = "Get User Profile",
                    Method = "GET",
                    Route = "/users/{id}",
                    Source = @"
                        var user = new { id, name = ""Alice Vance"", role = ""Engineer"" };
                        return Ok(user);
                    ",
                    TestHarness = new FryServerTestHarnessItem
                    {
                        PathParams = new() { ["id"] = "123" }
                    }
                }
            }
        };
    }

    [RelayCommand]
    public void SelectCell(FryServerCellViewModel? cell)
    {
        if (cell == null) return;
        foreach (var c in Cells) c.IsSelected = false;
        cell.IsSelected = true;
        ActiveCell = cell;
    }

    [RelayCommand]
    public void ToggleSideBar() => IsSideBarVisible = !IsSideBarVisible;

    [RelayCommand]
    public void ToggleBottomPanel() => IsBottomPanelVisible = !IsBottomPanelVisible;

    public ObservableCollection<FryRunningServerItem> RunningServers { get; } = new();
    public int RunningServerCount => RunningServers.Count;
    public bool HasRunningServers => RunningServers.Count > 0;
    public string RunningServersBadgeText => RunningServerCount > 0 ? RunningServerCount.ToString() : string.Empty;

    private void OnRunningServersChanged()
    {
        Dispatcher.UIThread.Post(SyncRunningServers);
    }

    public void SyncRunningServers()
    {
        RunningServers.Clear();
        foreach (var server in _registry.RunningServers)
        {
            RunningServers.Add(server);
        }
        OnPropertyChanged(nameof(RunningServerCount));
        OnPropertyChanged(nameof(HasRunningServers));
        OnPropertyChanged(nameof(RunningServersBadgeText));
    }

    [RelayCommand]
    public async Task StopRunningServerAsync(FryRunningServerItem? server)
    {
        if (server == null) return;
        await _registry.StopServerAsync(server.ServerId).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task StopAllRunningServersAsync()
    {
        await _registry.StopAllAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    public void SwitchToRunningServer(FryRunningServerItem? server)
    {
        if (server?.Document != null)
        {
            LoadDocument(server.Document, server.FilePath);
        }
    }

    // ── Explorer: the workspace folder, as in the other studios ──────────────

    private readonly Action<ScriptDocumentItem>? _openScriptAction;
    private readonly Action<NotebookDocumentItem>? _openNotebookAction;

    /// <summary>The workspace folder's files, as the Code Studio and Notebook show them (null without a workspace).</summary>
    public StudioWorkspaceExplorer? Explorer { get; }

    /// <summary>The server's overview (runtime, metrics, exports) under the Explorer: a section that opens on demand.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerInfoChevron))]
    private bool _isServerInfoExpanded;

    public string ServerInfoChevron => IsServerInfoExpanded ? "ChevronDown" : "ChevronRight";

    [RelayCommand]
    private void ToggleServerInfo() => IsServerInfoExpanded = !IsServerInfoExpanded;

    // Asks before something is deleted for good (null: nobody to ask).
    private readonly PdfEditorApp.Plugins.CSharpEditor.Services.Common.IStudioDialogService? _dialogs;

    private StudioWorkspaceExplorer CreateExplorer(IScriptStorageService storage)
    {
        var explorer = new StudioWorkspaceExplorer(storage, OpenFromExplorerAsync, async folder =>
        {
            var created = await storage.CreateNewServerDocumentAsync("New Server", folder);
            return created.Id;
        }, dialogs: _dialogs);

        // The open server document's file was renamed or deleted from the Explorer.
        explorer.DocumentRenamed += (id, fileName) =>
        {
            if (!string.Equals(id, Document?.Id, StringComparison.OrdinalIgnoreCase)) return;
            Document!.Title = Path.GetFileNameWithoutExtension(fileName);
            DocumentTitle = Document.Title;
            if (!string.IsNullOrEmpty(FilePath)) FilePath = Path.Combine(Path.GetDirectoryName(FilePath) ?? string.Empty, fileName);
        };
        explorer.DocumentDeleted += id =>
        {
            if (string.Equals(id, Document?.Id, StringComparison.OrdinalIgnoreCase)) LoadDocument(CreateDefaultDocument());
        };

        storage.ActiveWorkspaceChanged += () => Dispatcher.UIThread.Post(() => _ = explorer.RefreshExplorerAsync());
        storage.ExternalChangeDetected += () => Dispatcher.UIThread.Post(() => _ = explorer.RefreshIfStaleAsync());
        explorer.Highlight(Document?.Id); // marked when the listing arrives
        _ = explorer.RefreshExplorerAsync();
        return explorer;
    }

    // A server document opens here; a notebook in the Notebook Studio; anything else in the Code Studio.
    private async Task OpenFromExplorerAsync(PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer.ExplorerItemViewModel item)
    {
        if (_storageService == null || string.IsNullOrEmpty(item.DocumentId)) return;
        var extension = item.FileExtension;
        if (extension.Equals(".fryserver", StringComparison.OrdinalIgnoreCase))
        {
            if (await _storageService.LoadServerDocumentAsync(item.DocumentId) is { } server) LoadDocument(server, item.FullPath);
        }
        else if (extension.Equals(".frynb", StringComparison.OrdinalIgnoreCase))
        {
            if (await _storageService.LoadNotebookAsync(item.DocumentId) is { } notebook) _openNotebookAction?.Invoke(notebook);
        }
        else if (await _storageService.LoadScriptAsync(item.DocumentId) is { } script)
        {
            _openScriptAction?.Invoke(script);
        }
    }

    public void LoadDocument(FryServerDocumentItem document, string? filePath = null)
    {
        _engine.StateChanged -= OnServerStateChanged;
        _engine.RequestProcessed -= OnRequestProcessed;

        Document = document;
        FilePath = filePath ?? string.Empty;
        DocumentTitle = string.IsNullOrWhiteSpace(document.Title) ? "API Server" : document.Title;

        PortInput = document.ServerConfig.Port;
        HostInput = document.ServerConfig.Host;
        ApiPrefixInput = document.ServerConfig.ApiPrefix;
        CorsEnabled = document.ServerConfig.EnableCors;

        var existing = _registry.GetServer(document.Id);
        if (existing != null)
        {
            _engine = existing.Engine;
            IsServerRunning = existing.IsRunning;
            BoundPort = existing.BoundPort;
            BaseUrl = existing.BaseUrl;
            ServerStatusText = $"Listening on :{existing.BoundPort}";
        }
        else
        {
            _engine = new FryHttpListenerServerEngine(_portService);
            IsServerRunning = false;
            ServerStatusText = "Stopped";
        }

        _engine.StateChanged += OnServerStateChanged;
        _engine.RequestProcessed += OnRequestProcessed;

        PopulateCells();
        _ = CheckPortAvailabilityAsync();
        SyncRunningServers();
        Explorer?.Highlight(document.Id);
    }

    [RelayCommand]
    public async Task SaveDocumentAsync()
    {
        if (_storageService == null || Document == null) return;
        await _storageService.SaveServerDocumentAsync(Document);
    }

    /// <summary>
    /// Save As: writes this server document to <paramref name="filePath"/> as a document of its own and shows it. A server
    /// that is running keeps running under the document it was started from.
    /// </summary>
    public async Task<bool> SaveDocumentAsAsync(string filePath)
    {
        if (_storageService == null || Document == null) return false;
        var copy = System.Text.Json.JsonSerializer.Deserialize<FryServerDocumentItem>(System.Text.Json.JsonSerializer.Serialize(Document))!;
        var written = await _storageService.SaveServerDocumentAsAsync(copy, filePath);
        if (written == null) return false;
        LoadDocument(copy, written);
        return true;
    }

    [RelayCommand]
    public void ReturnToHub() => _backToHubAction?.Invoke();

    [RelayCommand]
    public void ReturnToHome() => _backToHomeAction?.Invoke();

    public void Dispose()
    {
        _engine.StateChanged -= OnServerStateChanged;
        _engine.RequestProcessed -= OnRequestProcessed;
        _registry.RunningServersChanged -= OnRunningServersChanged;
    }
}
