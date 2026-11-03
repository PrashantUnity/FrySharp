using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Material.Icons;
using PdfEditorApp.Plugins.CSharpEditor.Controls;
using PdfEditorApp.Plugins.CSharpEditor.Controls.Common;
using PdfEditorApp.Plugins.CSharpEditor.Controls.Visuals;
using PdfEditorApp.Plugins.CSharpEditor.Visualizers.Models;
using PdfEditorApp.Plugins.CSharpEditor.Visualizers.Services;
using PdfEditorApp.Plugins.CSharpEditor.Visuals.Building;

namespace PdfEditorApp.Plugins.CSharpEditor.Visualizers.Controls;

public partial class InteractiveVisualizerControl : UserControl
{
    private const double ZoomStep = 1.2;
    private const double MinCanvasHeight = 80;
    private const double MaxCanvasHeight = 1200;
    private const double DefaultCanvasHeight = 200;

    public static readonly StyledProperty<VisualizerOptions?> OptionsProperty =
        AvaloniaProperty.Register<InteractiveVisualizerControl, VisualizerOptions?>(nameof(Options));

    public static readonly RoutedEvent<VisualizerStepLineEventArgs> StepSourceLineChangedEvent =
        RoutedEvent.Register<InteractiveVisualizerControl, VisualizerStepLineEventArgs>("StepSourceLineChanged", RoutingStrategies.Bubble);

    private VisualizerSequence? _observedSequence;
    private bool _isFullScreenView;

    public VisualizerOptions? Options { get => GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }
    public VisualizerViewState ViewState { get; set; } = new();
    public bool IsFullScreenView
    {
        get => _isFullScreenView;
        set
        {
            _isFullScreenView = value;
            ApplyViewMode();
        }
    }

    public event EventHandler? ExitFullScreenRequested;
    public event EventHandler<ElementClickedEventArgs<VisualizerHitTestResult>>? ElementClicked;

    private T? Find<T>(string name) where T : Control { try { return this.FindControl<T>(name); } catch { return null; } }
    private VisualizerCanvasControl? Canvas => Find<VisualizerCanvasControl>("CanvasControl");

    static InteractiveVisualizerControl() =>
        OptionsProperty.Changed.AddClassHandler<InteractiveVisualizerControl>((c, _) => c.ApplyOptions());

    public InteractiveVisualizerControl()
    {
        // Inside the studio its styles are already there; a window of its own gets them when it joins one.
        PdfEditorApp.Plugins.CSharpEditor.Controls.Common.StudioStyleSheet.IncludeWhereMissing(this);
        try { InitializeComponent(); SetupChrome(); WireEvents(); ApplyViewMode(); } catch { }
    }
    public InteractiveVisualizerControl(VisualizerOptions options) : this() { Options = options; ApplyOptions(); }

    private void SetupChrome()
    {
        if (Find<VisualChromeControl>("Chrome") is not { } chrome) return;
        if (Find<StackPanel>("KindToolsPanel") is { } tools) { tools.IsVisible = true; chrome.SetKindTools(tools); }
        if (Find<Border>("CanvasHost") is { } host) { host.IsVisible = true; chrome.SetCanvasContent(host); }
        if (Find<VisualizerPlaybackControl>("PlaybackControl") is { } play) chrome.SetFooter(play);
        chrome.SpecGetter = () => Options != null ? VisualizerOptionsConverter.ToSpec(Canvas?.GetEffectiveOptions() ?? Options) : null;
        chrome.DataCsvGetter = () => Options != null ? VisualizerExportService.ToCsv(Canvas?.GetEffectiveOptions() ?? Options) : null;
        chrome.CustomGifSaver = async () => await ExportGifAsync();
        chrome.ResetFitRequested += (_, _) => { Canvas?.FitToView(); UpdateZoomLevel(); };
        chrome.FullscreenRequested += (_, _) => ToggleFullScreen();
    }

    private void WireEvents()
    {
        if (Canvas is { } canvas) { canvas.ElementClicked += (_, e) => ElementClicked?.Invoke(this, e); canvas.ViewportChanged += (_, _) => UpdateZoomLevel(); }
        Bind("ZoomInBtn", () => Canvas?.ZoomBy(ZoomStep));
        Bind("ZoomOutBtn", () => Canvas?.ZoomBy(1 / ZoomStep));
        Bind("ResetViewBtn", () => Canvas?.ResetView());
        Bind("ToggleValuesBtn", () => { if (Options != null) { ViewState.OverrideShowValues = !ViewState.EffectiveShowValues(Options); Canvas?.InvalidateVisual(); } });
        Bind("ToggleCoordsBtn", () => { if (Options != null) { ViewState.OverrideShowCoordinates = !ViewState.EffectiveShowCoordinates(Options); Canvas?.InvalidateVisual(); } });
    }

    private void Bind(string name, Action act) { if (Find<Button>(name) is { } b) b.Click += (_, _) => act(); }

    public double GetInlineCanvasHeight() => InlineCanvasHeight();

    private double InlineCanvasHeight() =>
        Options?.Height is { } height && height > 0 ? Math.Clamp(height, MinCanvasHeight, MaxCanvasHeight) : GetDefaultCanvasHeightForKind();

    private double GetDefaultCanvasHeightForKind()
    {
        if (Options == null) return DefaultCanvasHeight;
        return Options.Kind switch
        {
            VisualizerKind.ArrayPointers => 125,
            VisualizerKind.LinkedList => 125,
            VisualizerKind.Bars => 140,
            VisualizerKind.Tree => 220,
            VisualizerKind.Graph => 230,
            VisualizerKind.Matrix => 210,
            VisualizerKind.Islands => 210,
            VisualizerKind.Board => 220,
            VisualizerKind.Canvas => 240,
            _ => DefaultCanvasHeight
        };
    }

    private void ApplyViewMode()
    {
        VerticalAlignment = IsFullScreenView ? VerticalAlignment.Stretch : VerticalAlignment.Top;

        if (Find<Border>("CanvasHost") is { } host)
        {
            host.Height = IsFullScreenView ? double.NaN : InlineCanvasHeight();
            host.VerticalAlignment = IsFullScreenView ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        }

        if (Find<VisualChromeControl>("Chrome") is { } chrome)
        {
            chrome.VerticalAlignment = IsFullScreenView ? VerticalAlignment.Stretch : VerticalAlignment.Top;
            chrome.SetFullScreenState(IsFullScreenView);
        }
    }

    public void ApplyOptions()
    {
        var opts = Options;
        if (opts == null) return;
        if (Canvas is { } canvas) { canvas.Options = opts; canvas.ViewState = ViewState; }
        if (Find<VisualChromeControl>("Chrome") is { } chrome)
        {
            chrome.DefaultCanvasHeight = InlineCanvasHeight();
            chrome.SupportsGifExport = opts.Sequence is { HasSteps: true };
        }
        ApplyViewMode();
        if (Find<VisualizerPlaybackControl>("PlaybackControl") is { } play) { play.Sequence = opts.Sequence; play.IsVisible = opts.Sequence is { HasSteps: true }; }
        if (Find<StackPanel>("MatrixToolsPanel") is { } mt) mt.IsVisible = opts.MatrixData != null;
        UpdateHeader();
        UpdateZoomLevel();
        ObserveSequence(VisualRoot != null ? opts.Sequence : null);
    }

    private void UpdateHeader()
    {
        var opts = Options;
        if (opts == null) return;
        var icon = opts.Kind switch { VisualizerKind.Matrix => MaterialIconKind.Grid, VisualizerKind.Islands => MaterialIconKind.Island, VisualizerKind.Tree => MaterialIconKind.FamilyTree, VisualizerKind.Graph => MaterialIconKind.Graph, VisualizerKind.LinkedList => MaterialIconKind.VectorLink, VisualizerKind.ArrayPointers => MaterialIconKind.FormatListNumbered, _ => MaterialIconKind.CodeBraces };
        Find<VisualChromeControl>("Chrome")?.SetHeader(string.IsNullOrEmpty(opts.Title) ? "Data Structure Visualizer" : opts.Title, opts.Subtitle, opts.GetSummaryText(), opts.Notice, icon);
    }

    private void UpdateZoomLevel()
    {
        if (Find<TextBlock>("ZoomLevelText") is { } text) text.Text = $"{Math.Round((Canvas?.ViewState.Zoom ?? 1.0) * 100)}%";
    }

    public void ToggleFullScreen() { if (IsFullScreenView) ExitFullScreenRequested?.Invoke(this, EventArgs.Empty); else VisualizerFullScreenOverlay.Open(this); }
    public void FitToView() => Canvas?.FitToView();
    public void FocusCanvas() => Canvas?.Focus();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) return;
        var seq = Options?.Sequence is { HasSteps: true } s ? s : null;
        e.Handled = e.Key switch
        {
            Key.Left when seq != null => Step(() => seq.PrevStep()),
            Key.Right when seq != null => Step(() => seq.NextStep()),
            Key.Home when seq != null => Step(() => seq.FirstStep()),
            Key.End when seq != null => Step(() => seq.LastStep()),
            Key.Space when seq != null => Step(() => seq.TogglePlay()),
            Key.OemPlus or Key.Add => Step(() => Canvas?.ZoomBy(ZoomStep)),
            Key.OemMinus or Key.Subtract => Step(() => Canvas?.ZoomBy(1 / ZoomStep)),
            Key.D0 or Key.NumPad0 => Step(() => Canvas?.ResetView()),
            Key.F => Step(() => Canvas?.FitToView()),
            Key.Escape when IsFullScreenView => Step(() => ExitFullScreenRequested?.Invoke(this, EventArgs.Empty)),
            _ => false
        };
    }

    private static bool Step(Action act) { act(); return true; }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); ObserveSequence(Options?.Sequence); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); ObserveSequence(null); }

    private void ObserveSequence(VisualizerSequence? sequence)
    {
        if (ReferenceEquals(_observedSequence, sequence)) return;
        if (_observedSequence != null) _observedSequence.StepChanged -= OnSequenceStepChanged;
        _observedSequence = sequence;
        if (_observedSequence != null) _observedSequence.StepChanged += OnSequenceStepChanged;
    }

    private void OnSequenceStepChanged(object? sender, int stepIndex)
    {
        var step = _observedSequence?.CurrentStep;
        RaiseEvent(new VisualizerStepLineEventArgs(step?.SourceLine ?? 0, step?.SourceFile, reveal: false));
    }

    public async Task ExportGifAsync()
    {
        var seq = Options?.Sequence;
        if (seq == null || !seq.HasSteps) return;

        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is { CanSave: true } sp)
        {
            string rawTitle = !string.IsNullOrWhiteSpace(Options?.Title) ? Options.Title : "visualizer";
            string safeTitle = string.Concat(rawTitle.Split(System.IO.Path.GetInvalidFileNameChars())).Replace(" ", "_");

            var file = await sp.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Export Visualizer Animation as GIF",
                DefaultExtension = "gif",
                SuggestedFileName = $"{safeTitle}.gif",
                FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("Animated GIF (*.gif)") { Patterns = ["*.gif"] }]
            });

            if (file != null)
            {
                var eff = Canvas?.GetEffectiveOptions() ?? Options!;
                int w = (int)Math.Max(320, Canvas?.Bounds.Width ?? 640);
                int h = (int)Math.Max(200, Canvas?.Bounds.Height ?? 360);
                var exportOptions = new VisualizerGifExportOptions
                {
                    Width = w,
                    Height = h,
                    IncludeBanner = true
                };

                await using var stream = await file.OpenWriteAsync();
                byte[] bytes = await VisualizerGifExportService.ExportToGifBytesAsync(eff, exportOptions);
                await stream.WriteAsync(bytes);
            }
        }
    }
}
