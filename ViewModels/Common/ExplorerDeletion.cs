using PdfEditorApp.Plugins.CSharpEditor.Services.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using ExplorerItemViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer.ExplorerItemViewModel;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;

/// <summary>What happened to an Explorer item the user deleted (it was already confirmed on its row).</summary>
public enum ExplorerDeletionResult
{
    Deleted,
    Cancelled,
    Failed
}

/// <summary>
/// Deleting from an Explorer, the same in every studio: the item goes to the machine's Trash, where it can be put back. Only
/// when the Trash can't take it is it deleted for good, and only after a second, explicit yes.
/// </summary>
public static class ExplorerDeletion
{
    public static string TrashName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    /// <returns>What happened, and why it failed for the status line (null unless <see cref="ExplorerDeletionResult.Failed"/>).</returns>
    public static async Task<(ExplorerDeletionResult Result, string? Error)> DeleteAsync(IScriptStorageService storage, IStudioDialogService dialogs, ExplorerItemViewModel item)
    {
        if (item.IsPlaceholder || item.IsExternalGroup) return (ExplorerDeletionResult.Cancelled, null);
        if (!item.IsDirectory && string.IsNullOrEmpty(item.DocumentId)) return (ExplorerDeletionResult.Cancelled, null);

        try
        {
            var trashed = item.IsDirectory
                ? await storage.MoveFolderToTrashAsync(item.FullPath)
                : await storage.MoveItemToTrashAsync(item.DocumentId!);
            if (trashed) return (ExplorerDeletionResult.Deleted, null);

            var what = item.IsDirectory ? $"the folder '{item.Name}' and everything in it" : $"'{item.Name}'";
            if (!await StudioQuestions.ConfirmAsync(dialogs,
                    $"Delete {item.Name} permanently?",
                    $"{char.ToUpperInvariant(what[0])}{what[1..]} couldn't be moved to the {TrashName}. Deleting it permanently can't be undone.",
                    "Delete Permanently"))
            {
                return (ExplorerDeletionResult.Cancelled, null);
            }

            if (item.IsDirectory) await storage.DeleteFolderAsync(item.FullPath);
            else await storage.DeleteItemAsync(item.DocumentId!);
            return (ExplorerDeletionResult.Deleted, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ExplorerDeletionResult.Failed, $"⚠️ Couldn't delete {item.Name}: {ex.Message}");
        }
    }

    /// <summary>The document ids under <paramref name="item"/> (itself when it is a document): their tabs close with it.</summary>
    public static List<string> DocumentIds(ExplorerItemViewModel item)
    {
        var ids = new List<string>();
        void Walk(ExplorerItemViewModel node)
        {
            if (!node.IsDirectory && !string.IsNullOrEmpty(node.DocumentId)) ids.Add(node.DocumentId);
            foreach (var child in node.Children) Walk(child);
        }

        Walk(item);
        return ids;
    }
}
