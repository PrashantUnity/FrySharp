using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;

/// <summary>Whatever an Explorer panel shows: its visible rows (keyboard navigation walks them).</summary>
public interface IExplorerRowsHost
{
    ExplorerRowList ExplorerRows { get; }
}

/// <summary>
/// The Explorer's visible rows as one flat list: every item of the tree, depth first, leaving out what a collapsed folder
/// hides. It is what the Explorer binds to, so a virtualizing list only ever builds the rows on screen (a workspace with
/// thousands of files costs a screenful of controls, not thousands), instead of one nested control per item.
/// The list follows the tree by itself: adding, removing or re-sorting items rebuilds it, and expanding or collapsing a
/// folder inserts or removes just that folder's rows.
/// </summary>
public sealed class ExplorerRowList
{
    private readonly ObservableCollection<ExplorerItemViewModel> _roots;
    private int _suspended;
    private int _patching;
    private bool _dirty;

    public ExplorerRowList(ObservableCollection<ExplorerItemViewModel> roots)
    {
        _roots = roots;
        Watch(roots);
        Rebuild();
    }

    /// <summary>The rows to show, in order. Indentation comes from each item's own depth.</summary>
    public RangeObservableCollection<ExplorerItemViewModel> Rows { get; } = new();

    /// <summary>
    /// Holds off updates while the tree is changed in bulk (a rebuild, a sort); one rebuild follows when the returned scope
    /// is disposed. Without it, each of thousands of single changes would rebuild the list.
    /// </summary>
    public IDisposable Suspend()
    {
        _suspended++;
        return new Resume(this);
    }

    /// <summary>
    /// Changes what a folder holds (<paramref name="change"/> edits <c>folder.Children</c>) and patches the rows for it: the folder's
    /// old rows are swapped for its new ones and every other row stays as it is. A rebuild of the whole list would make the
    /// list control drop and recreate the rows on screen, which is what opening a folder in a big workspace must not do.
    /// </summary>
    public void ChangeChildren(ExplorerItemViewModel folder, Action change)
    {
        var index = folder.IsExpanded ? Rows.IndexOf(folder) : -1;
        var oldCount = 0;
        if (index >= 0)
        {
            while (index + 1 + oldCount < Rows.Count && Rows[index + 1 + oldCount].Depth > folder.Depth) oldCount++;
        }

        _patching++;
        try
        {
            change();
        }
        finally
        {
            _patching--;
        }

        // Hidden (collapsed, or under a collapsed folder): its rows appear when it is expanded.
        if (index < 0) return;

        var rows = new List<ExplorerItemViewModel>();
        Flatten(folder.Children, rows);
        if (oldCount > 0) Rows.RemoveRange(index + 1, oldCount);
        if (rows.Count > 0) Rows.InsertRange(index + 1, rows);
    }

    private sealed class Resume(ExplorerRowList owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner.EndSuspend();
        }
    }

    private void EndSuspend()
    {
        if (--_suspended == 0 && _dirty) Rebuild();
    }

    private void Invalidate()
    {
        if (_patching > 0) return;
        if (_suspended > 0)
        {
            _dirty = true;
            return;
        }

        Rebuild();
    }

    private void Rebuild()
    {
        _dirty = false;
        var rows = new List<ExplorerItemViewModel>();
        Flatten(_roots, rows);
        Apply(rows);
    }

    // The new rows replace the old ones only where they differ: the rows both lists start and end with stay, and so do
    // the controls on screen for them (re-creating every visible row was most of an Explorer refresh's time). The same
    // rows in the same order (a refresh of a workspace that didn't change) raise nothing.
    private void Apply(List<ExplorerItemViewModel> rows)
    {
        var max = Math.Min(Rows.Count, rows.Count);
        var prefix = 0;
        while (prefix < max && ReferenceEquals(Rows[prefix], rows[prefix])) prefix++;
        if (prefix == Rows.Count && prefix == rows.Count) return;

        var suffix = 0;
        while (suffix < max - prefix && ReferenceEquals(Rows[Rows.Count - 1 - suffix], rows[rows.Count - 1 - suffix])) suffix++;

        // Nothing in common: one reset is cheaper than a removal and an insertion.
        if (prefix == 0 && suffix == 0)
        {
            Rows.ReplaceAll(rows);
            return;
        }

        var removed = Rows.Count - prefix - suffix;
        var inserted = rows.Count - prefix - suffix;
        if (removed > 0) Rows.RemoveRange(prefix, removed);
        if (inserted > 0) Rows.InsertRange(prefix, rows.GetRange(prefix, inserted));
    }

    private static void Flatten(IEnumerable<ExplorerItemViewModel> items, List<ExplorerItemViewModel> into)
    {
        foreach (var item in items)
        {
            into.Add(item);
            if (item.IsDirectory && item.IsExpanded) Flatten(item.Children, into);
        }
    }

    // Unsubscribe first: the same collection or item is watched again when a sort takes it out and puts it back.
    private void Watch(ObservableCollection<ExplorerItemViewModel> items)
    {
        items.CollectionChanged -= OnItemsChanged;
        items.CollectionChanged += OnItemsChanged;
        foreach (var item in items) WatchItem(item);
    }

    private void WatchItem(ExplorerItemViewModel item)
    {
        if (!item.IsDirectory) return;
        item.PropertyChanged -= OnItemPropertyChanged;
        item.PropertyChanged += OnItemPropertyChanged;
        Watch(item.Children);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (ExplorerItemViewModel item in e.NewItems) WatchItem(item);
        }

        Invalidate();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ExplorerItemViewModel.IsExpanded) || sender is not ExplorerItemViewModel folder) return;
        if (_patching > 0) return;

        if (_suspended > 0)
        {
            _dirty = true;
            return;
        }

        var index = Rows.IndexOf(folder);
        if (index < 0) return; // Hidden by a collapsed ancestor: its own rows appear when that ancestor is expanded.

        if (folder.IsExpanded)
        {
            var children = new List<ExplorerItemViewModel>();
            Flatten(folder.Children, children);
            Rows.InsertRange(index + 1, children);
        }
        else
        {
            var count = 0;
            while (index + 1 + count < Rows.Count && Rows[index + 1 + count].Depth > folder.Depth) count++;
            Rows.RemoveRange(index + 1, count);
        }
    }
}
