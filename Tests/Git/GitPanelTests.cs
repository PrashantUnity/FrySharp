using CSharpEditorPlugin.Tests.TestSupport;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.Git;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace.Git;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;
using Xunit;

namespace CSharpEditorPlugin.Tests.Git;

/// <summary>The shared Source Control view: badges, refreshes that never pile up, and discards that ask first.</summary>
public class GitPanelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FrySharp_GitPanel_" + Guid.NewGuid().ToString("N"));
    private readonly LocalScriptStorageService _storage;

    public GitPanelTests() => _storage = new LocalScriptStorageService(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private CSharpCodeStudioViewModel Studio(RecordingGitService git, IStudioDialogService? dialogs = null)
    {
        var studio = new CSharpCodeStudioViewModel(
            new ScriptDocumentItem { Title = "Scratch", Code = "// scratch" }, _storage, new RoslynCompilerService(), new ScriptExecutionEngine(),
            () => { }, () => { }, postToUiThread: action => action(), dialogs: dialogs);
        studio.Git.GitService = git;
        return studio;
    }

    private static GitRepositoryStatus Status(params GitFileChange[] working) => new()
    {
        IsRepository = true,
        CurrentBranch = "main",
        StagedChanges = [],
        WorkingChanges = working
    };

    [Fact]
    public async Task ExplorerRows_GetTheirBadge_IncludingRowsDrawnLater_AndTheirFolders()
    {
        var library = Path.Combine(_root, "library");
        Directory.CreateDirectory(Path.Combine(library, "src"));
        await File.WriteAllTextAsync(Path.Combine(library, "src", "Program.cs"), "// program");
        var git = new RecordingGitService { RepositoryStatus = Status(new GitFileChange("src/Program.cs", GitFileStatusKind.Modified, IsStaged: false)) };
        var studio = Studio(git);
        await studio.RefreshExplorerAsync();

        await studio.Git.RefreshGitStatusAsync();
        var folder = studio.ExplorerRows.Rows.Single(r => r.IsDirectory && r.Name == "src");
        Assert.Equal("•", folder.GitStatusLetter);

        // The file's row is drawn only when its folder opens, after the status was read.
        folder.IsExpanded = true;
        var file = studio.ExplorerRows.Rows.Single(r => r.Name == "Program.cs");
        Assert.Equal("M", file.GitStatusLetter);
    }

    [Fact]
    public async Task DiscardAll_AsksSeparatelyAboutUntrackedFiles_AndKeepsThemWhenDeclined()
    {
        var git = new RecordingGitService
        {
            RepositoryStatus = Status(
                new GitFileChange("tracked.cs", GitFileStatusKind.Modified, IsStaged: false),
                new GitFileChange("new.cs", GitFileStatusKind.Untracked, IsStaged: false))
        };
        var dialogs = new ScriptedDialogService(StudioQuestions.Confirm, StudioQuestions.Cancel);
        var studio = Studio(git, dialogs);
        await studio.Git.RefreshGitStatusAsync();

        await studio.Git.DiscardAllChangesAsync();

        Assert.Equal(2, dialogs.Asked.Count);
        Assert.Contains("new.cs", dialogs.Asked[1].Details);
        Assert.Equal(false, git.DiscardAllIncludedUntracked);
    }

    [Fact]
    public async Task DiscardAll_Cancelled_DiscardsNothing()
    {
        var git = new RecordingGitService { RepositoryStatus = Status(new GitFileChange("tracked.cs", GitFileStatusKind.Modified, IsStaged: false)) };
        var dialogs = new ScriptedDialogService(StudioQuestions.Cancel);
        var studio = Studio(git, dialogs);
        await studio.Git.RefreshGitStatusAsync();

        await studio.Git.DiscardAllChangesAsync();

        Assert.Null(git.DiscardAllIncludedUntracked);
    }

    [Fact]
    public async Task DiscardingAnUntrackedFile_AsksBeforeDeletingIt()
    {
        var git = new RecordingGitService { RepositoryStatus = Status(new GitFileChange("new.cs", GitFileStatusKind.Untracked, IsStaged: false)) };
        var dialogs = new ScriptedDialogService(StudioQuestions.Cancel);
        var studio = Studio(git, dialogs);
        await studio.Git.RefreshGitStatusAsync();

        await studio.Git.DiscardFileChangesAsync(studio.Git.WorkingChanges[0]);

        Assert.Single(dialogs.Asked);
        Assert.Equal(0, git.DiscardFileCalls);
    }

    [Fact]
    public async Task RefreshesAskedForWhileOneRuns_RunOnceMore_NotOncePerAsk()
    {
        var git = new RecordingGitService { RepositoryStatus = Status(), StatusDelay = TimeSpan.FromMilliseconds(150) };
        var studio = Studio(git);

        var first = studio.Git.RefreshGitStatusAsync();
        for (var i = 0; i < 10; i++) _ = studio.Git.RefreshGitStatusAsync();
        await first;

        Assert.Equal(2, git.StatusCalls);
    }

    [Fact]
    public async Task GitIsAskedWhetherItIsInstalled_OnlyOnce()
    {
        var git = new RecordingGitService { RepositoryStatus = Status() };
        var studio = Studio(git);

        await studio.Git.RefreshGitStatusAsync();
        await studio.Git.RefreshGitStatusAsync();

        Assert.Equal(1, git.InstalledCalls);
    }

    [Theory]
    [InlineData("? \"caf\\303\\251.py\"", "café.py")]
    [InlineData("? \"\\346\\226\\207\\344\\273\\266.md\"", "文件.md")]
    [InlineData("? \"with \\\"quote\\\".txt\"", "with \"quote\".txt")]
    [InlineData("? plain name.txt", "plain name.txt")]
    public void QuotedPaths_AreDecoded_AsGitWroteThem(string line, string expected)
    {
        var status = GitStatusParser.Parse("# branch.head main\n" + line + "\n");

        Assert.Equal(expected, status.WorkingChanges.Single().RelativePath);
    }

    private sealed class RecordingGitService : IGitService
    {
        public GitRepositoryStatus RepositoryStatus { get; set; } = GitRepositoryStatus.EmptyClean();
        public TimeSpan StatusDelay { get; set; }
        public int StatusCalls;
        public int InstalledCalls;
        public int DiscardFileCalls;
        public bool? DiscardAllIncludedUntracked;

        private static Task<GitCommandResult> Ok() => Task.FromResult(new GitCommandResult(true, ""));

        public Task<bool> IsGitInstalledAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref InstalledCalls);
            return Task.FromResult(true);
        }

        public Task<bool> IsGitRepositoryAsync(string workspaceRoot, CancellationToken ct = default) => Task.FromResult(true);

        public async Task<GitRepositoryStatus> GetStatusAsync(string workspaceRoot, CancellationToken ct = default)
        {
            Interlocked.Increment(ref StatusCalls);
            if (StatusDelay > TimeSpan.Zero) await Task.Delay(StatusDelay, ct);
            return RepositoryStatus;
        }

        public Task<GitCommandResult> StageFileAsync(string workspaceRoot, string relativePath, CancellationToken ct = default) => Ok();
        public Task<GitCommandResult> StageAllAsync(string workspaceRoot, CancellationToken ct = default) => Ok();
        public Task<GitCommandResult> UnstageFileAsync(string workspaceRoot, string relativePath, CancellationToken ct = default) => Ok();
        public Task<GitCommandResult> UnstageAllAsync(string workspaceRoot, CancellationToken ct = default) => Ok();

        public Task<GitCommandResult> DiscardFileChangesAsync(string workspaceRoot, string relativePath, bool isUntracked = false, CancellationToken ct = default)
        {
            DiscardFileCalls++;
            return Ok();
        }

        public Task<GitCommandResult> DiscardAllChangesAsync(string workspaceRoot, bool includeUntracked = true, CancellationToken ct = default)
        {
            DiscardAllIncludedUntracked = includeUntracked;
            return Ok();
        }

        public Task<GitCommandResult> CommitAsync(string workspaceRoot, string message, bool stageAllIfNoneStaged = false, CancellationToken ct = default) => Ok();
        public Task<IReadOnlyList<GitBranchItem>> GetBranchesAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GitBranchItem>>([new GitBranchItem("main", true, false)]);
        public Task<GitCommandResult> CheckoutBranchAsync(string workspaceRoot, string branchName, bool createNew = false, CancellationToken ct = default) => Ok();
        public Task<GitCommandResult> PullAsync(string workspaceRoot, CancellationToken ct = default) => Ok();
        public Task<GitCommandResult> PushAsync(string workspaceRoot, CancellationToken ct = default) => Ok();
        public Task<GitCommandResult> SyncAsync(string workspaceRoot, CancellationToken ct = default) => Ok();
        public Task<GitCommandResult> InitRepositoryAsync(string workspaceRoot, CancellationToken ct = default) => Ok();
        public Task<string?> GetFileHeadContentAsync(string workspaceRoot, string relativePath, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetFileDiffAsync(string workspaceRoot, string relativePath, bool staged = false, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
