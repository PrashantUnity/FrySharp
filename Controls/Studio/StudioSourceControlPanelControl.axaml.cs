using Avalonia.Controls;
using Avalonia.Input;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Git;

namespace PdfEditorApp.Plugins.CSharpEditor.Controls.Studio;

public partial class StudioSourceControlPanelControl : UserControl
{
    public StudioSourceControlPanelControl()
    {
        InitializeComponent();
    }

    private void OnCommitBoxKeyDown(object? sender, KeyEventArgs e)
    {
        // ⌘Enter on macOS or Ctrl+Enter on Windows/Linux triggers Commit
        bool isModifier = OperatingSystem.IsMacOS()
            ? e.KeyModifiers.HasFlag(KeyModifiers.Meta)
            : e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (isModifier && e.Key == Key.Return)
        {
            if (DataContext is IGitStudioHostViewModel vm && vm.CommitCommand.CanExecute(null))
            {
                vm.CommitCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
