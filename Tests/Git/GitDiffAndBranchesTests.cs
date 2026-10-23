using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

public class GitDiffAndBranchesTests : IDisposable
{
    private readonly string _testBaseDir;
    private readonly LocalScriptStorageService _testStorage;

    public GitDiffAndBranchesTests()
    {
        _testBaseDir = Path.Combine(Path.GetTempPath(), "FrySharp_DiffBranchTests_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void GitDiffParser_UnifiedDiff_ParsesLinesAndSideBySide()
    {
        string diff = """
            diff --git a/Calculator.cs b/Calculator.cs
            index 1234567..89abcdef 100644
            --- a/Calculator.cs
            +++ b/Calculator.cs
            @@ -1,4 +1,5 @@
             using System;
            -public class Calc
            +public class Calculator
            +{
             }
            """;

        var doc = GitDiffParser.Parse("Calculator.cs", isStaged: false, diff, originalContent: null, modifiedContent: null);

        Assert.Equal("Calculator.cs", doc.FileName);
        Assert.Equal(2, doc.AdditionsCount); // +public class Calculator, +{
        Assert.Equal(1, doc.DeletionsCount); // -public class Calc
        Assert.NotEmpty(doc.InlineLines);
        Assert.NotEmpty(doc.SideBySideRows);

        // Verify inline markers
        Assert.Contains(doc.InlineLines, l => l.IsDeletion && l.Text.Trim() == "public class Calc");
        Assert.Contains(doc.InlineLines, l => l.IsAddition && l.Text.Trim() == "public class Calculator");

        // Verify side-by-side alignment
        var sbsModRow = doc.SideBySideRows.FirstOrDefault(r => r.OldKind == GitDiffLineKind.Deletion);
        Assert.NotNull(sbsModRow);
        Assert.Equal("public class Calc", sbsModRow.OldText?.Trim());
        Assert.Equal("public class Calculator", sbsModRow.NewText?.Trim());
    }

    [Fact]
    public void GitDiffParser_UntrackedFile_SynthesizesAdditions()
    {
        string newFileContent = """
            line 1
            line 2
            line 3
            """;

        var doc = GitDiffParser.Parse("NewScript.cs", isStaged: false, diffOutput: null, originalContent: null, modifiedContent: newFileContent);

        Assert.Equal(3, doc.AdditionsCount);
        Assert.Equal(0, doc.DeletionsCount);
        Assert.Equal(3, doc.InlineLines.Count);
        Assert.All(doc.InlineLines, l => Assert.True(l.IsAddition));
    }

    [Fact]
    public void GitDiffParser_DeletedFile_SynthesizesDeletions()
    {
        string oldFileContent = """
            old line 1
            old line 2
            """;

        var doc = GitDiffParser.Parse("Deleted.cs", isStaged: true, diffOutput: null, originalContent: oldFileContent, modifiedContent: null);

        Assert.Equal(0, doc.AdditionsCount);
        Assert.Equal(2, doc.DeletionsCount);
        Assert.Equal(2, doc.InlineLines.Count);
        Assert.All(doc.InlineLines, l => Assert.True(l.IsDeletion));
    }

    [Fact]
    public void GitCommitMessageGenerator_ConventionalCommits()
    {
        var testChanges = new List<GitFileChange>
        {
            new("Tests/MyTests.cs", GitFileStatusKind.Modified, false)
        };
        Assert.Equal("test: update MyTests.cs", GitCommitMessageGenerator.Generate(testChanges));

        var docsChanges = new List<GitFileChange>
        {
            new("docs/architecture.md", GitFileStatusKind.Modified, false)
        };
        Assert.Equal("docs: update architecture.md", GitCommitMessageGenerator.Generate(docsChanges));

        var gitChanges = new List<GitFileChange>
        {
            new("Services/Workspace/Git/GitService.cs", GitFileStatusKind.Modified, true),
            new("Services/Workspace/Git/GitDiffParser.cs", GitFileStatusKind.Added, true)
        };
        Assert.Equal("feat(git): update git implementation and components", GitCommitMessageGenerator.Generate(gitChanges));
    }

    [Fact]
    public async Task CSharpCodeStudioViewModel_OpenDiffTab_DisplaysDiffViewer()
    {
        var fakeGit = new TestDiffGitService();
        var studio = CreateStudio(fakeGit);

        var changeItem = new GitFileChangeItemViewModel(
            new GitFileChange("Services/Workspace/Git/GitService.cs", GitFileStatusKind.Modified, IsStaged: false));

        await studio.OpenDiffTabAsync(changeItem);

        Assert.True(studio.ShowDiffViewer);
        Assert.False(studio.ShowTextEditor);
        Assert.NotNull(studio.ActiveDiffDocument);
        Assert.Equal("GitService.cs", studio.ActiveDiffDocument.FileName);

        // Verify active tab in OpenTabs
        var diffTab = studio.OpenTabs.FirstOrDefault(t => t.IsActive);
        Assert.NotNull(diffTab);
        Assert.True(diffTab.IsDiffTab);
        Assert.Contains("GitService.cs (Working Tree)", diffTab.Title);

        // Verify toggle layout
        Assert.True(studio.IsSideBySideDiff);
        studio.ToggleDiffLayout();
        Assert.False(studio.IsSideBySideDiff);

        // Close diff tab
        await studio.CloseActiveDiffTabAsync();
        Assert.False(studio.ShowDiffViewer);
    }

    [Fact]
    public async Task CSharpCodeStudioViewModel_Branches_RefreshAndCheckout()
    {
        var fakeGit = new TestDiffGitService();
        var studio = CreateStudio(fakeGit);

        await studio.RefreshBranchesAsync();

        Assert.Equal(3, studio.AvailableBranches.Count);
        Assert.Equal("main", studio.CurrentGitBranch);

        // Filter branches
        studio.BranchFilterText = "feature";
        Assert.Single(studio.FilteredBranches);
        Assert.Equal("feature/git-ui", studio.FilteredBranches.First().Name);

        // Checkout branch
        var target = studio.AvailableBranches.First(b => b.Name == "feature/git-ui");
        await studio.CheckoutBranchAsync(target);

        Assert.Equal("feature/git-ui", studio.CurrentGitBranch);
        Assert.Equal("feature/git-ui", fakeGit.CheckedOutBranch);
    }

    [Fact]
    public async Task CSharpCodeStudioViewModel_Branches_CreateBranch()
    {
        var fakeGit = new TestDiffGitService();
        var studio = CreateStudio(fakeGit);

        studio.NewBranchName = "bugfix/issue-42";
        await studio.CreateAndCheckoutBranchAsync();

        Assert.Equal("bugfix/issue-42", studio.CurrentGitBranch);
        Assert.Equal("bugfix/issue-42", fakeGit.CreatedBranch);
    }

    [Fact]
    public void CSharpCodeStudioViewModel_SmartCommitMessage_GeneratesAndPopulates()
    {
        var fakeGit = new TestDiffGitService();
        var studio = CreateStudio(fakeGit);

        studio.WorkingChanges.Add(new GitFileChangeItemViewModel(
            new GitFileChange("Services/Workspace/Git/GitService.cs", GitFileStatusKind.Modified, false)));

        studio.GenerateSmartCommitMessage();

        Assert.Equal("feat(git): update GitService.cs", studio.GitCommitMessage);
    }

    [Fact]
    public async Task GitDiffTab_SaveDocument_NeverOverwritesFileOnDisk()
    {
        var fakeGit = new TestDiffGitService();
        var studio = CreateStudio(fakeGit);

        var tempFile = Path.Combine(_testBaseDir, "RealSourceCode.cs");
        await File.WriteAllTextAsync(tempFile, "public class KeepMeSafe { }");

        var syntheticDoc = new ScriptDocumentItem
        {
            Id = $"git-diff:{tempFile}",
            Title = "RealSourceCode.cs (Working Tree)",
            SourceFilePath = tempFile,
            IsReadOnly = true,
            Code = ""
        };

        // Attempt saving via storage service
        var savedStorage = await _testStorage.SaveSourceFileAsync(syntheticDoc, overwriteChangesOnDisk: true);
        Assert.True(savedStorage);

        // Verify the real file was untouched
        var onDisk = await File.ReadAllTextAsync(tempFile);
        Assert.Equal("public class KeepMeSafe { }", onDisk);
    }

    [Fact]
    public async Task BranchPicker_CreateAndCheckoutBranch_UsesFilterTextWhenNewBranchNameEmpty()
    {
        var fakeGit = new TestDiffGitService();
        var studio = CreateStudio(fakeGit);

        studio.NewBranchName = string.Empty;
        studio.BranchFilterText = "feature/new-from-filter";

        await studio.CreateAndCheckoutBranchCommand.ExecuteAsync(null);

        Assert.Equal("feature/new-from-filter", fakeGit.CreatedBranch);
        Assert.Equal(string.Empty, studio.BranchFilterText);
        Assert.Equal(string.Empty, studio.NewBranchName);
    }

    private CSharpCodeStudioViewModel CreateStudio(IGitService gitService)
    {
        var doc = new ScriptDocumentItem
        {
            Title = "Diff Test Script",
            Code = "Console.WriteLine(\"Testing Diff\");"
        };

        var studio = new CSharpCodeStudioViewModel(
            doc,
            _testStorage,
            new RoslynCompilerService(),
            new ScriptExecutionEngine(),
            backToHubAction: () => { },
            backToHomeAction: () => { },
            postToUiThread: action => action());

        studio.GitService = gitService;
        return studio;
    }

    private sealed class TestDiffGitService : IGitService
    {
        public string? CheckedOutBranch { get; private set; }
        public string? CreatedBranch { get; private set; }

        public Task<bool> IsGitInstalledAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> IsGitRepositoryAsync(string workspaceRoot, CancellationToken ct = default) => Task.FromResult(true);

        public Task<GitRepositoryStatus> GetStatusAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(GitRepositoryStatus.EmptyClean(CheckedOutBranch ?? "main"));

        public Task<GitCommandResult> StageFileAsync(string workspaceRoot, string relativePath, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> StageAllAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> UnstageFileAsync(string workspaceRoot, string relativePath, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> UnstageAllAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> DiscardFileChangesAsync(string workspaceRoot, string relativePath, bool isUntracked = false, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> DiscardAllChangesAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> CommitAsync(string workspaceRoot, string message, bool stageAllIfNoneStaged = false, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<IReadOnlyList<GitBranchItem>> GetBranchesAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GitBranchItem>>(new[]
            {
                new GitBranchItem("main", CheckedOutBranch == null || CheckedOutBranch == "main", false),
                new GitBranchItem("feature/git-ui", CheckedOutBranch == "feature/git-ui", false),
                new GitBranchItem("origin/main", false, true)
            });

        public Task<GitCommandResult> CheckoutBranchAsync(string workspaceRoot, string branchName, bool createNew = false, CancellationToken ct = default)
        {
            if (createNew) CreatedBranch = branchName;
            CheckedOutBranch = branchName;
            return Task.FromResult(new GitCommandResult(true, ""));
        }

        public Task<GitCommandResult> PullAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> PushAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> SyncAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<GitCommandResult> InitRepositoryAsync(string workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, ""));

        public Task<string?> GetFileHeadContentAsync(string workspaceRoot, string relativePath, CancellationToken ct = default) =>
            Task.FromResult<string?>("// Original HEAD content\npublic class Calculator\n{\n}\n");

        public Task<string?> GetFileDiffAsync(string workspaceRoot, string relativePath, bool staged = false, CancellationToken ct = default) =>
            Task.FromResult<string?>("""
                diff --git a/Calculator.cs b/Calculator.cs
                --- a/Calculator.cs
                +++ b/Calculator.cs
                @@ -1,3 +1,4 @@
                 public class Calculator
                 {
                +    public int Add(int a, int b) => a + b;
                 }
                """);
    }
}
