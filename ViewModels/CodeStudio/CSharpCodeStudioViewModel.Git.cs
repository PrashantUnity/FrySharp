using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Git;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;

// The Source Control view is the one Notebook Studio has too (GitPanelViewModel); this studio opens a changed file as its diff.
public partial class CSharpCodeStudioViewModel : IGitPanelHost
{
    private GitPanelViewModel? _git;

    /// <summary>The Source Control view: status, staging, commits, branches and the Explorer's badges.</summary>
    public GitPanelViewModel Git => _git ??= new GitPanelViewModel(_storageService, this, _dialogs, _postToUiThread);

    bool IGitPanelHost.IsPageActive => _isPageActive;

    Task IGitPanelHost.OpenChangedFileAsync(GitFileChangeItemViewModel item) => OpenDiffTabAsync(item);

    async Task IGitPanelHost.OnWorkingTreeChangedAsync()
    {
        await RefreshExplorerIfStaleAsync();
        await ReloadChangedDocumentsAsync(null);
    }
}
