using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels;
using CSharpManagerViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.Hub.CSharpManagerViewModel;

namespace PdfEditorApp.Plugins.CSharpEditor.Views;

public partial class CSharpManagerView : UserControl
{
    public CSharpManagerView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnFileDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) && e.Key == Key.O)
        {
            e.Handled = true;
            if (DataContext is CSharpManagerViewModel vm)
            {
                _ = HubFilePickerActions.OpenProjectFileAsync(this, vm);
            }
        }
        else if ((e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) && e.Key == Key.K)
        {
            // The search box's badge promises this shortcut.
            e.Handled = true;
            this.FindDescendantOfType<PdfEditorApp.Plugins.CSharpEditor.Controls.Hub.HubTopHeaderControl>()?.FocusSearch();
        }
        else if ((e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) && (e.Key == Key.OemComma || e.Key == Key.Oem1))
        {
            e.Handled = true;
            if (DataContext is CSharpManagerViewModel vm)
            {
                vm.NavigateToSettings();
            }
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not CSharpManagerViewModel vm) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files == null) return;

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } path)
            {
                await vm.OpenExistingProjectAsync(path);
                break;
            }
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (DataContext is CSharpManagerViewModel vm && e.NewSize.Width > 0)
        {
            vm.UpdateAdaptiveRail(e.NewSize.Width);
        }
    }
}
