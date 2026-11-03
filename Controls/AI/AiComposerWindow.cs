using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FrySharp.Sdk;
using PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.UI;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.AI;

namespace PdfEditorApp.Plugins.CSharpEditor.Controls.AI;

/// <summary>
/// Native desktop OS window hosting the AI Assistant when extracted ("torn out")
/// from the main IDE workspace. Supports multi-monitor setups, topmost pinning, and re-docking.
/// </summary>
public class AiComposerWindow : Window
{
    private static AiComposerWindow? _activeInstance;
    private readonly AiComposerViewModel _viewModel;

    public static AiComposerWindow? ActiveInstance => _activeInstance;

    public AiComposerWindow(AiComposerViewModel viewModel, AiWindowOptions? options = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        Title = options?.Title ?? "Fry AI Studio Assistant";
        Width = options?.Width ?? Math.Max(540, _viewModel.WindowWidth);
        Height = options?.Height ?? Math.Max(650, _viewModel.WindowHeight);
        MinWidth = 380;
        MinHeight = 420;
        CanResize = true;
        Topmost = options?.Topmost ?? false;
        WindowStartupLocation = options?.CenterScreen == false ? WindowStartupLocation.Manual : WindowStartupLocation.CenterScreen;

        Background = new SolidColorBrush(Color.Parse("#161622"));
        Opacity = _viewModel.WindowOpacity;

        _viewModel.StyleChanged += OnStyleChanged;
        _viewModel.CaptureProtectionChanged += OnCaptureProtectionChanged;

        Opened += (_, _) =>
        {
            if (_viewModel.IsProtectedFromCapture)
            {
                WindowProtectionService.SetProtected(this, true);
            }
        };

        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal && Bounds.Width > 0 && Bounds.Height > 0)
            {
                _viewModel.WindowWidth = Math.Round(Bounds.Width);
                _viewModel.WindowHeight = Math.Round(Bounds.Height);
            }
        };

        var composerControl = new StudioFloatingComposerControl
        {
            DataContext = _viewModel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // A window of its own is outside the studio host: it needs the studio's styles unless the application has them.
        PdfEditorApp.Plugins.CSharpEditor.Controls.Common.StudioStyleSheet.IncludeUnlessApplicationHasIt(this);
        Content = composerControl;
        // The layout's interface font and body weight, inherited by everything in the window (as in the studio host).
        PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.Theming.Layout.Tokens.SetFontFamily(this, "DsUiFontFamily");
        PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.Theming.Layout.Tokens.SetFontWeight(this, "DsWeightBody");
        PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.ThemeEngine.TrackResourceRoot(this);
    }

    private void OnCaptureProtectionChanged(bool isProtected)
    {
        Dispatcher.UIThread.Post(() =>
        {
            WindowProtectionService.SetProtected(this, isProtected);
        });
    }

    private void OnStyleChanged(AiStyleOptions style)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (style.Opacity.HasValue) Opacity = style.Opacity.Value;
            if (style.Width.HasValue) Width = style.Width.Value;
            if (style.Height.HasValue) Height = style.Height.Value;
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _viewModel.StyleChanged -= OnStyleChanged;
        _viewModel.CaptureProtectionChanged -= OnCaptureProtectionChanged;

        if (_activeInstance == this)
        {
            _activeInstance = null;
        }

        if (_viewModel.IsExtracted)
        {
            _viewModel.ReDock(AiDockMode.FloatingOverlay);
        }
    }

    public static void ShowExtracted(AiComposerViewModel vm, AiWindowOptions? options = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_activeInstance != null)
            {
                _activeInstance.Activate();
                return;
            }

            var win = new AiComposerWindow(vm, options);
            _activeInstance = win;
            win.Show();
        });
    }

    public static void CloseExtracted(AiComposerViewModel vm)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_activeInstance != null)
            {
                var inst = _activeInstance;
                _activeInstance = null;
                inst.Close();
            }
        });
    }
}
