using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.Git;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace.Git;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Git;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;

public partial class CSharpCodeStudioViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTextEditor))]
    private bool _showDiffViewer;

    [ObservableProperty]
    private GitDiffDocument? _activeDiffDocument;

    [ObservableProperty]
    private bool _isSideBySideDiff = true;

    public void UpdateDiffStateForTab(StudioTabItemViewModel tab)
    {
        if (tab.IsDiffTab && tab.DiffDocument != null)
        {
            ActiveDiffDocument = tab.DiffDocument;
            ShowDiffViewer = true;
        }
        else
        {
            ActiveDiffDocument = null;
            ShowDiffViewer = false;
        }

        OnPropertyChanged(nameof(ShowTextEditor));
    }

    public async Task OpenDiffTabAsync(GitFileChangeItemViewModel changeItem)
    {
        if (changeItem == null || string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        var root = _storageService.ActiveWorkspaceRootPath;
        var fullPath = Path.Combine(root, changeItem.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            // 1. Fetch git diff and file contents
            var diffOutput = await GitService.GetFileDiffAsync(root, changeItem.RelativePath, changeItem.IsStaged);
            var headContent = await GitService.GetFileHeadContentAsync(root, changeItem.RelativePath);

            string? workingContent = null;
            if (File.Exists(fullPath))
            {
                workingContent = await File.ReadAllTextAsync(fullPath);
            }

            // 2. Parse diff
            var diffDoc = GitDiffParser.Parse(
                changeItem.RelativePath,
                changeItem.IsStaged,
                diffOutput,
                headContent,
                workingContent);

            // 3. Check if diff tab is already open
            string tabId = $"git-diff:{changeItem.RelativePath}:{(changeItem.IsStaged ? "staged" : "working")}";
            var existingTab = OpenTabs.FirstOrDefault(t => t.Id == tabId);
            if (existingTab != null)
            {
                existingTab.DiffDocument = diffDoc;
                await SwitchToTabAsync(existingTab);
                return;
            }

            // 4. Create new diff tab
            string kindLabel = changeItem.IsStaged ? "Index" : "Working Tree";
            var syntheticDoc = new ScriptDocumentItem
            {
                Id = tabId,
                Title = $"{changeItem.FileName} ({kindLabel})",
                Description = $"Git Diff: {changeItem.RelativePath}",
                SourceFilePath = fullPath,
                IsEphemeral = true,
                IsReadOnly = true
            };

            var diffTab = CreateTab(syntheticDoc, isActive: true);
            diffTab.IsDiffTab = true;
            diffTab.DiffDocument = diffDoc;

            OpenTabs.Add(diffTab);
            await SwitchToTabAsync(diffTab);
        }
        catch (Exception ex)
        {
            GitStatusMessage = $"Diff error: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ToggleDiffLayout()
    {
        IsSideBySideDiff = !IsSideBySideDiff;
    }

    [RelayCommand]
    public async Task StageActiveDiffAsync()
    {
        if (ActiveDiffDocument == null || string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        var root = _storageService.ActiveWorkspaceRootPath;
        await GitService.StageFileAsync(root, ActiveDiffDocument.RelativePath);
        await RefreshGitStatusAsync();
        await CloseActiveDiffTabAsync();
    }

    [RelayCommand]
    public async Task UnstageActiveDiffAsync()
    {
        if (ActiveDiffDocument == null || string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        var root = _storageService.ActiveWorkspaceRootPath;
        await GitService.UnstageFileAsync(root, ActiveDiffDocument.RelativePath);
        await RefreshGitStatusAsync();
        await CloseActiveDiffTabAsync();
    }

    [RelayCommand]
    public async Task DiscardActiveDiffAsync()
    {
        if (ActiveDiffDocument == null || string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        var root = _storageService.ActiveWorkspaceRootPath;
        await GitService.DiscardFileChangesAsync(root, ActiveDiffDocument.RelativePath, isUntracked: false);
        await RefreshGitStatusAsync();
        await CloseActiveDiffTabAsync();
    }

    [RelayCommand]
    public async Task OpenActiveDiffSourceFileAsync()
    {
        if (ActiveDiffDocument == null || string.IsNullOrWhiteSpace(_storageService?.ActiveWorkspaceRootPath)) return;
        var root = _storageService.ActiveWorkspaceRootPath;
        var fullPath = Path.Combine(root, ActiveDiffDocument.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath))
        {
            await OpenWorkspaceFileAsync(fullPath);
        }
    }

    [RelayCommand]
    public async Task CloseActiveDiffTabAsync()
    {
        var activeTab = OpenTabs.FirstOrDefault(t => t.IsActive && t.IsDiffTab);
        if (activeTab != null)
        {
            await CloseTabAsync(activeTab);
        }
    }
}
