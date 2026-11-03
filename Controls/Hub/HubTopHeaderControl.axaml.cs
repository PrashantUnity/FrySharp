using Avalonia.Controls;
using Avalonia.Input;

namespace PdfEditorApp.Plugins.CSharpEditor.Controls.Hub;

public partial class HubTopHeaderControl : UserControl
{
    public HubTopHeaderControl()
    {
        InitializeComponent();
        // The badge shows the shortcut as this machine's keyboard has it.
        if (OperatingSystem.IsMacOS()) this.FindControl<TextBlock>("ShortcutModifier")!.Text = "⌘";
    }

    /// <summary>Ctrl+K (⌘K): puts the caret in the search box, its text selected.</summary>
    public void FocusSearch()
    {
        if (this.FindControl<TextBox>("SearchBox") is not { } box) return;
        box.Focus(NavigationMethod.Pointer);
        box.SelectAll();
    }
}
