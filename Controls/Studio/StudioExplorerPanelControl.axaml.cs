using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;
using ExplorerItemViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer.ExplorerItemViewModel;

namespace PdfEditorApp.Plugins.CSharpEditor.Controls.Studio;

public partial class StudioExplorerPanelControl : UserControl
{
    // Marks the menu entries added for each source-file language, so they're replaced rather than repeated.
    private const string NewFileEntryTag = "new-source-file";

    public event EventHandler<RoutedEventArgs>? OpenProjectRequested;

    public StudioExplorerPanelControl()
    {
        InitializeComponent();
    }

    private void OnRenameTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ExplorerItemViewModel vm && vm.IsRenaming)
        {
            vm.CommitRename();
        }
    }

    private void OnOpenProjectMenuClick(object? sender, RoutedEventArgs e)
    {
        OpenProjectRequested?.Invoke(this, e);
    }

    // The Explorer's own menu: "New Python File" and friends after "New Script", for the selected folder.
    private void OnRootMenuOpening(object? sender, EventArgs e)
    {
        if (sender is MenuFlyout menu) AddNewFileEntries(menu, target: null, afterIndex: 0);
    }

    // A folder's menu: the same entries after "New Server Here", creating the file in that folder.
    private void OnItemMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu || menu.Target?.DataContext is not ExplorerItemViewModel item) return;
        AddNewFileEntries(menu, item.IsManageableDirectory ? item : null, afterIndex: 3, show: item.IsManageableDirectory);
    }

    private void AddNewFileEntries(MenuFlyout menu, ExplorerItemViewModel? target, int afterIndex, bool show = true)
    {
        foreach (var old in menu.Items.OfType<MenuItem>().Where(m => Equals(m.Tag, NewFileEntryTag)).ToList())
        {
            menu.Items.Remove(old);
        }

        if (!show || DataContext is not IExplorerNewFileHost host) return;

        var index = Math.Min(afterIndex + 1, menu.Items.Count);
        foreach (var option in host.NewFileOptions)
        {
            var icon = new MaterialIcon { Width = 14, Height = 14 };
            if (Enum.TryParse<MaterialIconKind>(option.IconKind, out var kind)) icon.Kind = kind;
            if (Color.TryParse(option.AccentHex, out var color)) icon.Foreground = new SolidColorBrush(color);

            menu.Items.Insert(index++, new MenuItem
            {
                Header = target == null ? option.Label : option.Label + " Here",
                Icon = icon,
                Command = option.Command,
                CommandParameter = target,
                Tag = NewFileEntryTag
            });
        }
    }

    private void OnExplorerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;

        ExplorerRowList? rowsList = DataContext switch
        {
            PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.CSharpCodeStudioViewModel cs => cs.ExplorerRows,
            PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks.CSharpNotebookStudioViewModel nb => nb.ExplorerRows,
            StudioWorkspaceExplorer swe => swe.ExplorerRows,
            _ => null
        };

        if (rowsList == null || rowsList.Rows.Count == 0) return;

        var rows = rowsList.Rows;
        var selectedIndex = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].IsSelected)
            {
                selectedIndex = i;
                break;
            }
        }

        var current = selectedIndex >= 0 ? rows[selectedIndex] : null;

        switch (e.Key)
        {
            case Key.Down:
                var nextIndex = selectedIndex < 0 ? 0 : Math.Min(selectedIndex + 1, rows.Count - 1);
                SelectRow(rows[nextIndex]);
                e.Handled = true;
                break;

            case Key.Up:
                var prevIndex = selectedIndex < 0 ? 0 : Math.Max(selectedIndex - 1, 0);
                SelectRow(rows[prevIndex]);
                e.Handled = true;
                break;

            case Key.Right:
                if (current != null && current.IsDirectory)
                {
                    if (!current.IsExpanded)
                    {
                        current.IsExpanded = true;
                    }
                    else if (selectedIndex + 1 < rows.Count && rows[selectedIndex + 1].Depth > current.Depth)
                    {
                        SelectRow(rows[selectedIndex + 1]);
                    }
                    e.Handled = true;
                }
                break;

            case Key.Left:
                if (current != null)
                {
                    if (current.IsDirectory && current.IsExpanded)
                    {
                        current.IsExpanded = false;
                        e.Handled = true;
                    }
                    else if (current.Parent != null)
                    {
                        SelectRow(current.Parent);
                        e.Handled = true;
                    }
                }
                break;

            case Key.Enter:
                if (current != null)
                {
                    current.ToggleExpand();
                    e.Handled = true;
                }
                break;

            case Key.F2:
                if (current != null && !current.IsPlaceholder)
                {
                    current.StartRename();
                    e.Handled = true;
                }
                break;

            case Key.Delete or Key.Back when OperatingSystem.IsMacOS() && (e.KeyModifiers & KeyModifiers.Meta) != 0:
            case Key.Delete:
                if (current != null && !current.IsPlaceholder)
                {
                    current.RequestDelete();
                    e.Handled = true;
                }
                break;
        }

        void SelectRow(ExplorerItemViewModel item)
        {
            if (item.IsPlaceholder) return;
            item.Select();
        }
    }
}
