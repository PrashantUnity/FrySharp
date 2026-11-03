using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PdfEditorApp.Plugins.CSharpEditor.Models.Git;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Workspace.Git;

public interface IGitService
{
    Task<bool> IsGitInstalledAsync(CancellationToken ct = default);
    Task<bool> IsGitRepositoryAsync(string workspaceRoot, CancellationToken ct = default);
    Task<GitRepositoryStatus> GetStatusAsync(string workspaceRoot, CancellationToken ct = default);

    Task<GitCommandResult> StageFileAsync(string workspaceRoot, string relativePath, CancellationToken ct = default);
    Task<GitCommandResult> StageAllAsync(string workspaceRoot, CancellationToken ct = default);
    Task<GitCommandResult> UnstageFileAsync(string workspaceRoot, string relativePath, CancellationToken ct = default);
    Task<GitCommandResult> UnstageAllAsync(string workspaceRoot, CancellationToken ct = default);

    Task<GitCommandResult> DiscardFileChangesAsync(string workspaceRoot, string relativePath, bool isUntracked = false, CancellationToken ct = default);
    /// <param name="includeUntracked">Also delete the files git doesn't track (<c>git clean</c>): for good, so only after asking about them.</param>
    Task<GitCommandResult> DiscardAllChangesAsync(string workspaceRoot, bool includeUntracked = true, CancellationToken ct = default);

    Task<GitCommandResult> CommitAsync(string workspaceRoot, string message, bool stageAllIfNoneStaged = false, CancellationToken ct = default);

    Task<IReadOnlyList<GitBranchItem>> GetBranchesAsync(string workspaceRoot, CancellationToken ct = default);
    Task<GitCommandResult> CheckoutBranchAsync(string workspaceRoot, string branchName, bool createNew = false, CancellationToken ct = default);

    Task<GitCommandResult> PullAsync(string workspaceRoot, CancellationToken ct = default);
    Task<GitCommandResult> PushAsync(string workspaceRoot, CancellationToken ct = default);
    Task<GitCommandResult> SyncAsync(string workspaceRoot, CancellationToken ct = default);

    Task<GitCommandResult> InitRepositoryAsync(string workspaceRoot, CancellationToken ct = default);

    Task<string?> GetFileHeadContentAsync(string workspaceRoot, string relativePath, CancellationToken ct = default);
    Task<string?> GetFileDiffAsync(string workspaceRoot, string relativePath, bool staged = false, CancellationToken ct = default);
}
