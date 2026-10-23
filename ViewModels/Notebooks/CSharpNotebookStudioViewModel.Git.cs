using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models.Git;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace.Git;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Git;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks;

public partial class CSharpNotebookStudioViewModel : IGitStudioHostViewModel
{
    private IGitService? _gitService;

    public IGitService GitService
    {
        get => _gitService ??= new GitService();
        set => _gitService = value;
    }

    [ObservableProperty]
    private bool _isGitInstalled = true;

    [ObservableProperty]
    private bool _isGitRepository = false;

    [ObservableProperty]
    private string _currentGitBranch = "main";

    [ObservableProperty]
    private string? _upstreamGitBranch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncStatusText), nameof(SyncButtonText), nameof(IsSyncButtonVisible))]
    private int _gitAheadCount = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncStatusText), nameof(SyncButtonText), nameof(IsSyncButtonVisible))]
    private int _gitBehindCount = 0;

    [ObservableProperty]
    private string _gitCommitMessage = string.Empty;

    [ObservableProperty]
    private bool _isGitBusy = false;

    [ObservableProperty]
    private string _gitBusyMessage = string.Empty;

    [ObservableProperty]
    private string _gitStatusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyGitChanges))]
    private int _uncommittedChangesCount = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StagedSectionChevronKind))]
    private bool _isStagedSectionExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkingSectionChevronKind))]
    private bool _isWorkingSectionExpanded = true;

    public string StagedSectionChevronKind => IsStagedSectionExpanded ? "ChevronDown" : "ChevronRight";
    public string WorkingSectionChevronKind => IsWorkingSectionExpanded ? "ChevronDown" : "ChevronRight";
    public string CommitPlaceholderText => OperatingSystem.IsMacOS() ? "Message (⌘Enter to commit)" : "Message (Ctrl+Enter to commit)";

    [ObservableProperty]
    private GitFileChangeItemViewModel? _selectedGitFile;

    public ObservableCollection<GitFileChangeItemViewModel> StagedChanges { get; } = new();
    public ObservableCollection<GitFileChangeItemViewModel> WorkingChanges { get; } = new();
    public ObservableCollection<GitBranchItem> GitBranches { get; } = new();
    public ObservableCollection<GitBranchItem> AvailableBranches { get; } = new();

    public bool HasStagedChanges => StagedChanges.Count > 0;
    public bool HasWorkingChanges => WorkingChanges.Count > 0;
    public bool HasAnyGitChanges => UncommittedChangesCount > 0;
    public bool IsSyncButtonVisible => IsGitRepository && (GitAheadCount > 0 || GitBehindCount > 0);

    public string SyncStatusText
    {
        get
        {
            if (GitAheadCount > 0 && GitBehindCount > 0) return $"{GitBehindCount}↓ {GitAheadCount}↑";
            if (GitAheadCount > 0) return $"{GitAheadCount}↑";
            if (GitBehindCount > 0) return $"{GitBehindCount}↓";
            return string.Empty;
        }
    }

    public string SyncButtonText
    {
        get
        {
            if (GitAheadCount > 0 && GitBehindCount > 0) return $"Sync Changes {GitBehindCount}↓ {GitAheadCount}↑";
            if (GitAheadCount > 0) return $"Sync Changes {GitAheadCount}↑";
            if (GitBehindCount > 0) return $"Sync Changes {GitBehindCount}↓";
            return "Sync Changes";
        }
    }

    // Branch Flyout
    [ObservableProperty]
    private bool _isBranchFlyoutOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredBranches))]
    private string _branchFilterText = string.Empty;

    [ObservableProperty]
    private string _newBranchName = string.Empty;

    [ObservableProperty]
    private bool _isBranchBusy;

    public IEnumerable<GitBranchItem> FilteredBranches
    {
        get
        {
            if (string.IsNullOrWhiteSpace(BranchFilterText)) return AvailableBranches;
            return AvailableBranches.Where(b => b.Name.Contains(BranchFilterText, StringComparison.OrdinalIgnoreCase));
        }
    }

    private string GetSourceControlSideBarTitle()
    {
        var wsRoot = _storageService?.ActiveWorkspaceRootPath;
        if (!string.IsNullOrWhiteSpace(wsRoot))
        {
            var folderName = Path.GetFileName(wsRoot.TrimEnd('/', '\\'));
            if (!string.IsNullOrWhiteSpace(folderName)) return $"SOURCE CONTROL: {folderName.ToUpperInvariant()}";
        }
        return "SOURCE CONTROL";
    }

    public void InitializeGitSupport()
    {
        if (_storageService != null)
        {
            _storageService.ActiveWorkspaceChanged += () => _ = RefreshGitStatusAsync();
            _storageService.ExternalChangeDetected += () => { if (_isPageActive) _ = RefreshGitStatusAsync(); };
        }
        _ = RefreshGitStatusAsync();
    }

    [RelayCommand]
    public async Task RefreshGitStatusAsync()
    {
        var wsRoot = _storageService?.ActiveWorkspaceRootPath;
        if (string.IsNullOrWhiteSpace(wsRoot)) return;

        IsGitBusy = true;
        GitBusyMessage = "Checking repository status...";
        try
        {
            IsGitInstalled = await GitService.IsGitInstalledAsync();
            if (!IsGitInstalled)
            {
                IsGitRepository = false;
                UncommittedChangesCount = 0;
                StagedChanges.Clear();
                WorkingChanges.Clear();
                ClearExplorerGitBadges();
                return;
            }

            IsGitRepository = await GitService.IsGitRepositoryAsync(wsRoot);
            if (!IsGitRepository)
            {
                UncommittedChangesCount = 0;
                StagedChanges.Clear();
                WorkingChanges.Clear();
                ClearExplorerGitBadges();
                return;
            }

            var status = await GitService.GetStatusAsync(wsRoot);
            CurrentGitBranch = string.IsNullOrWhiteSpace(status.CurrentBranch) ? "main" : status.CurrentBranch;
            UpstreamGitBranch = status.UpstreamBranch;
            GitAheadCount = status.AheadCount;
            GitBehindCount = status.BehindCount;

            Dispatcher.UIThread.Post(() =>
            {
                StagedChanges.Clear();
                foreach (var sc in status.StagedChanges) StagedChanges.Add(new GitFileChangeItemViewModel(sc));

                WorkingChanges.Clear();
                foreach (var wc in status.WorkingChanges) WorkingChanges.Add(new GitFileChangeItemViewModel(wc));

                UncommittedChangesCount = status.TotalUncommittedCount;
                UpdateExplorerGitBadges(status);

                OnPropertyChanged(nameof(HasStagedChanges));
                OnPropertyChanged(nameof(HasWorkingChanges));
                OnPropertyChanged(nameof(HasAnyGitChanges));
                OnPropertyChanged(nameof(SyncStatusText));
                OnPropertyChanged(nameof(SyncButtonText));
                OnPropertyChanged(nameof(IsSyncButtonVisible));
            });
        }
        catch (Exception ex)
        {
            GitStatusMessage = $"Status check error: {ex.Message}";
        }
        finally
        {
            IsGitBusy = false;
            GitBusyMessage = string.Empty;
        }
    }

    private void ClearExplorerGitBadges()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in ExplorerRows.Rows)
            {
                item.GitStatusLetter = null;
                item.GitStatusColorHex = null;
            }
        });
    }

    private void UpdateExplorerGitBadges(GitRepositoryStatus status)
    {
        var changeMap = new Dictionary<string, GitFileChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var sc in status.StagedChanges) changeMap[sc.RelativePath] = sc;
        foreach (var wc in status.WorkingChanges) changeMap[wc.RelativePath] = wc;

        var wsRoot = _storageService?.ActiveWorkspaceRootPath;
        if (string.IsNullOrWhiteSpace(wsRoot)) return;

        foreach (var item in ExplorerRows.Rows)
        {
            if (!item.IsDirectory && !string.IsNullOrWhiteSpace(item.FullPath))
            {
                var rel = Path.GetRelativePath(wsRoot, item.FullPath).Replace('\\', '/');
                if (changeMap.TryGetValue(rel, out var change))
                {
                    item.GitStatusLetter = change.StatusLetter;
                    item.GitStatusColorHex = change.StatusKind switch
                    {
                        GitFileStatusKind.Modified => "#F59E0B",
                        GitFileStatusKind.Added => "#22C55E",
                        GitFileStatusKind.Untracked => "#22C55E",
                        GitFileStatusKind.Renamed => "#06B6D4",
                        GitFileStatusKind.Deleted => "#EF4444",
                        GitFileStatusKind.Conflicted => "#A855F7",
                        _ => "#94A3B8"
                    };
                }
                else
                {
                    item.GitStatusLetter = null;
                    item.GitStatusColorHex = null;
                }
            }
        }
    }

    [RelayCommand]
    public async Task CommitAsync()
    {
        var wsRoot = _storageService?.ActiveWorkspaceRootPath;
        if (string.IsNullOrWhiteSpace(wsRoot) || string.IsNullOrWhiteSpace(GitCommitMessage)) return;

        var stageAll = StagedChanges.Count == 0 && WorkingChanges.Count > 0;
        IsGitBusy = true;
        GitBusyMessage = "Committing changes...";
        try
        {
            var res = await GitService.CommitAsync(wsRoot, GitCommitMessage.Trim(), stageAllIfNoneStaged: stageAll);
            if (res.Success)
            {
                GitCommitMessage = string.Empty;
                GitStatusMessage = "Committed successfully.";
            }
            else
            {
                GitStatusMessage = res.ErrorMessage ?? "Commit failed.";
            }
            await RefreshGitStatusAsync();
        }
        catch (Exception ex)
        {
            GitStatusMessage = $"Commit error: {ex.Message}";
        }
        finally
        {
            IsGitBusy = false;
            GitBusyMessage = string.Empty;
        }
    }

    [RelayCommand]
    public async Task SyncChangesAsync()
    {
        var wsRoot = _storageService?.ActiveWorkspaceRootPath;
        if (string.IsNullOrWhiteSpace(wsRoot)) return;

        IsGitBusy = true;
        GitBusyMessage = "Synchronizing with remote repository...";
        try
        {
            var res = await GitService.SyncAsync(wsRoot);
            GitStatusMessage = res.Success ? "Sync complete." : (res.ErrorMessage ?? "Sync failed.");
            await RefreshGitStatusAsync();
        }
        catch (Exception ex)
        {
            GitStatusMessage = $"Sync error: {ex.Message}";
        }
        finally
        {
            IsGitBusy = false;
            GitBusyMessage = string.Empty;
        }
    }

    [RelayCommand]
    public async Task StageFileAsync(GitFileChangeItemViewModel? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        await GitService.StageFileAsync(_storageService.ActiveWorkspaceRootPath, item.RelativePath);
        await RefreshGitStatusAsync();
    }

    [RelayCommand]
    public async Task StageAllAsync()
    {
        if (string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        await GitService.StageAllAsync(_storageService.ActiveWorkspaceRootPath);
        await RefreshGitStatusAsync();
    }

    [RelayCommand]
    public async Task UnstageFileAsync(GitFileChangeItemViewModel? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        await GitService.UnstageFileAsync(_storageService.ActiveWorkspaceRootPath, item.RelativePath);
        await RefreshGitStatusAsync();
    }

    [RelayCommand]
    public async Task UnstageAllAsync()
    {
        if (string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        await GitService.UnstageAllAsync(_storageService.ActiveWorkspaceRootPath);
        await RefreshGitStatusAsync();
    }

    [RelayCommand]
    public async Task DiscardFileChangesAsync(GitFileChangeItemViewModel? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        var isUntracked = item.StatusKind == GitFileStatusKind.Untracked;
        await GitService.DiscardFileChangesAsync(_storageService.ActiveWorkspaceRootPath, item.RelativePath, isUntracked);
        await RefreshGitStatusAsync();
        await RefreshExplorerIfStaleAsync();
    }

    [RelayCommand]
    public async Task DiscardAllChangesAsync()
    {
        if (string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        await GitService.DiscardAllChangesAsync(_storageService.ActiveWorkspaceRootPath);
        await RefreshGitStatusAsync();
        await RefreshExplorerIfStaleAsync();
    }

    [RelayCommand]
    public async Task InitGitRepositoryAsync()
    {
        var wsRoot = _storageService?.ActiveWorkspaceRootPath;
        if (string.IsNullOrWhiteSpace(wsRoot)) return;

        IsGitBusy = true;
        GitBusyMessage = "Initializing Git repository...";
        try
        {
            var res = await GitService.InitRepositoryAsync(wsRoot);
            GitStatusMessage = res.Success ? "Initialized empty Git repository." : (res.ErrorMessage ?? "Initialization failed.");
            await RefreshGitStatusAsync();
        }
        catch (Exception ex)
        {
            GitStatusMessage = $"Init error: {ex.Message}";
        }
        finally
        {
            IsGitBusy = false;
            GitBusyMessage = string.Empty;
        }
    }

    [RelayCommand]
    public async Task OpenChangedFileAsync(GitFileChangeItemViewModel? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        var fullPath = Path.Combine(_storageService.ActiveWorkspaceRootPath, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath)) await OpenWorkspaceFileAsync(fullPath);
    }

    [RelayCommand]
    public async Task OpenSourceFileAsync(GitFileChangeItemViewModel? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        var fullPath = Path.Combine(_storageService.ActiveWorkspaceRootPath, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath)) await OpenWorkspaceFileAsync(fullPath);
    }

    [RelayCommand]
    public void ToggleStagedSection() => IsStagedSectionExpanded = !IsStagedSectionExpanded;

    [RelayCommand]
    public void ToggleWorkingSection() => IsWorkingSectionExpanded = !IsWorkingSectionExpanded;

    [RelayCommand]
    public async Task RefreshBranchesAsync()
    {
        if (string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        var root = _storageService.ActiveWorkspaceRootPath;

        try
        {
            IsBranchBusy = true;
            var branches = await GitService.GetBranchesAsync(root);
            AvailableBranches.Clear();
            foreach (var b in branches)
            {
                AvailableBranches.Add(b);
                if (b.IsCurrent)
                {
                    CurrentGitBranch = b.Name;
                }
            }
            OnPropertyChanged(nameof(FilteredBranches));
        }
        catch (Exception ex)
        {
            GitStatusMessage = $"Branch error: {ex.Message}";
        }
        finally
        {
            IsBranchBusy = false;
        }
    }

    [RelayCommand]
    public async Task CheckoutBranchAsync(GitBranchItem? branch)
    {
        if (branch == null || string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        var root = _storageService.ActiveWorkspaceRootPath;

        try
        {
            IsBranchBusy = true;
            var res = await GitService.CheckoutBranchAsync(root, branch.Name, createNew: false);
            if (res.Success)
            {
                CurrentGitBranch = branch.Name;
                IsBranchFlyoutOpen = false;
                await RefreshBranchesAsync();
                await RefreshGitStatusAsync();
                await RefreshExplorerIfStaleAsync();
            }
            else
            {
                GitStatusMessage = res.ErrorMessage ?? "Checkout failed.";
            }
        }
        catch (Exception ex)
        {
            GitStatusMessage = $"Checkout error: {ex.Message}";
        }
        finally
        {
            IsBranchBusy = false;
        }
    }

    [RelayCommand]
    public async Task CreateAndCheckoutBranchAsync()
    {
        if (string.IsNullOrWhiteSpace(NewBranchName) || string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        var root = _storageService.ActiveWorkspaceRootPath;
        var targetName = NewBranchName.Trim();

        try
        {
            IsBranchBusy = true;
            var res = await GitService.CheckoutBranchAsync(root, targetName, createNew: true);
            if (res.Success)
            {
                CurrentGitBranch = targetName;
                NewBranchName = string.Empty;
                IsBranchFlyoutOpen = false;
                await RefreshBranchesAsync();
                await RefreshGitStatusAsync();
                await RefreshExplorerIfStaleAsync();
            }
            else
            {
                GitStatusMessage = res.ErrorMessage ?? "Branch creation failed.";
            }
        }
        catch (Exception ex)
        {
            GitStatusMessage = $"Create branch error: {ex.Message}";
        }
        finally
        {
            IsBranchBusy = false;
        }
    }

    [RelayCommand]
    public void GenerateSmartCommitMessage()
    {
        var allChanges = StagedChanges.Count > 0
            ? StagedChanges.Select(vm => vm.Model).ToList()
            : WorkingChanges.Select(vm => vm.Model).ToList();

        if (allChanges.Count == 0) return;

        var message = GitCommitMessageGenerator.Generate(allChanges);
        GitCommitMessage = message;
    }
}
