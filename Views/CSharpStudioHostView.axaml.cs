using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PdfEditorApp.Plugins.CSharpEditor.Controls;
using PdfEditorApp.Plugins.CSharpEditor.Controls.Common;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels;
using CSharpBlindProblemsViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.BlindProblems.CSharpBlindProblemsViewModel;
using CSharpCodeStudioViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.CSharpCodeStudioViewModel;
using CSharpDocsViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.Docs.CSharpDocsViewModel;
using CSharpManagerViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.Hub.CSharpManagerViewModel;
using CSharpNotebookStudioViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks.CSharpNotebookStudioViewModel;
using CSharpSettingsViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.Settings.CSharpSettingsViewModel;
using CSharpStudioHostViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common.CSharpStudioHostViewModel;

namespace PdfEditorApp.Plugins.CSharpEditor.Views;

public partial class CSharpStudioHostView : UserControl, IDisposable
{
    private readonly KeepAlivePageHost _pages;

    public CSharpStudioHostView()
    {
        InitializeComponent();
        StudioStyleSheet.IncludeUnlessApplicationHasIt(this);
        AddHandler(KeyDownEvent, OnHostKeyDown, RoutingStrategies.Tunnel);

        // The studio's palette is included here, once; the theme engine shows its theme layer on top of it here.
        PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.ThemeEngine.TrackResourceRoot(this);

        _pages = this.FindControl<KeepAlivePageHost>("PageHost")!;
        _pages.Register<CSharpManagerViewModel>(() => new CSharpManagerView());
        _pages.Register<CSharpCodeStudioViewModel>(() => new CSharpCodeStudioView());
        _pages.Register<CSharpNotebookStudioViewModel>(() => new CSharpNotebookStudioView());
        _pages.Register<CSharpDocsViewModel>(() => new CSharpDocsView());
        _pages.Register<CSharpBlindProblemsViewModel>(() => new CSharpBlindProblemsView());
        _pages.Register<CSharpSettingsViewModel>(() => new CSharpSettingsView());
        _pages.Register<PdfEditorApp.Plugins.CSharpEditor.ViewModels.Server.FryServerStudioViewModel>(() => new PdfEditorApp.Plugins.CSharpEditor.Views.Server.FryServerStudioView());
    }

    /// <summary>The page views, built lazily and kept alive (exposed so tests and tooling can count them).</summary>
    public KeepAlivePageHost Pages => _pages;

    /// <summary>Releases every page view; the studio is going away for good.</summary>
    public void Dispose()
    {
        _stallMonitor?.Dispose();
        _stallMonitor = null;
        _pages.Dispose();
    }

    // Debug builds report every UI-thread stall over 200 ms to the debug log, with the work that was running.
    private UiStallMonitor? _stallMonitor;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
#if DEBUG
        if (_stallMonitor == null && DataContext is CSharpStudioHostViewModel host) _stallMonitor = new UiStallMonitor(host.Activities);
#endif
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _stallMonitor?.Dispose();
        _stallMonitor = null;
    }

    private void OnHostKeyDown(object? sender, KeyEventArgs e)
    {
        // While a question is open the page below gets no keys: Escape cancels and Enter answers even when the focus is
        // still in an editor, Tab and Space move between and press the dialog's buttons.
        if (DataContext is CSharpStudioHostViewModel { Dialogs.IsOpen: true } host)
        {
            var insideDialog = e.Source is Avalonia.Visual source && this.FindControl<Control>("DialogOverlay") is { } overlay &&
                               (ReferenceEquals(source, overlay) || overlay.IsVisualAncestorOf(source));
            if (e.Key == Key.Escape) host.Dialogs.Dismiss();
            else if (e.Key == Key.Enter && !(insideDialog && e.Source is Button)) host.Dialogs.AcceptDefault();
            else if (insideDialog && e.Key is Key.Tab or Key.Space or Key.Enter) return;
            e.Handled = true;
            return;
        }

        var isModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (isModifier && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && (e.Key == Key.OemComma || e.Key == Key.Oem1))
        {
            if (DataContext is CSharpStudioHostViewModel hostVm)
            {
                hostVm.NavigateToSettings();
                e.Handled = true;
            }
        }
    }
}
