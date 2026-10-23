using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models.Git;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace.Git;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;

public partial class CSharpCodeStudioViewModel
{
    public ObservableCollection<GitBranchItem> AvailableBranches { get; } = new();

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
        if (branch == null || branch.IsCurrent || string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        var root = _storageService.ActiveWorkspaceRootPath;

        try
        {
            IsBranchBusy = true;
            GitStatusMessage = $"Checking out {branch.Name}...";
            var res = await GitService.CheckoutBranchAsync(root, branch.Name, createNew: false);
            if (res.Success)
            {
                CurrentGitBranch = branch.Name;
                IsBranchFlyoutOpen = false;
                BranchFilterText = string.Empty;
                await RefreshGitStatusAsync();
                await RefreshBranchesAsync();
                await RefreshExplorerIfStaleAsync();
                GitStatusMessage = $"Switched to branch '{branch.Name}'";
            }
            else
            {
                GitStatusMessage = $"Checkout failed: {res.ErrorMessage ?? res.Output}";
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
        var branchName = !string.IsNullOrWhiteSpace(NewBranchName) ? NewBranchName : BranchFilterText;
        if (string.IsNullOrWhiteSpace(branchName) || string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        var root = _storageService.ActiveWorkspaceRootPath;
        var targetName = branchName.Trim();

        try
        {
            IsBranchBusy = true;
            GitStatusMessage = $"Creating branch {targetName}...";
            var res = await GitService.CheckoutBranchAsync(root, targetName, createNew: true);
            if (res.Success)
            {
                CurrentGitBranch = targetName;
                NewBranchName = string.Empty;
                IsBranchFlyoutOpen = false;
                BranchFilterText = string.Empty;
                await RefreshGitStatusAsync();
                await RefreshBranchesAsync();
                await RefreshExplorerIfStaleAsync();
                GitStatusMessage = $"Created and switched to branch '{targetName}'";
            }
            else
            {
                GitStatusMessage = $"Create branch failed: {res.ErrorMessage ?? res.Output}";
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
