using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;

namespace PdfEditorApp.Plugins.CSharpEditor.Controls.Overlays;

/// <summary>Draws the studio's modal question (<see cref="StudioDialogViewModel"/>): Enter answers with the primary button, Escape cancels.</summary>
public partial class StudioDialogOverlayControl : UserControl
{
    private StudioDialogViewModel? _vm;

    public StudioDialogOverlayControl()
    {
        InitializeComponent();
        // Tunnelling, so the editor or page below never sees the keys while a question is open.
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as StudioDialogViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StudioDialogViewModel.IsOpen) || _vm is not { IsOpen: true }) return;

        // The buttons are built by the next layout pass; focus the primary one then, so Enter and Tab start there.
        Dispatcher.UIThread.Post(() =>
        {
            var buttons = this.FindControl<ItemsControl>("ChoiceButtons")?.GetVisualDescendants().OfType<Button>().ToList();
            var primary = buttons?.FirstOrDefault(b => b.Classes.Contains("primary")) ?? buttons?.FirstOrDefault();
            primary?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { IsOpen: true }) return;

        switch (e.Key)
        {
            case Key.Escape:
                _vm.Dismiss();
                e.Handled = true;
                break;
            case Key.Enter when e.Source is not Button:
                _vm.AcceptDefault();
                e.Handled = true;
                break;
        }
    }

    // A click outside the card does nothing: the question needs an answer (as in VS Code).
    private void OnScrimPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}
