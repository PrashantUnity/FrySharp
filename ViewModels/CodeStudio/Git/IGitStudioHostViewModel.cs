using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models.Git;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Git;

/// <summary>
/// Defines the common Git host capabilities required by <c>StudioSourceControlPanelControl</c>
/// and <c>StudioBranchPickerControl</c>, enabling seamless reuse across Code Studio and Notebook Studio.
/// </summary>
public interface IGitStudioHostViewModel
{
    bool IsGitInstalled { get; }
    bool IsGitRepository { get; }
    bool IsGitBusy { get; }
    string GitBusyMessage { get; }
    string GitStatusMessage { get; }
    string CurrentGitBranch { get; }
    string? UpstreamGitBranch { get; }
    int GitAheadCount { get; }
    int GitBehindCount { get; }
    string GitCommitMessage { get; set; }
    string CommitPlaceholderText { get; }
    int UncommittedChangesCount { get; }
    bool HasAnyGitChanges { get; }
    bool HasStagedChanges { get; }
    bool HasWorkingChanges { get; }
    bool IsSyncButtonVisible { get; }
    string SyncStatusText { get; }
    string SyncButtonText { get; }
    bool IsStagedSectionExpanded { get; set; }
    bool IsWorkingSectionExpanded { get; set; }
    string StagedSectionChevronKind { get; }
    string WorkingSectionChevronKind { get; }
    GitFileChangeItemViewModel? SelectedGitFile { get; set; }

    ObservableCollection<GitFileChangeItemViewModel> StagedChanges { get; }
    ObservableCollection<GitFileChangeItemViewModel> WorkingChanges { get; }
    ObservableCollection<GitBranchItem> AvailableBranches { get; }
    IEnumerable<GitBranchItem> FilteredBranches { get; }

    bool IsBranchFlyoutOpen { get; set; }
    string BranchFilterText { get; set; }
    string NewBranchName { get; set; }
    bool IsBranchBusy { get; }

    IAsyncRelayCommand RefreshGitStatusCommand { get; }
    IAsyncRelayCommand InitGitRepositoryCommand { get; }
    IAsyncRelayCommand CommitCommand { get; }
    IAsyncRelayCommand SyncChangesCommand { get; }
    IAsyncRelayCommand StageAllCommand { get; }
    IAsyncRelayCommand UnstageAllCommand { get; }
    IAsyncRelayCommand DiscardAllChangesCommand { get; }
    IAsyncRelayCommand<GitFileChangeItemViewModel?> StageFileCommand { get; }
    IAsyncRelayCommand<GitFileChangeItemViewModel?> UnstageFileCommand { get; }
    IAsyncRelayCommand<GitFileChangeItemViewModel?> DiscardFileChangesCommand { get; }
    IAsyncRelayCommand<GitFileChangeItemViewModel?> OpenChangedFileCommand { get; }
    IAsyncRelayCommand<GitFileChangeItemViewModel?> OpenSourceFileCommand { get; }
    IRelayCommand ToggleStagedSectionCommand { get; }
    IRelayCommand ToggleWorkingSectionCommand { get; }
    IAsyncRelayCommand RefreshBranchesCommand { get; }
    IAsyncRelayCommand<GitBranchItem> CheckoutBranchCommand { get; }
    IAsyncRelayCommand CreateAndCheckoutBranchCommand { get; }
    IRelayCommand GenerateSmartCommitMessageCommand { get; }
}
