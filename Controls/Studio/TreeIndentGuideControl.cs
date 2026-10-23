using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PdfEditorApp.Plugins.CSharpEditor.Controls.Studio;

/// <summary>
/// Lightweight control that renders vertical tree indentation guide lines directly on the drawing context.
/// Each line aligns at the horizontal center of an ancestor indentation level.
/// </summary>
public class TreeIndentGuideControl : Control
{
    public static readonly StyledProperty<int> DepthProperty =
        AvaloniaProperty.Register<TreeIndentGuideControl, int>(nameof(Depth));

    public static readonly StyledProperty<double> IndentWidthProperty =
        AvaloniaProperty.Register<TreeIndentGuideControl, double>(nameof(IndentWidth), 16.0);

    public static readonly StyledProperty<IBrush?> GuideBrushProperty =
        AvaloniaProperty.Register<TreeIndentGuideControl, IBrush?>(nameof(GuideBrush));

    static TreeIndentGuideControl()
    {
        AffectsRender<TreeIndentGuideControl>(DepthProperty, IndentWidthProperty, GuideBrushProperty);
        AffectsMeasure<TreeIndentGuideControl>(DepthProperty, IndentWidthProperty);
    }

    public int Depth
    {
        get => GetValue(DepthProperty);
        set => SetValue(DepthProperty, value);
    }

    public double IndentWidth
    {
        get => GetValue(IndentWidthProperty);
        set => SetValue(IndentWidthProperty, value);
    }

    public IBrush? GuideBrush
    {
        get => GetValue(GuideBrushProperty);
        set => SetValue(GuideBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var depth = Depth;
        return depth <= 0 ? new Size(0, 0) : new Size(depth * IndentWidth, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var depth = Depth;
        return depth <= 0 ? new Size(0, finalSize.Height) : new Size(depth * IndentWidth, finalSize.Height);
    }

    public override void Render(DrawingContext context)
    {
        var depth = Depth;
        if (depth <= 0) return;

        var brush = GuideBrush;
        if (brush == null) return;

        var pen = new Pen(brush, 1.0);
        var h = Bounds.Height;
        var w = IndentWidth;

        for (int i = 0; i < depth; i++)
        {
            // Center the 1px line in the indent column (x.5 for sharp 1px rendering)
            double x = Math.Floor(i * w + (w / 2.0)) + 0.5;
            context.DrawLine(pen, new Point(x, 0), new Point(x, h));
        }
    }
}
