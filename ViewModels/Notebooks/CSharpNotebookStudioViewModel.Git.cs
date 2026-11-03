using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Git;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks;

// The Source Control view is the one Code Studio has too (GitPanelViewModel); here a changed file opens as itself.
public partial class CSharpNotebookStudioViewModel : IGitPanelHost
{
    private GitPanelViewModel? _git;

    /// <summary>The Source Control view: status, staging, commits, branches and the Explorer's badges.</summary>
    public GitPanelViewModel Git => _git ??= new GitPanelViewModel(_storageService, this, _dialogs);

    bool IGitPanelHost.IsPageActive => _isPageActive;

    Task IGitPanelHost.OpenChangedFileAsync(GitFileChangeItemViewModel item) => Git.OpenSourceFileAsync(item);

    Task IGitPanelHost.OnWorkingTreeChangedAsync() => RefreshExplorerIfStaleAsync();
}
