using System.Collections.Generic;
using System.Threading.Tasks;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Common;

/// <summary>How a dialog button looks and what the keyboard does with it: Enter presses the primary one, Escape the cancel one.</summary>
public enum DialogChoiceKind
{
    Normal,
    Primary,
    Destructive,
    Cancel
}

/// <summary>One button of a studio dialog; <see cref="Id"/> is what <see cref="IStudioDialogService.ShowChoiceAsync"/> returns.</summary>
public sealed record DialogChoice(string Id, string Label, DialogChoiceKind Kind = DialogChoiceKind.Normal);

/// <summary>A question for the user: a title, a sentence, optionally a list (the files it is about), and its buttons.</summary>
public sealed record DialogRequest(string Title, string Message, IReadOnlyList<string> Details, IReadOnlyList<DialogChoice> Choices);

/// <summary>
/// Asks the user something inside the studio (one modal card over the whole studio, never a separate window that can end up
/// behind it). View models get it through an optional constructor parameter; the studio host owns the one that draws.
/// </summary>
public interface IStudioDialogService
{
    /// <summary>
    /// Shows <paramref name="request"/> and returns the <see cref="DialogChoice.Id"/> the user picked (Escape and the close
    /// button pick the <see cref="DialogChoiceKind.Cancel"/> choice). Returns null when there is nobody to ask (tests,
    /// tools): callers then do what they did before the question existed.
    /// </summary>
    Task<string?> ShowChoiceAsync(DialogRequest request);
}

/// <summary>No studio on screen: nothing is asked.</summary>
public sealed class NullStudioDialogService : IStudioDialogService
{
    public static NullStudioDialogService Instance { get; } = new();

    public Task<string?> ShowChoiceAsync(DialogRequest request) => Task.FromResult<string?>(null);
}

/// <summary>The questions the studio asks in more than one place.</summary>
public static class StudioQuestions
{
    public const string Save = "save";
    public const string DontSave = "dont-save";
    public const string Cancel = "cancel";
    public const string Confirm = "confirm";

    /// <summary>What to do with unsaved documents that are about to be closed. Null: nobody to ask.</summary>
    public static Task<string?> AskToSaveAsync(IStudioDialogService dialogs, IReadOnlyList<string> documentTitles, bool closingStudio = false)
    {
        var single = documentTitles.Count == 1;
        var title = single ? $"Save changes to {documentTitles[0]}?" : $"Save changes to {documentTitles.Count} files?";
        var message = closingStudio
            ? "These files have changes that aren't saved. They will be lost if you quit without saving."
            : single
                ? "Your changes will be lost if you don't save them."
                : "These files have changes that aren't saved. They will be lost if you close them without saving.";

        return dialogs.ShowChoiceAsync(new DialogRequest(
            title,
            message,
            single ? [] : documentTitles,
            [
                new DialogChoice(Save, single ? "Save" : "Save All", DialogChoiceKind.Primary),
                new DialogChoice(DontSave, "Don't Save", DialogChoiceKind.Destructive),
                new DialogChoice(Cancel, "Cancel", DialogChoiceKind.Cancel)
            ]));
    }

    /// <summary>
    /// A yes/no question about something that can't be undone. True when the user confirmed, or when there is nobody to ask
    /// (tests and tools keep the behaviour they had before the question existed).
    /// </summary>
    public static async Task<bool> ConfirmAsync(IStudioDialogService dialogs, string title, string message, string confirmLabel, IReadOnlyList<string>? details = null, bool destructive = true)
    {
        var answer = await dialogs.ShowChoiceAsync(new DialogRequest(
            title,
            message,
            details ?? [],
            [
                new DialogChoice(Confirm, confirmLabel, destructive ? DialogChoiceKind.Destructive : DialogChoiceKind.Primary),
                new DialogChoice(Cancel, "Cancel", DialogChoiceKind.Cancel)
            ]));

        return answer is null or Confirm;
    }
}
