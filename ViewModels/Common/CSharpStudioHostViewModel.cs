using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FryWorld.Sdk.Services;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.Server;
using PdfEditorApp.Plugins.CSharpEditor.Services.Activities;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Documentation;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Kernels;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;
using PdfEditorApp.Plugins.CSharpEditor.Services.Problems.Catalogs.Blind75;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Startup;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Templates;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;

public partial class CSharpStudioHostViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The currently active studio host instance across the application, used by MCP tools and ambient services.
    /// </summary>
    public static CSharpStudioHostViewModel? ActiveInstance { get; internal set; }

    private const string PluginId = "com.frypdf.plugin.csharpeditor";

    // 0 = no automatic timeout: execution runs until the user clicks Stop, matching Jupyter/
    // dotnet-interactive. A fixed ceiling only made sense as a safety net for when Stop couldn't
    // reliably interrupt a running script — now that it can (see ExecutionAbandonment), an
    // unrelated slow-but-working call (e.g. a network request) shouldn't be cut off arbitrarily.
    private const int DefaultExecutionTimeoutSeconds = 0;

    private readonly IScriptStorageService _storageService;
    private readonly ISettingsManager? _settingsStore;

    // One progress store for the whole studio: the Blind 75 page and Code Studio (which marks a problem solved when
    // every case passes) must see each other's changes, and two stores on one file would overwrite each other.
    private readonly IBlindProgressService _blindProgress;

    // One set of languages for every page: the same registry lists the workspace's files, and the same saved toolchain
    // choices, studio environments and processes serve the Code Studio, the notebooks and the Hub.
    private readonly StudioLanguageServices _languages;
    public StudioLanguageServices Languages => _languages;
    public IScriptStorageService StorageService => _storageService;
    private RoslynCompilerService? _compilerService;
    private ScriptExecutionEngine? _executionEngine;

    [ObservableProperty]
    private object _currentPage;

    // Pages are kept alive once built, so leaving one no longer detaches it; tell the pages that care (timers etc.).
    partial void OnCurrentPageChanged(object? oldValue, object newValue)
    {
        if (ReferenceEquals(oldValue, newValue)) return;
        (oldValue as IPageLifecycle)?.OnDeactivated();
        (newValue as IPageLifecycle)?.OnActivated();
    }

    [ObservableProperty]
    private bool _isOnManagerPage = true;

    [ObservableProperty]
    private bool _isEngineLoading = true;

    [ObservableProperty]
    private string _engineStatus = "Warming up Roslyn .NET engine…";

    [ObservableProperty]
    private string _activeDocumentTitle = "Hub";

    // Every page reports the work the user may wait for here; the presenter decides what of it to show, and when.
    private readonly IActivityService _activities;

    /// <summary>Where the studio's pages and services report running work.</summary>
    public IActivityService Activities => _activities;

    /// <summary>What the screen shows of that work: progress lines, the status entry, toasts and the blocking card.</summary>
    public ActivityPresenterViewModel Activity { get; }

    /// <summary>The studio's one modal question (unsaved changes, deletes, discards), drawn over every page.</summary>
    public StudioDialogViewModel Dialogs { get; } = new();

    // Opening a document from one place and then another before the first is ready: only the last one may take the page.
    private readonly LatestOperation _navigation = new();

    /// <summary>
    /// Action callback for standalone test runners or host shells to close the preview window.
    /// </summary>
    public Action? RequestClose { get; set; }

    // The C# engine starts in two stages off the UI thread; opening a document waits only for the first one.
    private readonly EngineReadiness _engine;

    /// <summary>The engine's start-up state (the Hub shows it; opening a document waits for its Core stage).</summary>
    public EngineReadiness Engine => _engine;

    /// <summary>Completes when the studio pages can open documents (the engine's Core stage).</summary>
    public Task InitTask => _engine.CoreReady;

    public Hub.CSharpManagerViewModel ManagerViewModel { get; }
    public Docs.CSharpDocsViewModel DocsViewModel { get; }
    public BlindProblems.CSharpBlindProblemsViewModel BlindProblemsViewModel { get; }
    public Settings.CSharpSettingsViewModel SettingsViewModel { get; }
    public CodeStudio.CSharpCodeStudioViewModel? CodeStudioViewModel { get; internal set; }
    public Notebooks.CSharpNotebookStudioViewModel? NotebookStudioViewModel { get; internal set; }
    public PdfEditorApp.Plugins.CSharpEditor.ViewModels.Server.FryServerStudioViewModel? ServerStudioViewModel { get; private set; }
    public AI.AiComposerViewModel AiComposer { get; }

    /// <param name="serviceProvider">Resolves the plugin settings store when <paramref name="settingsStore"/> isn't given.</param>
    /// <param name="settingsStore">The plugin's settings (execution timeout).</param>
    /// <param name="blindProgress">Where Blind 75 progress lives; the user's progress file when not given.</param>
    /// <param name="storageService">Where scripts and notebooks live; the user's library when not given.</param>
    public CSharpStudioHostViewModel(
        IServiceProvider? serviceProvider = null,
        ISettingsManager? settingsStore = null,
        IBlindProgressService? blindProgress = null,
        StudioLanguageServices? languages = null,
        IScriptStorageService? storageService = null,
        IActivityService? activities = null)
    {
        EnsureThreadPoolHeadroom();
        _activities = activities ?? new ActivityService();
        Activity = new ActivityPresenterViewModel(_activities);
        _languages = languages ?? StudioLanguageServices.Default;
        if (storageService == null)
        {
            // The user's own library: files changed outside the studio (git, another editor) should show up by themselves.
            var library = new LocalScriptStorageService(languages: _languages.Registry)
            {
                // Deleting from an Explorer moves things to the Trash, where they can be put back.
                RecycleBin = new PdfEditorApp.Plugins.CSharpEditor.Services.Workspace.RecycleBin()
            };
            library.StartWatchingForChanges();
            _storageService = library;
        }
        else
        {
            _storageService = storageService;
        }

        // Bridge to the ambient extensibility context
        PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.LanguageServices = _languages;
        RouteExtensionDialogsToStudio();
        PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.WorkspaceService.RootPathResolver = () => _storageService.ActiveWorkspaceRootPath;


        _blindProgress = blindProgress ?? new LocalBlindProgressService();
        // Prefer an explicitly-passed store (how the real plugin host wires it, via
        // IFryPluginContext.TryGetService inside CSharpEditorPlugin.ApplyAsync's ViewFactory), but
        // fall back to resolving it off the plain IServiceProvider — the standalone Runner already
        // supplies one this way via StandaloneServiceProvider/StandaloneSettingsStore.
        _settingsStore = settingsStore ?? serviceProvider?.GetService(typeof(ISettingsManager)) as ISettingsManager;

        // ── Initialize Documentation & Learning Center page ──
        DocsViewModel = new Docs.CSharpDocsViewModel(
            docService: DocumentationService.Instance,
            backToHubAction: NavigateToManager,
            openScriptAction: NavigateToCodeStudio,
            openNotebookAction: NavigateToNotebookStudio,
            storageService: _storageService,
            languages: _languages);

        // ── Initialize Blind 75 Algorithm Hub page ──
        BlindProblemsViewModel = new BlindProblems.CSharpBlindProblemsViewModel(
            progressService: _blindProgress,
            backToHubAction: NavigateToManager,
            openScriptAction: NavigateToCodeStudio,
            openNotebookAction: NavigateToNotebookStudio);

        // ── Initialize Settings & Environment Setup page ──
        SettingsViewModel = new Settings.CSharpSettingsViewModel(
            _languages,
            _languages.StudioSettings,
            backToHubAction: NavigateToManager,
            backToPreviousAction: NavigateToPreviousPage,
            openScriptAction: NavigateToCodeStudio,
            activities: _activities);

        // ── Show Manager immediately — it doesn't need the compiler ──
        ManagerViewModel = new Hub.CSharpManagerViewModel(
            _storageService,
            openScriptAction: NavigateToCodeStudio,
            openNotebookAction: NavigateToNotebookStudio,
            navigateToHomeAction: NavigateToHome,
            navigateToDocsAction: () => NavigateToDocs(),
            navigateToBlindProblemsAction: () => NavigateToBlindProblems(),
            languages: _languages,
            navigateToSettingsAction: cat => NavigateToSettings(cat),
            openServerAction: server => NavigateToServerStudio(server),
            activities: _activities);

        _currentPage = ManagerViewModel;
        _activeDocumentTitle = "Hub";

        AiComposer = new AI.AiComposerViewModel(
            settings: _languages.StudioSettings.GetSettings().Ai,
            settingsStore: _languages.StudioSettings);

        PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.ComposerVmResolver = () => AiComposer;
        PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.AiService.AttachViewModel(AiComposer);

        // ── Boot the Roslyn compiler service off the UI thread ──
        // ⚠️  DO NOT move RoslynCompilerService or child ViewModel construction back into this
        //     constructor body. See the post-mortem comment on InitializeCompilerAsync below.
        _engine = new EngineReadiness(_activities, InitializeCompilerAsync, WarmUpEngineAsync);
        _engine.Changed += () => UiDispatchHelper.RunOnUi(() =>
        {
            IsEngineLoading = _engine.State == EngineState.Starting;
            EngineStatus = _engine.StatusText;
            ManagerViewModel.RoslynEngineStatus = _engine.StatusText;
        });
        _engine.Start();
        ActiveInstance = this;
    }

    // Extensions and customization scripts ask through the SDK's dialog API; on screen they get the studio's own card instead
    // of a loose window that can end up behind the studio. Without a running UI (tests) nobody could answer, so the API keeps
    // its own fallback there.
    private void RouteExtensionDialogsToStudio()
    {
        if (!UiDispatchHelper.HasLiveUiLifetime) return;

        var api = PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.UiService.DialogsService;
        api.ConfirmHandler = async (message, title) =>
            await Dialogs.ShowChoiceAsync(new DialogRequest(title, message, [],
            [
                new DialogChoice(StudioQuestions.Confirm, "Yes", DialogChoiceKind.Primary),
                new DialogChoice(StudioQuestions.Cancel, "No", DialogChoiceKind.Cancel)
            ])) == StudioQuestions.Confirm;
        api.AlertHandler = (message, title) =>
            Dialogs.ShowChoiceAsync(new DialogRequest(title, message, [], [new DialogChoice(StudioQuestions.Confirm, "OK", DialogChoiceKind.Primary)]));
    }

    // Every open editor's TextMate tokenizer blocks a thread-pool thread while it lives (AvaloniaEdit.TextMate's design), and
    // so do the readers of running programs. With the pool's minimum at the core count, a notebook with a screenful of cells
    // left the studio's async work (opening, saving, git) queued behind the pool's slow growth. Give it room up front.
    private static void EnsureThreadPoolHeadroom()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        if (workers < 64) ThreadPool.SetMinThreads(64, io);
    }

    // The second stage: only makes the first run fast. Nobody waits for it.
    private static Task WarmUpEngineAsync()
    {
        NotebookExecutionKernel.Warmup();
        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // ⚠️  POST-MORTEM: UI FREEZE BUG — 2026-09-14  (DO NOT REPEAT)
    // ══════════════════════════════════════════════════════════════════════════════════════
    // SYMPTOM:  The Runner window opened but showed a completely blank/frozen white screen
    //           for ~14 seconds before any UI became visible.
    //
    // ROOT CAUSE: `new RoslynCompilerService()` calls `InitializeDefaultReferences()` which
    //             loads 15+ `MetadataReference` objects from disk via reflection. This was
    //             called synchronously in this constructor — ON THE UI THREAD.
    //             The UI thread was blocked for 14,124 ms. Avalonia cannot paint any frames
    //             while the UI thread is blocked, so the window appeared frozen/invisible.
    //
    // DIAGNOSIS: Added `Program.Log()` timing stamps around each constructor call.
    //            Log showed:  "VM created OK (14124ms)"  → blocked inside `new RoslynCompilerService()`.
    //
    // FIX:       1. ManagerViewModel is created synchronously (fast, no compiler needed).
    //            2. RoslynCompilerService + ScriptExecutionEngine + both child ViewModels
    //               are ALL created inside `Task.Run` on a background thread.
    //            3. Only the 4 lightweight property assignments are posted back to the UI
    //               thread via `Dispatcher.UIThread.Post` (fire-and-forget, non-blocking).
    //
    // RULE:      NEVER construct RoslynCompilerService (or any Roslyn/MSBuild/heavy-reflection
    //            object) on the UI thread. Always use Task.Run. This applies to any future
    //            ViewModel or service that loads assemblies, reads disk at startup, or calls
    //            into Roslyn APIs during construction.
    // ══════════════════════════════════════════════════════════════════════════════════════
    private async Task InitializeCompilerAsync()
    {
        CodeStudio.CSharpCodeStudioViewModel? codeVm = null;
        Notebooks.CSharpNotebookStudioViewModel? notebookVm = null;

        await Task.Run(() =>
        {
            // Only what the pages need to open documents. The first compilation (the slow part of a cold engine) is
            // the Warm stage, run afterwards; opening a script no longer waits for it.
            _compilerService = new RoslynCompilerService();
            _executionEngine = new ScriptExecutionEngine();

            var initialScript = new ScriptDocumentItem
            {
                Title = "1. Two Sum (Algorithm Workspace)",
                Code = CodeTemplateLibrary.GetTemplates()[0].InitialCode,
                Notes = CodeTemplateLibrary.GetTemplates()[0].Notes,
                IsEphemeral = true
            };

            codeVm = new CodeStudio.CSharpCodeStudioViewModel(
                initialScript,
                _storageService,
                _compilerService,
                _executionEngine,
                backToHubAction: NavigateToManager,
                backToHomeAction: NavigateToHome,
                getTimeoutSeconds: GetExecutionTimeoutSeconds,
                openNotebookAction: NavigateToNotebookStudio,
                navigateToDocsAction: () => NavigateToDocs(),
                blindProgress: _blindProgress,
                languages: _languages,
                navigateToSettingsAction: () => NavigateToSettings(),
                openServerAction: server => NavigateToServerStudio(server),
                activities: _activities,
                dialogs: Dialogs);

            var initialNotebook = new NotebookDocumentItem
            {
                Title = "Interactive C# Notebook",
                IsEphemeral = true
            };

            notebookVm = new Notebooks.CSharpNotebookStudioViewModel(
                initialNotebook,
                _storageService,
                _compilerService,
                _executionEngine,
                backToHubAction: NavigateToManager,
                backToHomeAction: NavigateToHome,
                getTimeoutSeconds: GetExecutionTimeoutSeconds,
                openScriptAction: NavigateToCodeStudio,
                navigateToDocsAction: () => NavigateToDocs(),
                languages: _languages,
                navigateToSettingsAction: () => NavigateToSettings(),
                openServerAction: server => NavigateToServerStudio(server),
                activities: _activities,
                dialogs: Dialogs);
        });

        void Publish()
        {
            CodeStudioViewModel = codeVm;
            NotebookStudioViewModel = notebookVm;
            if (codeVm != null)
            {
                codeVm.ToggleAiComposerAction = () => ToggleAiComposer();
            }
            AiComposer.InitializeServices(codeVm, _storageService, _compilerService, PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.CustomizationManager);
        }

        // On the UI thread in the studio (inline in tests), and finished before the Core stage counts as done: an open
        // that waited for it must find the pages.
        await UiDispatchHelper.InvokeAsync(Publish);
    }

    [RelayCommand]
    public void ToggleAiComposer()
    {
        AiComposer.ToggleFloating();
    }

    [RelayCommand]
    public void NavigateToHome()
    {
        RequestClose?.Invoke();

        try
        {
            var msgType = Type.GetType("PdfEditorApp.Messages.NavigateToHomeMessage, PdfEditorApp");
            if (msgType != null)
            {
                var instance = Activator.CreateInstance(msgType);
                var sendMethod = typeof(WeakReferenceMessenger).GetMethods()
                    .FirstOrDefault(m => m.Name == "Send" && m.IsGenericMethod && m.GetGenericArguments().Length == 1);
                sendMethod?.MakeGenericMethod(msgType).Invoke(WeakReferenceMessenger.Default, [instance]);
            }
        }
        catch
        {
        }
    }

    /// <summary>Opens a script in the Code Studio. Returns at once; a failure is shown to the user, never lost.</summary>
    public void NavigateToCodeStudio(ScriptDocumentItem script) =>
        OpenInCodeStudioAsync(script).FireAndForget(_activities, "Opening script");

    /// <summary>Opens a script in the Code Studio and completes when it is the page on screen (or was overtaken).</summary>
    public async Task OpenInCodeStudioAsync(ScriptDocumentItem script)
    {
        var title = string.IsNullOrWhiteSpace(script.Title) ? "Untitled Script" : script.Title;
        var token = _navigation.Begin();
        using var activity = _activities.Start(new ActivityOptions($"Opening {title}", ActivityLocation.Window));
        if (CodeStudioViewModel == null) await _engine.WhenCoreReadyAsync();
        if (CodeStudioViewModel == null || token.IsCancellationRequested) return;

        await CodeStudioViewModel.UpdateActiveScriptAsync(script);
        if (token.IsCancellationRequested) return;
        CurrentPage = CodeStudioViewModel;
        IsOnManagerPage = false;
        ActiveDocumentTitle = title;
    }

    /// <summary>Opens a notebook in the Notebook Studio. Returns at once; a failure is shown to the user, never lost.</summary>
    public void NavigateToNotebookStudio(NotebookDocumentItem notebook) =>
        OpenInNotebookStudioAsync(notebook).FireAndForget(_activities, "Opening notebook");

    /// <summary>Opens a notebook in the Notebook Studio and completes when it is the page on screen (or was overtaken).</summary>
    public async Task OpenInNotebookStudioAsync(NotebookDocumentItem notebook)
    {
        var title = string.IsNullOrWhiteSpace(notebook.Title) ? "Untitled Notebook" : notebook.Title;
        var token = _navigation.Begin();
        using var activity = _activities.Start(new ActivityOptions($"Opening {title}", ActivityLocation.Window));
        if (NotebookStudioViewModel == null) await _engine.WhenCoreReadyAsync();
        if (NotebookStudioViewModel == null || token.IsCancellationRequested) return;

        NotebookStudioViewModel.UpdateActiveNotebook(notebook);
        CurrentPage = NotebookStudioViewModel;
        IsOnManagerPage = false;
        ActiveDocumentTitle = title;
    }

    public void NavigateToServerStudio(FryServerDocumentItem server, string? filePath = null)
    {
        var title = string.IsNullOrWhiteSpace(server.Title) ? "API Server" : server.Title;
        _navigation.CancelCurrent();
        if (ServerStudioViewModel == null)
        {
            ServerStudioViewModel = new PdfEditorApp.Plugins.CSharpEditor.ViewModels.Server.FryServerStudioViewModel(
                document: server,
                filePath: filePath,
                portService: new PdfEditorApp.Plugins.CSharpEditor.Services.Server.PortAvailabilityService(),
                storageService: _storageService,
                backToHubAction: NavigateToManager,
                backToHomeAction: NavigateToHome,
                openScriptAction: NavigateToCodeStudio,
                openNotebookAction: NavigateToNotebookStudio,
                dialogs: Dialogs);
        }
        else
        {
            ServerStudioViewModel.LoadDocument(server, filePath);
        }

        CurrentPage = ServerStudioViewModel;
        IsOnManagerPage = false;
        ActiveDocumentTitle = title;
    }

    [RelayCommand]
    public void NavigateToManager()
    {
        _navigation.CancelCurrent();
        ManagerViewModel.ReloadIfStaleAsync().FireAndForget(_activities, "Refreshing the Hub");
        CurrentPage = ManagerViewModel;
        IsOnManagerPage = true;
        ActiveDocumentTitle = "Hub";
    }

    [RelayCommand]
    public void NavigateToDocs(string? articleId = null)
    {
        _navigation.CancelCurrent();
        if (!string.IsNullOrEmpty(articleId))
        {
            DocsViewModel.SelectTopic(articleId);
        }
        CurrentPage = DocsViewModel;
        IsOnManagerPage = false;
        ActiveDocumentTitle = "Documentation";
    }

    [RelayCommand]
    public void NavigateToBlindProblems(int? problemNumber = null)
    {
        _navigation.CancelCurrent();
        if (problemNumber.HasValue)
        {
            // The page's own row for it (it keeps its own copy of the catalog), so the table highlights that row.
            var problem = BlindProblemsViewModel.AllProblems.FirstOrDefault(p => p.Number == problemNumber.Value);
            if (problem != null)
            {
                BlindProblemsViewModel.SelectedProblem = problem;
            }
        }
        CurrentPage = BlindProblemsViewModel;
        IsOnManagerPage = false;
        ActiveDocumentTitle = "Blind 75";
    }

    private object? _previousPageBeforeSettings;

    [RelayCommand]
    public void NavigateToSettings(string? category = null)
    {
        _navigation.CancelCurrent();
        _previousPageBeforeSettings = CurrentPage;
        if (!string.IsNullOrEmpty(category))
        {
            SettingsViewModel.SelectCategory(category);
        }
        CurrentPage = SettingsViewModel;
        IsOnManagerPage = false;
        ActiveDocumentTitle = "Settings";
    }

    [RelayCommand]
    public void NavigateToPreviousPage()
    {
        if (_previousPageBeforeSettings != null)
        {
            CurrentPage = _previousPageBeforeSettings;
            IsOnManagerPage = ReferenceEquals(CurrentPage, ManagerViewModel);
            ActiveDocumentTitle = IsOnManagerPage ? "Hub" : (CurrentPage is Docs.CSharpDocsViewModel ? "Documentation" : (CurrentPage is BlindProblems.CSharpBlindProblemsViewModel ? "Blind 75" : (CurrentPage is Settings.CSharpSettingsViewModel ? "Settings" : (CurrentPage is PdfEditorApp.Plugins.CSharpEditor.ViewModels.Server.FryServerStudioViewModel ? "API Server" : "Editor"))));
        }
        else
        {
            NavigateToManager();
        }
    }

    [RelayCommand]
    public async Task OpenExistingProjectAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        await ManagerViewModel.OpenExistingProjectAsync(path);
    }

    /// <summary>
    /// Reads the user-configured "ExecutionTimeoutSeconds" plugin setting (see plugin.json), falling
    /// back to the documented default if no settings store was supplied (e.g. the standalone Runner) or
    /// the host hasn't registered one — this never throws and never blocks execution on a missing service.
    /// </summary>
    public int GetExecutionTimeoutSeconds() =>
        _settingsStore?.GetSetting(PluginId, "ExecutionTimeoutSeconds", DefaultExecutionTimeoutSeconds) ?? DefaultExecutionTimeoutSeconds;

    /// <summary>The titles of every open script and notebook with edits that aren't saved.</summary>
    public IReadOnlyList<string> UnsavedDocumentTitles =>
        [.. CodeStudioViewModel?.UnsavedDocumentTitles ?? [], .. NotebookStudioViewModel?.UnsavedDocumentTitles ?? []];

    /// <summary>
    /// Before the studio closes (the window, the app): asks once about every unsaved document — save them all, drop them, or
    /// stay. False when the studio must stay open (the user cancelled, or a save failed).
    /// </summary>
    public async Task<bool> ConfirmCloseStudioAsync()
    {
        var unsaved = UnsavedDocumentTitles;
        if (unsaved.Count == 0) return _closeConfirmed = true;

        var answer = await StudioQuestions.AskToSaveAsync(Dialogs, unsaved, closingStudio: true);
        if (answer is null or StudioQuestions.DontSave) return _closeConfirmed = true;
        if (answer != StudioQuestions.Save) return false;
        return _closeConfirmed = await SaveAllDocumentsAsync();
    }

    // The user answered the close question: what is still unsaved now is what they chose not to save.
    private bool _closeConfirmed;

    /// <summary>Saves every open script and notebook with unsaved edits. False when one of them couldn't be saved.</summary>
    public async Task<bool> SaveAllDocumentsAsync()
    {
        var saved = CodeStudioViewModel == null || await CodeStudioViewModel.SaveAllAsync();
        saved &= NotebookStudioViewModel == null || await NotebookStudioViewModel.SaveAllAsync();
        return saved;
    }

    // A host that tears the studio down without asking (FryPDF unloading the plugin or exiting: its SDK has no way to keep a
    // plugin open) would lose every unsaved edit. Saving them is the lesser surprise; the wait is bounded so exit never hangs.
    private void SaveUnsavedDocumentsBeforeTeardown()
    {
        if (_closeConfirmed || UnsavedDocumentTitles.Count == 0) return;
        try
        {
            var save = SaveAllDocumentsAsync();
            if (UiDispatchHelper.HasLiveUiLifetime && Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                // The saves continue on this thread; let the dispatcher run them for a moment.
                var frame = new Avalonia.Threading.DispatcherFrame();
                void Stop()
                {
                    frame.Continue = false;
                    Avalonia.Threading.Dispatcher.UIThread.Post(static () => { });
                }

                _ = save.ContinueWith(_ => Stop(), TaskScheduler.Default);
                using var timeout = new Timer(_ => Stop(), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
                if (!save.IsCompleted) Avalonia.Threading.Dispatcher.UIThread.PushFrame(frame);
            }
            else
            {
                save.Wait(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CSharpStudioHost] Couldn't save the open documents on close: {ex.Message}");
        }
    }

    /// <summary>Writes every pending preference change now (the studio or the window closing).</summary>
    public void FlushSettings()
    {
        try
        {
            SettingsViewModel.FlushAutoSave();
            _languages.StudioSettings.Flush();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CSharpStudioHost] Couldn't save the preferences on close: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (ActiveInstance == this) ActiveInstance = null;
        FlushSettings();
        SaveUnsavedDocumentsBeforeTeardown();
        DocsViewModel.Dispose();
        Activity.Dispose();
        (_storageService as IDisposable)?.Dispose();
    }
}
