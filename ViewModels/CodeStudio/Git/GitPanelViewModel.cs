using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models.Git;
using PdfEditorApp.Plugins.CSharpEditor.Services.Activities;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace.Git;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Git;

/// <summary>What a studio gives its Source Control view: its Explorer (for the badges) and how it opens files.</summary>
public interface IGitPanelHost : IExplorerRowsHost
{

    /// <summary>True while the studio's page is on screen (a hidden page catches up when it is shown again).</summary>
    bool IsPageActive { get; }

    /// <summary>A changed file's row was clicked: its diff (Code Studio) or the file itself.</summary>
    Task OpenChangedFileAsync(GitFileChangeItemViewModel item);

    /// <summary>Opens a file of the workspace by its full path.</summary>
    Task OpenWorkspaceFileAsync(string fullPath);

    /// <summary>Git rewrote files of the working tree (a checkout, a discard): the Explorer and the open documents catch up.</summary>
    Task OnWorkingTreeChangedAsync();
}

/// <summary>
/// The Source Control view of a studio (the same one in Code Studio and Notebook Studio): the repository's status, staging,
/// commits, branches, and the Explorer's status badges. Git runs off the UI thread, refreshes never pile up (one runs, and one
/// more follows if something changed meanwhile), and nothing that loses work happens without asking.
/// </summary>
public sealed partial class GitPanelViewModel : ObservableObject, IGitStudioHostViewModel
{
    private readonly IScriptStorageService _storage;
    private readonly IGitPanelHost _host;
    private readonly IStudioDialogService _dialogs;
    private readonly Action<Action> _postToUiThread;
    private IGitService? _gitService;

    public GitPanelViewModel(IScriptStorageService storage, IGitPanelHost host, IStudioDialogService? dialogs = null, Action<Action>? postToUiThread = null)
    {
        _storage = storage;
        _host = host;
        _dialogs = dialogs ?? NullStudioDialogService.Instance;
        _postToUiThread = postToUiThread ?? UiDispatchHelper.RunOnUi;

        // Both arrive on a background thread: the refresh itself always starts on the UI thread.
        _storage.ActiveWorkspaceChanged += () => _postToUiThread(() => RefreshGitStatusAsync().FireAndForget(NullActivityService.Instance, "Checking the repository"));
        _storage.ExternalChangeDetected += () => _postToUiThread(() =>
        {
            if (_host.IsPageActive) RefreshGitStatusAsync().FireAndForget(NullActivityService.Instance, "Checking the repository");
        });

        _host.ExplorerRows.Rows.CollectionChanged += OnExplorerRowsChanged;
    }

    public IGitService GitService
    {
        get => _gitService ??= new GitService();
        set
        {
            _gitService = value;
            _gitInstalled = null;
        }
    }

    // ── State ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isGitInstalled = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSyncButtonVisible))] private bool _isGitRepository;
    [ObservableProperty] private string _currentGitBranch = "main";
    [ObservableProperty] private string? _upstreamGitBranch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncStatusText), nameof(SyncButtonText), nameof(IsSyncButtonVisible))]
    private int _gitAheadCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncStatusText), nameof(SyncButtonText), nameof(IsSyncButtonVisible))]
    private int _gitBehindCount;

    [ObservableProperty] private string _gitCommitMessage = string.Empty;
    [ObservableProperty] private bool _isGitBusy;
    [ObservableProperty] private string _gitBusyMessage = string.Empty;
    [ObservableProperty] private string _gitStatusMessage = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAnyGitChanges))] private int _uncommittedChangesCount;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StagedSectionChevronKind))] private bool _isStagedSectionExpanded = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WorkingSectionChevronKind))] private bool _isWorkingSectionExpanded = true;
    [ObservableProperty] private GitFileChangeItemViewModel? _selectedGitFile;

    public string StagedSectionChevronKind => IsStagedSectionExpanded ? "ChevronDown" : "ChevronRight";
    public string WorkingSectionChevronKind => IsWorkingSectionExpanded ? "ChevronDown" : "ChevronRight";
    public string CommitPlaceholderText => OperatingSystem.IsMacOS() ? "Message (⌘Enter to commit)" : "Message (Ctrl+Enter to commit)";

    public ObservableCollection<GitFileChangeItemViewModel> StagedChanges { get; } = new();
    public ObservableCollection<GitFileChangeItemViewModel> WorkingChanges { get; } = new();
    public ObservableCollection<GitBranchItem> AvailableBranches { get; } = new();

    public bool HasStagedChanges => StagedChanges.Count > 0;
    public bool HasWorkingChanges => WorkingChanges.Count > 0;
    public bool HasAnyGitChanges => UncommittedChangesCount > 0;
    public bool IsSyncButtonVisible => IsGitRepository && (GitAheadCount > 0 || GitBehindCount > 0);

    public string SyncStatusText =>
        GitAheadCount > 0 && GitBehindCount > 0 ? $"{GitBehindCount}↓ {GitAheadCount}↑" :
        GitAheadCount > 0 ? $"{GitAheadCount}↑" :
        GitBehindCount > 0 ? $"{GitBehindCount}↓" : string.Empty;

    public string SyncButtonText => SyncStatusText.Length == 0 ? "Sync Changes" : $"Sync Changes {SyncStatusText}";

    /// <summary>The side bar's title while it shows this view: "SOURCE CONTROL: FOLDER".</summary>
    public string SideBarTitle
    {
        get
        {
            var root = _storage.ActiveWorkspaceRootPath;
            var folderName = string.IsNullOrWhiteSpace(root) ? null : Path.GetFileName(root.TrimEnd('/', '\\'));
            return string.IsNullOrWhiteSpace(folderName) ? "SOURCE CONTROL" : $"SOURCE CONTROL: {folderName.ToUpperInvariant()}";
        }
    }

    // Overlapping operations (a commit that refreshes afterwards) keep the spinner up until the last one ends.
    private int _busyDepth;

    private void BeginBusy(string message)
    {
        _busyDepth++;
        IsGitBusy = true;
        GitBusyMessage = message;
    }

    private void EndBusy()
    {
        if (--_busyDepth > 0) return;
        _busyDepth = 0;
        IsGitBusy = false;
        GitBusyMessage = string.Empty;
    }

    private string? Root => string.IsNullOrWhiteSpace(_storage.ActiveWorkspaceRootPath) ? null : _storage.ActiveWorkspaceRootPath;

    // ── Status ─────────────────────────────────────────────────────────────────────────────────────────────────────

    // Whether git is installed doesn't change while the studio runs: asked once (it is a process start).
    private Task<bool>? _gitInstalled;
    private Task<bool> IsGitInstalledAsync() => _gitInstalled ??= GitService.IsGitInstalledAsync();

    private Task? _refreshRunning;
    private bool _refreshAgain;
    private long _refreshedAtVersion = -1;

    /// <summary>
    /// Reads the repository's status again. Called while one is running, it doesn't start a second: the running one is
    /// followed by exactly one more, which sees everything that changed meanwhile.
    /// </summary>
    [RelayCommand]
    public Task RefreshGitStatusAsync()
    {
        if (_refreshRunning is { IsCompleted: false })
        {
            _refreshAgain = true;
            return _refreshRunning;
        }

        return _refreshRunning = RefreshUntilSettledAsync();
    }

    private async Task RefreshUntilSettledAsync()
    {
        do
        {
            _refreshAgain = false;
            await RefreshOnceAsync();
        }
        while (_refreshAgain);
    }

    /// <summary>The studio's page was shown again: catch up only if the workspace changed since the last look (git runs a process).</summary>
    public void OnHostActivated()
    {
        if (_storage.ContentVersion == _refreshedAtVersion) return;
        RefreshGitStatusAsync().FireAndForget(NullActivityService.Instance, "Checking the repository");
    }

    private sealed record Snapshot(bool Installed, GitRepositoryStatus? Status);

    private async Task RefreshOnceAsync()
    {
        if (Root is not { } root) return;
        var version = _storage.ContentVersion;

        BeginBusy("Checking repository status...");
        try
        {
            // Starting a process (and its reader threads) costs milliseconds each: never on the UI thread.
            var snapshot = await Task.Run(async () =>
            {
                if (!await IsGitInstalledAsync().ConfigureAwait(false)) return new Snapshot(false, null);
                if (!await GitService.IsGitRepositoryAsync(root).ConfigureAwait(false)) return new Snapshot(true, null);
                return new Snapshot(true, await GitService.GetStatusAsync(root).ConfigureAwait(false));
            });

            _refreshedAtVersion = version;
            Apply(snapshot);
        }
        catch (Exception ex)
        {
            GitStatusMessage = $"Status check error: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    private void Apply(Snapshot snapshot)
    {
        IsGitInstalled = snapshot.Installed;
        var status = snapshot.Status;
        IsGitRepository = status is { IsRepository: true };
        if (status is not { IsRepository: true })
        {
            StagedChanges.Clear();
            WorkingChanges.Clear();
            UncommittedChangesCount = 0;
            GitAheadCount = 0;
            GitBehindCount = 0;
            _statusByPath = new Dictionary<string, GitFileChange>(StringComparer.OrdinalIgnoreCase);
            _changedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ApplyBadges(_host.ExplorerRows.Rows);
            NotifyListsChanged();
            return;
        }

        CurrentGitBranch = string.IsNullOrWhiteSpace(status.CurrentBranch) ? "main" : status.CurrentBranch;
        UpstreamGitBranch = status.UpstreamBranch;
        GitAheadCount = status.AheadCount;
        GitBehindCount = status.BehindCount;

        StagedChanges.Clear();
        foreach (var change in status.StagedChanges) StagedChanges.Add(new GitFileChangeItemViewModel(change));
        WorkingChanges.Clear();
        foreach (var change in status.WorkingChanges) WorkingChanges.Add(new GitFileChangeItemViewModel(change));
        UncommittedChangesCount = status.TotalUncommittedCount;

        RememberStatus(status);
        ApplyBadges(_host.ExplorerRows.Rows);
        NotifyListsChanged();
    }

    private void NotifyListsChanged()
    {
        OnPropertyChanged(nameof(HasStagedChanges));
        OnPropertyChanged(nameof(HasWorkingChanges));
        OnPropertyChanged(nameof(HasAnyGitChanges));
    }

    // ── Explorer badges ────────────────────────────────────────────────────────────────────────────────────────────

    // The last status, by workspace-relative path, and the folders that hold a change: rows drawn later (a folder opened,
    // the Explorer refreshed) get their badge from it without asking git again.
    private Dictionary<string, GitFileChange> _statusByPath = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _changedFolders = new(StringComparer.OrdinalIgnoreCase);

    private void RememberStatus(GitRepositoryStatus status)
    {
        var byPath = new Dictionary<string, GitFileChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in status.StagedChanges) byPath[Normalize(change.RelativePath)] = change;
        foreach (var change in status.WorkingChanges) byPath[Normalize(change.RelativePath)] = change;

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in byPath.Keys)
        {
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
            {
                if (!folders.Add(path[..slash])) break;
            }
        }

        _statusByPath = byPath;
        _changedFolders = folders;
    }

    private static string Normalize(string relativePath) => relativePath.Replace('\\', '/').Trim('/');

    // An Explorer row's path from the workspace root: rows hold it relative already; a row of a lazily listed folder may
    // hold it in full.
    private string? RowPath(ExplorerItemViewModel row)
    {
        if (string.IsNullOrWhiteSpace(row.FullPath)) return null;
        if (!Path.IsPathRooted(row.FullPath)) return Normalize(row.FullPath);
        return Root is { } root ? Normalize(Path.GetRelativePath(root, row.FullPath)) : null;
    }

    private void OnExplorerRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_statusByPath.Count == 0 && _changedFolders.Count == 0) return;
        if (e.Action == NotifyCollectionChangedAction.Reset) ApplyBadges(_host.ExplorerRows.Rows);
        else if (e.NewItems != null) ApplyBadges(e.NewItems.OfType<ExplorerItemViewModel>());
    }

    private void ApplyBadges(IEnumerable<ExplorerItemViewModel> rows)
    {
        foreach (var row in rows)
        {
            if (row.IsPlaceholder || RowPath(row) is not { } path) continue;

            if (row.IsDirectory)
            {
                var hasChanges = _changedFolders.Contains(path);
                row.GitStatusLetter = hasChanges ? "•" : null;
                row.GitStatusColorHex = hasChanges ? "#F59E0B" : null;
            }
            else if (_statusByPath.TryGetValue(path, out var change))
            {
                row.GitStatusLetter = change.StatusLetter;
                row.GitStatusColorHex = change.StatusKind switch
                {
                    GitFileStatusKind.Modified => "#F59E0B",
                    GitFileStatusKind.Added or GitFileStatusKind.Untracked => "#22C55E",
                    GitFileStatusKind.Deleted => "#EF4444",
                    GitFileStatusKind.Renamed => "#06B6D4",
                    GitFileStatusKind.Conflicted => "#A855F7",
                    _ => "#94A3B8"
                };
            }
            else
            {
                row.GitStatusLetter = null;
                row.GitStatusColorHex = null;
            }
        }
    }

    // ── Commands ───────────────────────────────────────────────────────────────────────────────────────────────────

    // Runs one git command with the spinner up; a failure is shown in the panel instead of vanishing.
    private async Task<bool> RunAsync(string busyMessage, Func<string, Task<GitCommandResult>> command, string failedMessage)
    {
        if (Root is not { } root) return false;
        BeginBusy(busyMessage);
        try
        {
            var result = await Task.Run(() => command(root));
            if (!result.Success) GitStatusMessage = $"{failedMessage}: {result.ErrorMessage ?? result.Output}".TrimEnd(' ', ':');
            return result.Success;
        }
        catch (Exception ex)
        {
            GitStatusMessage = $"{failedMessage}: {ex.Message}";
            return false;
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand]
    public async Task CommitAsync()
    {
        if (Root == null || string.IsNullOrWhiteSpace(GitCommitMessage)) return;
        var message = GitCommitMessage.Trim();

        // Nothing staged and only new files: `commit -a` would leave them out and fail. Stage everything, as VS Code offers.
        var onlyUntracked = !HasStagedChanges && WorkingChanges.Count > 0 && WorkingChanges.All(c => c.StatusKind == GitFileStatusKind.Untracked);
        if (onlyUntracked)
        {
            if (!await StudioQuestions.ConfirmAsync(_dialogs, "Stage all changes and commit?",
                    "There are no staged changes, only new files. Stage them all and commit?", "Stage All & Commit", destructive: false))
            {
                return;
            }

            if (!await RunAsync("Staging changes...", root => GitService.StageAllAsync(root), "Stage failed")) return;
        }

        var committed = await RunAsync("Committing changes...",
            root => GitService.CommitAsync(root, message, stageAllIfNoneStaged: !onlyUntracked && !HasStagedChanges),
            "Commit failed");
        if (committed)
        {
            GitCommitMessage = string.Empty;
            GitStatusMessage = "Commit successful.";
        }

        await RefreshGitStatusAsync();
    }

    [RelayCommand]
    public async Task SyncChangesAsync()
    {
        if (await RunAsync("Synchronizing with remote repository...", root => GitService.SyncAsync(root), "Sync failed"))
        {
            GitStatusMessage = "Sync complete.";
        }

        await RefreshGitStatusAsync();
        await _host.OnWorkingTreeChangedAsync();
    }

    [RelayCommand]
    public async Task StageFileAsync(GitFileChangeItemViewModel? item)
    {
        if (item == null) return;
        await RunAsync("Staging...", root => GitService.StageFileAsync(root, item.RelativePath), $"Couldn't stage {item.FileName}");
        await RefreshGitStatusAsync();
    }

    [RelayCommand]
    public async Task StageAllAsync()
    {
        await RunAsync("Staging all changes...", root => GitService.StageAllAsync(root), "Stage failed");
        await RefreshGitStatusAsync();
    }

    [RelayCommand]
    public async Task UnstageFileAsync(GitFileChangeItemViewModel? item)
    {
        if (item == null) return;
        await RunAsync("Unstaging...", root => GitService.UnstageFileAsync(root, item.RelativePath), $"Couldn't unstage {item.FileName}");
        await RefreshGitStatusAsync();
    }

    [RelayCommand]
    public async Task UnstageAllAsync()
    {
        await RunAsync("Unstaging all changes...", root => GitService.UnstageAllAsync(root), "Unstage failed");
        await RefreshGitStatusAsync();
    }

    /// <summary>Throws away one file's changes; an untracked file is deleted. Asks first: neither can be undone.</summary>
    [RelayCommand]
    public async Task DiscardFileChangesAsync(GitFileChangeItemViewModel? item)
    {
        if (item == null || Root == null) return;
        var untracked = item.StatusKind == GitFileStatusKind.Untracked;
        var confirmed = untracked
            ? await StudioQuestions.ConfirmAsync(_dialogs, $"Delete {item.FileName}?",
                $"{item.FileName} isn't tracked by Git, so discarding it deletes the file. This can't be undone.", "Delete File")
            : await StudioQuestions.ConfirmAsync(_dialogs, $"Discard changes to {item.FileName}?",
                "Its changes since the last commit will be lost. This can't be undone.", "Discard Changes");
        if (!confirmed) return;

        await RunAsync("Discarding changes...", root => GitService.DiscardFileChangesAsync(root, item.RelativePath, untracked), $"Couldn't discard {item.FileName}");
        await RefreshGitStatusAsync();
        await _host.OnWorkingTreeChangedAsync();
    }

    /// <summary>
    /// Throws away every change since the last commit. Asks first, and asks separately before deleting the files git doesn't
    /// track: they were never committed, so nothing could bring them back.
    /// </summary>
    [RelayCommand]
    public async Task DiscardAllChangesAsync()
    {
        if (Root == null) return;
        var untracked = WorkingChanges.Where(c => c.StatusKind == GitFileStatusKind.Untracked).Select(c => c.RelativePath).ToList();
        var tracked = StagedChanges.Concat(WorkingChanges)
            .Where(c => c.StatusKind != GitFileStatusKind.Untracked).Select(c => c.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tracked.Count == 0 && untracked.Count == 0) return;

        if (tracked.Count > 0 && !await StudioQuestions.ConfirmAsync(_dialogs,
                tracked.Count == 1 ? "Discard changes to 1 file?" : $"Discard changes to {tracked.Count} files?",
                "Their changes since the last commit will be lost. This can't be undone.", "Discard All Changes", tracked))
        {
            return;
        }

        var deleteUntracked = untracked.Count > 0 && await StudioQuestions.ConfirmAsync(_dialogs,
            untracked.Count == 1 ? "Also delete 1 untracked file?" : $"Also delete {untracked.Count} untracked files?",
            "These files aren't tracked by Git: deleting them is permanent. Choose Cancel to keep them.", "Delete Untracked Files", untracked);

        if (tracked.Count > 0 || deleteUntracked)
        {
            await RunAsync("Discarding changes...", root => GitService.DiscardAllChangesAsync(root, deleteUntracked), "Discard failed");
        }

        await RefreshGitStatusAsync();
        await _host.OnWorkingTreeChangedAsync();
    }

    [RelayCommand]
    public async Task InitGitRepositoryAsync()
    {
        if (await RunAsync("Initializing Git repository...", root => GitService.InitRepositoryAsync(root), "Initialization failed"))
        {
            GitStatusMessage = "Initialized empty Git repository.";
        }

        await RefreshGitStatusAsync();
    }

    [RelayCommand]
    public async Task OpenChangedFileAsync(GitFileChangeItemViewModel? item)
    {
        if (item != null && Root != null) await _host.OpenChangedFileAsync(item);
    }

    [RelayCommand]
    public async Task OpenSourceFileAsync(GitFileChangeItemViewModel? item)
    {
        if (item == null || Root is not { } root) return;
        var fullPath = Path.Combine(root, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath)) await _host.OpenWorkspaceFileAsync(fullPath);
    }

    [RelayCommand]
    public void ToggleStagedSection() => IsStagedSectionExpanded = !IsStagedSectionExpanded;

    [RelayCommand]
    public void ToggleWorkingSection() => IsWorkingSectionExpanded = !IsWorkingSectionExpanded;

    [RelayCommand]
    public void GenerateSmartCommitMessage()
    {
        var changes = (StagedChanges.Count > 0 ? StagedChanges : WorkingChanges).Select(vm => vm.Model).ToList();
        if (changes.Count > 0) GitCommitMessage = GitCommitMessageGenerator.Generate(changes);
    }

    // ── Branches ───────────────────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isBranchFlyoutOpen;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FilteredBranches))] private string _branchFilterText = string.Empty;
    [ObservableProperty] private string _newBranchName = string.Empty;
    [ObservableProperty] private bool _isBranchBusy;

    public IEnumerable<GitBranchItem> FilteredBranches =>
        string.IsNullOrWhiteSpace(BranchFilterText)
            ? AvailableBranches
            : AvailableBranches.Where(b => b.Name.Contains(BranchFilterText, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    public async Task RefreshBranchesAsync()
    {
        if (Root is not { } root) return;
        try
        {
            IsBranchBusy = true;
            var branches = await Task.Run(() => GitService.GetBranchesAsync(root));
            AvailableBranches.Clear();
            foreach (var branch in branches)
            {
                AvailableBranches.Add(branch);
                if (branch.IsCurrent) CurrentGitBranch = branch.Name;
            }

            OnPropertyChanged(nameof(FilteredBranches));
        }
        catch (Exception ex)
        {
            GitStatusMessage = $"Branch refresh error: {ex.Message}";
        }
        finally
        {
            IsBranchBusy = false;
        }
    }

    [RelayCommand]
    public async Task OpenBranchPickerAsync()
    {
        IsBranchFlyoutOpen = true;
        await RefreshBranchesAsync();
    }

    [RelayCommand]
    public void CloseBranchPicker()
    {
        IsBranchFlyoutOpen = false;
        BranchFilterText = string.Empty;
        NewBranchName = string.Empty;
    }

    [RelayCommand]
    public async Task CheckoutBranchAsync(GitBranchItem? branch)
    {
        if (branch == null || branch.IsCurrent || Root == null) return;
        await SwitchBranchAsync(branch.Name, createNew: false);
    }

    [RelayCommand]
    public async Task CreateAndCheckoutBranchAsync()
    {
        var name = !string.IsNullOrWhiteSpace(NewBranchName) ? NewBranchName : BranchFilterText;
        if (string.IsNullOrWhiteSpace(name) || Root == null) return;
        await SwitchBranchAsync(name.Trim(), createNew: true);
    }

    private async Task SwitchBranchAsync(string name, bool createNew)
    {
        try
        {
            IsBranchBusy = true;
            GitStatusMessage = createNew ? $"Creating branch {name}..." : $"Checking out {name}...";
            if (!await RunAsync(GitStatusMessage, root => GitService.CheckoutBranchAsync(root, name, createNew), createNew ? "Create branch failed" : "Checkout failed"))
            {
                return;
            }

            CurrentGitBranch = name;
            NewBranchName = string.Empty;
            BranchFilterText = string.Empty;
            IsBranchFlyoutOpen = false;
            await RefreshGitStatusAsync();
            await RefreshBranchesAsync();
            await _host.OnWorkingTreeChangedAsync();
            GitStatusMessage = createNew ? $"Created and switched to branch '{name}'" : $"Switched to branch '{name}'";
        }
        finally
        {
            IsBranchBusy = false;
        }
    }
}
