using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.Git;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace.Git;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Git;
using Xunit;

namespace CSharpEditorPlugin.Tests;

public class CSharpCodeStudioGitManagementTests : IDisposable
{
    private readonly string _testBaseDir;
    private readonly LocalScriptStorageService _testStorage;

    public CSharpCodeStudioGitManagementTests()
    {
        _testBaseDir = Path.Combine(Path.GetTempPath(), "FrySharp_GitTests_" + Guid.NewGuid().ToString("N"));
        _testStorage = new LocalScriptStorageService(_testBaseDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testBaseDir))
            {
                Directory.Delete(_testBaseDir, recursive: true);
            }
        }
        catch { }
    }

    private CSharpCodeStudioViewModel CreateStudio(IGitService gitService)
    {
        var doc = new ScriptDocumentItem
        {
            Title = "Git Management Test Script",
            Code = "Console.WriteLine(\"Testing Git\");"
        };

        var studio = new CSharpCodeStudioViewModel(
            doc,
            _testStorage,
            new RoslynCompilerService(),
            new ScriptExecutionEngine(),
            backToHubAction: () => { },
            backToHomeAction: () => { },
            postToUiThread: action => action());

        studio.Git.GitService = gitService;
        return studio;
    }

    [Fact]
    public void SelectActivityBarItem_Index6_ActivatesSourceControl()
    {
        var fakeGit = new FakeGitService();
        var studio = CreateStudio(fakeGit);

        Assert.False(studio.IsSourceControlActive);

        studio.SelectActivityBarItem(6);

        Assert.Equal(6, studio.SelectedActivityBarIndex);
        Assert.True(studio.IsSourceControlActive);
        Assert.StartsWith("SOURCE CONTROL", studio.SideBarTitle);
        Assert.True(studio.IsSideBarVisible);
    }

    [Fact]
    public void SelectActivityBarItem_WhenSourceControlActive_TogglesSideBar()
    {
        var fakeGit = new FakeGitService();
        var studio = CreateStudio(fakeGit);

        studio.SelectActivityBarItem(6);
        Assert.True(studio.IsSideBarVisible);

        // Clicking again toggles sidebar closed
        studio.SelectActivityBarItem(6);
        Assert.False(studio.IsSideBarVisible);

        // Clicking again re-opens
        studio.SelectActivityBarItem(6);
        Assert.True(studio.IsSideBarVisible);
    }

    [Fact]
    public async Task RefreshGitStatusAsync_PopulatesStagedAndWorkingChanges()
    {
        var fakeGit = new FakeGitService
        {
            RepositoryStatus = new GitRepositoryStatus
            {
                IsRepository = true,
                CurrentBranch = "feature/awesome",
                AheadCount = 1,
                BehindCount = 0,
                StagedChanges = new[]
                {
                    new GitFileChange("src/Added.cs", GitFileStatusKind.Added, IsStaged: true)
                },
                WorkingChanges = new[]
                {
                    new GitFileChange("src/Modified.cs", GitFileStatusKind.Modified, IsStaged: false),
                    new GitFileChange("src/NewFile.cs", GitFileStatusKind.Untracked, IsStaged: false)
                }
            }
        };

        var studio = CreateStudio(fakeGit);

        await studio.Git.RefreshGitStatusAsync();

        Assert.True(studio.Git.IsGitRepository);
        Assert.Equal("feature/awesome", studio.Git.CurrentGitBranch);
        Assert.Equal(1, studio.Git.GitAheadCount);
        Assert.Equal(0, studio.Git.GitBehindCount);
        Assert.Equal("1↑", studio.Git.SyncStatusText);
        Assert.Equal("Sync Changes 1↑", studio.Git.SyncButtonText);
        Assert.True(studio.Git.IsSyncButtonVisible);

        Assert.Single(studio.Git.StagedChanges);
        Assert.Equal("Added.cs", studio.Git.StagedChanges[0].FileName);
        Assert.Equal("A", studio.Git.StagedChanges[0].StatusLetter);
        Assert.True(studio.Git.HasStagedChanges);

        Assert.Equal(2, studio.Git.WorkingChanges.Count);
        Assert.True(studio.Git.HasWorkingChanges);
        Assert.Equal(3, studio.Git.UncommittedChangesCount);
    }

    [Fact]
    public async Task CommitAsync_CallsGitServiceAndClearsMessage()
    {
        var fakeGit = new FakeGitService
        {
            RepositoryStatus = new GitRepositoryStatus
            {
                IsRepository = true,
                CurrentBranch = "main",
                StagedChanges = new[]
                {
                    new GitFileChange("src/File.cs", GitFileStatusKind.Modified, IsStaged: true)
                }
            }
        };

        var studio = CreateStudio(fakeGit);
        await studio.Git.RefreshGitStatusAsync();

        studio.Git.GitCommitMessage = "Initial commit message";
        await studio.Git.CommitAsync();

        Assert.Equal("Initial commit message", fakeGit.LastCommitMessage);
        Assert.Empty(studio.Git.GitCommitMessage);
    }

    [Fact]
    public async Task StageAndUnstageCommands_CallGitService()
    {
        var fakeGit = new FakeGitService
        {
            RepositoryStatus = new GitRepositoryStatus
            {
                IsRepository = true,
                CurrentBranch = "main",
                WorkingChanges = new[]
                {
                    new GitFileChange("src/File.cs", GitFileStatusKind.Modified, IsStaged: false)
                }
            }
        };

        var studio = CreateStudio(fakeGit);
        await studio.Git.RefreshGitStatusAsync();

        var changeItem = studio.Git.WorkingChanges[0];

        await studio.Git.StageFileCommand.ExecuteAsync(changeItem);
        Assert.Equal("src/File.cs", fakeGit.LastStagedPath);

        await studio.Git.UnstageFileCommand.ExecuteAsync(changeItem);
        Assert.Equal("src/File.cs", fakeGit.LastUnstagedPath);
    }

    [Fact]
    public async Task InitGitRepositoryCommand_CallsGitServiceAndRefreshes()
    {
        var fakeGit = new FakeGitService
        {
            IsRepo = false
        };

        var studio = CreateStudio(fakeGit);
        await studio.Git.RefreshGitStatusAsync();
        Assert.False(studio.Git.IsGitRepository);

        await studio.Git.InitGitRepositoryAsync();

        Assert.True(fakeGit.InitCalled);
        Assert.True(studio.Git.IsGitRepository);
    }

    private sealed class FakeGitService : IGitService
    {
        public bool IsInstalled { get; set; } = true;
        public bool IsRepo { get; set; } = true;
        public bool InitCalled { get; set; }
        public string? LastCommitMessage { get; set; }
        public string? LastStagedPath { get; set; }
        public string? LastUnstagedPath { get; set; }
        public GitRepositoryStatus RepositoryStatus { get; set; } = GitRepositoryStatus.EmptyClean();

        public Task<bool> IsGitInstalledAsync(CancellationToken ct = default) => Task.FromResult(IsInstalled);
        public Task<bool> IsGitRepositoryAsync(string workspaceRoot, CancellationToken ct = default) => Task.FromResult(IsRepo);
        public Task<GitRepositoryStatus> GetStatusAsync(string workspaceRoot, CancellationToken ct = default) => Task.FromResult(RepositoryStatus);

        public Task<GitCommandResult> StageFileAsync(string workspaceRoot, string relativePath, CancellationToken ct = default)
        {
            LastStagedPath = relativePath;
            return Task.FromResult(new GitCommandResult(true, ""));
        }

        public Task<GitCommandResult> StageAllAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> UnstageFileAsync(string workspaceRoot, string relativePath, CancellationToken ct = default)
        {
            LastUnstagedPath = relativePath;
            return Task.FromResult(new GitCommandResult(true, ""));
        }

        public Task<GitCommandResult> UnstageAllAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> DiscardFileChangesAsync(string workspaceRoot, string relativePath, bool isUntracked = false, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> DiscardAllChangesAsync(string workspaceRoot, bool includeUntracked = true, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> CommitAsync(string workspaceRoot, string message, bool stageAllIfNoneStaged = false, CancellationToken ct = default)
        {
            LastCommitMessage = message;
            RepositoryStatus = GitRepositoryStatus.EmptyClean(RepositoryStatus.CurrentBranch);
            return Task.FromResult(new GitCommandResult(true, ""));
        }

        public Task<IReadOnlyList<GitBranchItem>> GetBranchesAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GitBranchItem>>(new[] { new GitBranchItem("main", true, false) });

        public Task<GitCommandResult> CheckoutBranchAsync(string workspaceRoot, string branchName, bool createNew = false, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> PullAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> PushAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> SyncAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> InitRepositoryAsync(string workspaceRoot, CancellationToken ct = default)
        {
            InitCalled = true;
            IsRepo = true;
            return Task.FromResult(new GitCommandResult(true, ""));
        }

        public Task<string?> GetFileHeadContentAsync(string workspaceRoot, string relativePath, CancellationToken ct = default) =>
            Task.FromResult<string?>("// Original content");

        public Task<string?> GetFileDiffAsync(string workspaceRoot, string relativePath, bool staged = false, CancellationToken ct = default) =>
            Task.FromResult<string?>("--- a/file\n+++ b/file\n+ new line");
    }
}
