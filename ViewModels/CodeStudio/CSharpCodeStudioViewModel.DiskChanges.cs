using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Services.Activities;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;

// Open documents follow their files when another program changes them (git pull, checkout, a discard, another editor):
// a document without unsaved edits reloads in place; one with edits keeps them and says the file changed, so the next
// save can't silently undo what the other program did (and can't be silently undone by it).
public partial class CSharpCodeStudioViewModel
{
    /// <summary>Shown over the editor when the open document's file changed on disk while it had unsaved edits.</summary>
    [ObservableProperty]
    private string? _changedOnDiskNotice;

    public bool HasChangedOnDiskNotice => ChangedOnDiskNotice != null;

    partial void OnChangedOnDiskNoticeChanged(string? value) => OnPropertyChanged(nameof(HasChangedOnDiskNotice));

    // The tabs whose file changed while they had unsaved edits (their notice shows when they are on screen).
    private readonly HashSet<string> _changedOnDisk = new(StringComparer.OrdinalIgnoreCase);

    private static StringComparer PathComparer => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private void WatchOpenDocumentsOnDisk()
    {
        _storageService.ExternalFilesChanged += paths => _postToUiThread(() =>
            ReloadChangedDocumentsAsync(paths).FireAndForget(_activities, "Reloading files changed on disk"));
    }

    // The file a tab shows, when it has one.
    private string? DocumentFilePath(Common.StudioTabItemViewModel tab)
    {
        if (tab.Document.SourceFilePath is { } source) return Path.GetFullPath(source);
        var relative = _storageService.GetWorkspaceRelativePath(tab.Id);
        return string.IsNullOrEmpty(relative)
            ? null
            : Path.GetFullPath(Path.Combine(_storageService.ActiveWorkspaceRootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// Brings the open documents among <paramref name="changedPaths"/> (null: any may have changed) up to date with their
    /// files: reloaded when they have no unsaved edits, flagged when they do.
    /// </summary>
    internal async Task ReloadChangedDocumentsAsync(IReadOnlyCollection<string>? changedPaths)
    {
        var changed = changedPaths == null ? null : new HashSet<string>(changedPaths.Select(Path.GetFullPath), PathComparer);
        foreach (var tab in OpenTabs.ToList())
        {
            if (tab.Document.IsReadOnly || tab.IsDiffTab || tab.IsImage) continue;
            if (DocumentFilePath(tab) is not { } path || (changed != null && !changed.Contains(path))) continue;

            // Read the file itself to compare: loading it through the storage would also move what the storage thinks
            // was last saved, and then an automatic save could write over the other program's change.
            var onDisk = await Task.Run(() => ReadDocumentTextAsync(tab, path));
            if (onDisk == null || !OpenTabs.Contains(tab)) continue;

            var current = tab.Id == Script.Id ? Code : tab.Document.Code ?? string.Empty;
            if (string.Equals(onDisk, current, StringComparison.Ordinal)) continue;

            if (tab.IsDirty)
            {
                _changedOnDisk.Add(tab.Id);
                if (tab.Id == Script.Id) UpdateChangedOnDiskNotice();
                continue;
            }

            await ReloadTabFromDiskAsync(tab);
        }
    }

    // The text the editor would show for the file now: a source file's text, or a studio script's code. Null when it can't
    // be read (gone, too big for the editor, locked) — the tab is then left as it is.
    private static async Task<string?> ReadDocumentTextAsync(Common.StudioTabItemViewModel tab, string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > LocalScriptStorageService.MaxEditorFileBytes) return null;
            var text = await File.ReadAllTextAsync(path);
            if (tab.Document.SourceFilePath != null) return text;
            return System.Text.Json.JsonSerializer.Deserialize<Models.ScriptDocumentItem>(text)?.Code;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task ReloadTabFromDiskAsync(Common.StudioTabItemViewModel tab)
    {
        var fresh = await Task.Run(() => _storageService.LoadScriptAsync(tab.Id));
        if (fresh == null || !OpenTabs.Contains(tab)) return;

        _changedOnDisk.Remove(tab.Id);
        tab.IsDirty = false;
        if (tab.Id == Script.Id)
        {
            _isRestoringTabState = true;
            try
            {
                Code = fresh.Code ?? string.Empty;
                if (tab.Document.SourceFilePath == null) Notes = fresh.Notes;
            }
            finally
            {
                _isRestoringTabState = false;
            }

            RequestReloadEditorText?.Invoke();
            UpdateChangedOnDiskNotice();
        }
        else
        {
            tab.Document.Code = fresh.Code;
            if (tab.Document.SourceFilePath == null) tab.Document.Notes = fresh.Notes;
        }
    }

    private void UpdateChangedOnDiskNotice() =>
        ChangedOnDiskNotice = _changedOnDisk.Contains(Script.Id)
            ? $"{Script.Title} changed on disk. Reload it, or keep your version (saving will replace the file)."
            : null;

    /// <summary>Drops the unsaved edits of the open document and shows what its file has now.</summary>
    [RelayCommand]
    private async Task ReloadFromDiskAsync()
    {
        if (OpenTabs.FirstOrDefault(t => t.Id == Script.Id) is { } tab) await ReloadTabFromDiskAsync(tab);
    }

    /// <summary>Keeps the edits; the next save replaces the file (the user has been told it changed).</summary>
    [RelayCommand]
    private void KeepMyVersion()
    {
        _changedOnDisk.Remove(Script.Id);
        UpdateChangedOnDiskNotice();
    }
}
