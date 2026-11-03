using CSharpEditorPlugin.Tests.TestSupport;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using Xunit;
using CSharpCodeStudioViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.CSharpCodeStudioViewModel;
using CSharpNotebookStudioViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks.CSharpNotebookStudioViewModel;

namespace CSharpEditorPlugin.Tests.CodeStudio;

/// <summary>Closing a document with unsaved edits asks first (Save / Don't Save / Cancel); nothing is lost without the user choosing it.</summary>
public class UnsavedChangesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FrySharp_Unsaved_" + Guid.NewGuid().ToString("N"));
    private readonly LocalScriptStorageService _storage;

    public UnsavedChangesTests() => _storage = new LocalScriptStorageService(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private CSharpCodeStudioViewModel CodeStudio(ScriptDocumentItem first, IStudioDialogService dialogs) =>
        new(first, _storage, new RoslynCompilerService(), new ScriptExecutionEngine(), () => { }, () => { }, dialogs: dialogs);

    private async Task<(CSharpCodeStudioViewModel Studio, ScriptDocumentItem Edited)> TwoTabsWithAnEditAsync(IStudioDialogService dialogs)
    {
        var a = await _storage.CreateNewScriptAsync("Script A");
        var b = await _storage.CreateNewScriptAsync("Script B");
        var studio = CodeStudio(a, dialogs);
        await studio.UpdateActiveScriptAsync(b);
        studio.Code = "Console.WriteLine(\"edited\");";
        return (studio, b);
    }

    [Fact]
    public async Task ClosingAnEditedTab_Cancel_KeepsTheTabAndItsEdits()
    {
        var dialogs = new ScriptedDialogService(StudioQuestions.Cancel);
        var (studio, edited) = await TwoTabsWithAnEditAsync(dialogs);
        var tab = studio.OpenTabs.Single(t => t.Id == edited.Id);

        await studio.CloseTabAsync(tab);

        Assert.Single(dialogs.Asked);
        Assert.Contains("Script B", dialogs.Asked[0].Title);
        Assert.Contains(tab, studio.OpenTabs);
        Assert.True(tab.IsDirty);
        Assert.Equal("Console.WriteLine(\"edited\");", studio.Code);
    }

    [Fact]
    public async Task ClosingAnEditedTab_Save_WritesTheEditThenCloses()
    {
        var dialogs = new ScriptedDialogService(StudioQuestions.Save);
        var (studio, edited) = await TwoTabsWithAnEditAsync(dialogs);

        await studio.CloseTabAsync(studio.OpenTabs.Single(t => t.Id == edited.Id));

        Assert.DoesNotContain(studio.OpenTabs, t => t.Id == edited.Id);
        var onDisk = await _storage.LoadScriptAsync(edited.Id);
        Assert.Equal("Console.WriteLine(\"edited\");", onDisk!.Code);
    }

    [Fact]
    public async Task ClosingAnEditedTab_DontSave_ClosesWithoutWriting()
    {
        var dialogs = new ScriptedDialogService(StudioQuestions.DontSave);
        var (studio, edited) = await TwoTabsWithAnEditAsync(dialogs);

        await studio.CloseTabAsync(studio.OpenTabs.Single(t => t.Id == edited.Id));

        Assert.DoesNotContain(studio.OpenTabs, t => t.Id == edited.Id);
        var onDisk = await _storage.LoadScriptAsync(edited.Id);
        Assert.NotEqual("Console.WriteLine(\"edited\");", onDisk!.Code);
    }

    [Fact]
    public async Task ClosingACleanTab_AsksNothing()
    {
        var dialogs = new ScriptedDialogService();
        var a = await _storage.CreateNewScriptAsync("Script A");
        var b = await _storage.CreateNewScriptAsync("Script B");
        var studio = CodeStudio(a, dialogs);
        await studio.UpdateActiveScriptAsync(b);

        await studio.CloseTabAsync(studio.OpenTabs.Single(t => t.Id == b.Id));

        Assert.Empty(dialogs.Asked);
        Assert.Single(studio.OpenTabs);
    }

    [Fact]
    public async Task CloseOthers_AsksOnceAboutEveryEditedTab_AndCancelKeepsThemAll()
    {
        var dialogs = new ScriptedDialogService(StudioQuestions.Cancel);
        var a = await _storage.CreateNewScriptAsync("Script A");
        var b = await _storage.CreateNewScriptAsync("Script B");
        var c = await _storage.CreateNewScriptAsync("Script C");
        var studio = CodeStudio(a, dialogs);
        await studio.UpdateActiveScriptAsync(b);
        studio.Code = "// b edited";
        await studio.UpdateActiveScriptAsync(c);
        studio.Code = "// c edited";

        await studio.CloseOtherTabsAsync(studio.OpenTabs.Single(t => t.Id == a.Id));

        Assert.Single(dialogs.Asked);
        Assert.Equal(2, dialogs.Asked[0].Details.Count);
        Assert.Equal(3, studio.OpenTabs.Count);
    }

    [Fact]
    public async Task SaveAll_SavesEveryEditedTab_ShownOrNot()
    {
        var a = await _storage.CreateNewScriptAsync("Script A");
        var b = await _storage.CreateNewScriptAsync("Script B");
        var studio = CodeStudio(a, new ScriptedDialogService());
        studio.Code = "// a edited";
        await studio.UpdateActiveScriptAsync(b);
        studio.Code = "// b edited";

        Assert.Equal(2, studio.UnsavedDocumentTitles.Count);
        Assert.True(await studio.SaveAllAsync());

        Assert.Empty(studio.UnsavedDocumentTitles);
        Assert.Equal("// a edited", (await _storage.LoadScriptAsync(a.Id))!.Code);
        Assert.Equal("// b edited", (await _storage.LoadScriptAsync(b.Id))!.Code);
    }

    [Fact]
    public async Task LeavingAScriptThatWasOnlyLookedAt_DoesNotRewriteIt()
    {
        var a = await _storage.CreateNewScriptAsync("Script A");
        var b = await _storage.CreateNewScriptAsync("Script B");
        var studio = CodeStudio(a, new ScriptedDialogService());
        await studio.UpdateActiveScriptAsync(b);
        var path = Directory.GetFiles(_root, "Script B.frycs", SearchOption.AllDirectories).Single();
        var before = File.ReadAllText(path);
        var version = _storage.ContentVersion;

        studio.BackToHubCommand.Execute(null);
        await studio.UpdateActiveScriptAsync(a);

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(version, _storage.ContentVersion);
    }

    [Fact]
    public async Task EditingTheNotes_MarksTheTabEdited()
    {
        var a = await _storage.CreateNewScriptAsync("Script A");
        var studio = CodeStudio(a, new ScriptedDialogService());

        studio.Notes = "a note";

        Assert.True(studio.OpenTabs.Single().IsDirty);
    }

    [Fact]
    public async Task ClosingAModifiedNotebook_Cancel_KeepsIt_Save_WritesIt()
    {
        var dialogs = new ScriptedDialogService(StudioQuestions.Cancel, StudioQuestions.Save);
        var studio = new CSharpNotebookStudioViewModel(
            new NotebookDocumentItem { Title = "First" }, _storage, new RoslynCompilerService(), new ScriptExecutionEngine(),
            () => { }, () => { }, dialogs: dialogs);
        await studio.NewNotebookTab();
        var tab = studio.ActiveTab!;
        tab.Notebook.IsEphemeral = false;
        tab.Title = "Edited notebook";
        tab.Cells[0].Source = "Console.WriteLine(42);";
        Assert.True(tab.IsModified);

        studio.CloseTab(tab);
        Assert.Contains(tab, studio.Tabs);

        studio.CloseTab(tab);
        await studio.PendingClose;
        Assert.DoesNotContain(tab, studio.Tabs);
        Assert.Equal(2, dialogs.Asked.Count);
        Assert.False(tab.IsModified);
        Assert.Contains(Directory.GetFiles(_root, "*.frynb", SearchOption.AllDirectories), f => File.ReadAllText(f).Contains("Console.WriteLine(42);"));
    }
}
