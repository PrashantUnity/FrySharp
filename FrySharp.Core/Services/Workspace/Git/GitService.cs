using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEditorApp.Plugins.CSharpEditor.Models.Git;
using PdfEditorApp.Plugins.CSharpEditor.Services.Toolchains;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Workspace.Git;

public sealed class GitService : IGitService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(60);

    private readonly IHostEnvironment _host;

    public GitService(IHostEnvironment? host = null)
    {
        _host = host ?? new HostEnvironment();
    }

    public async Task<bool> IsGitInstalledAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await _host.RunAsync("git", ["--version"], TimeSpan.FromSeconds(3), ct);
            return res.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsGitRepositoryAsync(string workspaceRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !_host.DirectoryExists(workspaceRoot))
        {
            return false;
        }

        // Fast path check
        string dotGit = Path.Combine(workspaceRoot, ".git");
        if (_host.DirectoryExists(dotGit) || _host.FileExists(dotGit))
        {
            return true;
        }

        // CLI verification fallback (for worktrees or parent repos)
        var res = await RunGitAsync(workspaceRoot, ["rev-parse", "--is-inside-work-tree"], TimeSpan.FromSeconds(3), ct);
        return res.Success && res.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<GitRepositoryStatus> GetStatusAsync(string workspaceRoot, CancellationToken ct = default)
    {
        if (!await IsGitRepositoryAsync(workspaceRoot, ct))
        {
            return GitRepositoryStatus.NotARepository;
        }

        var res = await RunGitAsync(workspaceRoot, ["status", "--porcelain=v2", "--branch"], DefaultTimeout, ct);
        if (!res.Success)
        {
            // Fallback to porcelain v1 if v2 fails
            var fallbackRes = await RunGitAsync(workspaceRoot, ["status", "-s", "-b"], DefaultTimeout, ct);
            if (fallbackRes.Success)
            {
                return GitStatusParser.Parse(fallbackRes.Output);
            }

            return GitRepositoryStatus.NotARepository;
        }

        return GitStatusParser.Parse(res.Output);
    }

    public Task<GitCommandResult> StageFileAsync(string workspaceRoot, string relativePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return RunGitAsync(workspaceRoot, ["add", "--", relativePath], DefaultTimeout, ct);
    }

    public Task<GitCommandResult> StageAllAsync(string workspaceRoot, CancellationToken ct = default)
    {
        return RunGitAsync(workspaceRoot, ["add", "-A"], DefaultTimeout, ct);
    }

    public async Task<GitCommandResult> UnstageFileAsync(string workspaceRoot, string relativePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var res = await RunGitAsync(workspaceRoot, ["restore", "--staged", "--", relativePath], DefaultTimeout, ct);
        if (!res.Success)
        {
            // Fallback for older Git versions
            res = await RunGitAsync(workspaceRoot, ["reset", "HEAD", "--", relativePath], DefaultTimeout, ct);
        }
        return res;
    }

    public async Task<GitCommandResult> UnstageAllAsync(string workspaceRoot, CancellationToken ct = default)
    {
        var res = await RunGitAsync(workspaceRoot, ["restore", "--staged", "."], DefaultTimeout, ct);
        if (!res.Success)
        {
            res = await RunGitAsync(workspaceRoot, ["reset", "HEAD"], DefaultTimeout, ct);
        }
        return res;
    }

    public async Task<GitCommandResult> DiscardFileChangesAsync(string workspaceRoot, string relativePath, bool isUntracked = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (isUntracked)
        {
            // Clean untracked file or folder
            return await RunGitAsync(workspaceRoot, ["clean", "-f", "-d", "--", relativePath], DefaultTimeout, ct);
        }

        // Revert working tree changes to HEAD
        var res = await RunGitAsync(workspaceRoot, ["restore", "--worktree", "--", relativePath], DefaultTimeout, ct);
        if (!res.Success)
        {
            res = await RunGitAsync(workspaceRoot, ["checkout", "HEAD", "--", relativePath], DefaultTimeout, ct);
        }
        return res;
    }

    public async Task<GitCommandResult> DiscardAllChangesAsync(string workspaceRoot, bool includeUntracked = true, CancellationToken ct = default)
    {
        var res = await RunGitAsync(workspaceRoot, ["restore", "."], DefaultTimeout, ct);
        if (!res.Success)
        {
            res = await RunGitAsync(workspaceRoot, ["checkout", "."], DefaultTimeout, ct);
        }

        if (res.Success && includeUntracked)
        {
            // Untracked files are deleted for good: only when the caller asked the user about them separately.
            res = await RunGitAsync(workspaceRoot, ["clean", "-f", "-d"], DefaultTimeout, ct);
        }

        return res;
    }

    public Task<GitCommandResult> CommitAsync(string workspaceRoot, string message, bool stageAllIfNoneStaged = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var args = stageAllIfNoneStaged
            ? new[] { "commit", "-a", "-m", message }
            : new[] { "commit", "-m", message };

        return RunGitAsync(workspaceRoot, args, DefaultTimeout, ct);
    }

    public async Task<IReadOnlyList<GitBranchItem>> GetBranchesAsync(string workspaceRoot, CancellationToken ct = default)
    {
        var res = await RunGitAsync(workspaceRoot, ["branch", "-a", "--format=%(HEAD)|%(refname:short)|%(upstream:short)|%(refname)"], DefaultTimeout, ct);
        if (!res.Success || string.IsNullOrWhiteSpace(res.Output))
        {
            return Array.Empty<GitBranchItem>();
        }

        var branches = new List<GitBranchItem>();
        var lines = res.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            var parts = line.Split('|');
            if (parts.Length < 2) continue;

            bool isCurrent = parts[0].Trim() == "*";
            string name = parts[1].Trim();
            string? upstream = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : null;
            string fullRef = parts.Length > 3 ? parts[3].Trim() : string.Empty;
            // Any remote's branches, not only origin's; "origin/HEAD" only points at another branch.
            bool isRemote = fullRef.Length > 0
                ? fullRef.StartsWith("refs/remotes/", StringComparison.Ordinal)
                : name.StartsWith("origin/", StringComparison.OrdinalIgnoreCase);
            if (isRemote && fullRef.EndsWith("/HEAD", StringComparison.Ordinal)) continue;

            branches.Add(new GitBranchItem(name, isCurrent, isRemote, upstream));
        }

        return branches;
    }

    public Task<GitCommandResult> CheckoutBranchAsync(string workspaceRoot, string branchName, bool createNew = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        var args = createNew
            ? new[] { "checkout", "-b", branchName }
            : new[] { "checkout", branchName };

        return RunGitAsync(workspaceRoot, args, DefaultTimeout, ct);
    }

    public Task<GitCommandResult> PullAsync(string workspaceRoot, CancellationToken ct = default)
    {
        return RunGitAsync(workspaceRoot, ["pull"], NetworkTimeout, ct);
    }

    public Task<GitCommandResult> PushAsync(string workspaceRoot, CancellationToken ct = default)
    {
        return RunGitAsync(workspaceRoot, ["push"], NetworkTimeout, ct);
    }

    public async Task<GitCommandResult> SyncAsync(string workspaceRoot, CancellationToken ct = default)
    {
        var pullRes = await PullAsync(workspaceRoot, ct);
        if (!pullRes.Success) return pullRes;

        return await PushAsync(workspaceRoot, ct);
    }

    public Task<GitCommandResult> InitRepositoryAsync(string workspaceRoot, CancellationToken ct = default)
    {
        return RunGitAsync(workspaceRoot, ["init"], DefaultTimeout, ct);
    }

    public async Task<string?> GetFileHeadContentAsync(string workspaceRoot, string relativePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string normalized = relativePath.Replace('\\', '/');
        var res = await RunGitAsync(workspaceRoot, ["show", $"HEAD:{normalized}"], DefaultTimeout, ct);
        return res.Success ? res.Output : null;
    }

    public async Task<string?> GetFileDiffAsync(string workspaceRoot, string relativePath, bool staged = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var args = staged
            ? new[] { "diff", "--cached", "--", relativePath }
            : new[] { "diff", "--", relativePath };

        var res = await RunGitAsync(workspaceRoot, args, DefaultTimeout, ct);
        return res.Success ? res.Output : null;
    }

    private async Task<GitCommandResult> RunGitAsync(
        string workspaceRoot,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var fullArgs = new List<string>(args.Count + 5)
        {
            "-C",
            workspaceRoot,
            "--no-optional-locks",
            // Non-ASCII names as they are (UTF-8), not as octal escapes.
            "-c",
            "core.quotePath=false"
        };
        fullArgs.AddRange(args);

        var res = await _host.RunAsync("git", fullArgs, timeout, ct);

        string? error = null;
        if (res.ExitCode != 0)
        {
            error = !string.IsNullOrWhiteSpace(res.StandardError)
                ? res.StandardError.Trim()
                : res.StandardOutput.Trim();
        }

        return new GitCommandResult(
            Success: res.ExitCode == 0,
            Output: res.StandardOutput,
            ErrorMessage: error,
            ExitCode: res.ExitCode);
    }
}
