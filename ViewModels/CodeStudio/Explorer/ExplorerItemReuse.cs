namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;

/// <summary>
/// The items of an Explorer tree that is being drawn again, by kind and path. A refresh takes back the items still there
/// instead of making new ones, so their rows (and the controls on screen for them) stay: the row list then changes only
/// where the workspace did (<see cref="ExplorerRowList"/>), and refreshing a workspace that didn't change costs no UI work
/// at all. It used to rebuild every visible row on each refresh (most of an Explorer refresh's time).
/// </summary>
public sealed class ExplorerItemReuse
{
    private readonly Dictionary<string, ExplorerItemViewModel> _items = new(StringComparer.Ordinal);

    public ExplorerItemReuse(IEnumerable<ExplorerItemViewModel> roots) => Collect(roots);

    private void Collect(IEnumerable<ExplorerItemViewModel> items)
    {
        foreach (var item in items)
        {
            if (item.IsPlaceholder || item.IsExternalGroup) continue;
            _items[Key(item.IsDirectory, item.FullPath, item.DocumentId)] = item;
            if (item.IsDirectory) Collect(item.Children);
        }
    }

    private static string Key(bool isDirectory, string path, string? documentId) =>
        isDirectory ? "d|" + path : "f|" + path + "|" + documentId;

    /// <summary>The folder at <paramref name="fullPath"/> from before (emptied, under its new parent), or a new one.</summary>
    public ExplorerItemViewModel Folder(string fullPath, ExplorerItemViewModel? parent, Func<ExplorerItemViewModel> create)
    {
        if (!_items.Remove(Key(true, fullPath, null), out var folder)) return create();
        Place(folder, parent);
        folder.Children.Clear();
        return folder;
    }

    /// <summary>The file at <paramref name="fullPath"/> showing <paramref name="documentId"/> from before, or a new one.</summary>
    public ExplorerItemViewModel File(string fullPath, string? documentId, string name, ExplorerItemViewModel? parent, Func<ExplorerItemViewModel> create)
    {
        if (!_items.Remove(Key(false, fullPath, documentId), out var file) || file.Name != name || file.IsRenaming) return create();
        Place(file, parent);
        return file;
    }

    private static void Place(ExplorerItemViewModel item, ExplorerItemViewModel? parent)
    {
        item.Parent = parent;
        item.Depth = (parent?.Depth ?? -1) + 1;
    }
}
