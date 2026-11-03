using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;

namespace PdfEditorApp.Plugins.CSharpEditor.Controls.Common;

/// <summary>
/// Where the studio's styles (Styles/StudioStyles.axaml, AvaloniaEdit's theme included) come from. They must apply once on
/// any branch of the tree: Avalonia gives every control an instance of each style of its type in each copy, matched or
/// not, and the Runner had the bundle twice (the application and the studio host), so a notebook cell's Button carried 263
/// style instances and every control cost twice what it should to build and to keep.
/// The Runner applies the bundle to the whole application; inside FryPDF, which has none of it, the studio host includes
/// it for its own tree. Windows and controls that can also be shown on their own include it only where it is missing.
/// </summary>
public static class StudioStyleSheet
{
    /// <summary>The resource the bundle declares, by which a tree tells it already has it.</summary>
    public const string MarkerKey = "StudioStylesIncluded";

    private static StyleInclude Create() => new(new Uri("avares://CSharpEditorPlugin/"))
    {
        Source = new Uri("avares://CSharpEditorPlugin/Styles/StudioStyles.axaml")
    };

    /// <summary>Whether the application applies the bundle to every window (the Runner does; FryPDF doesn't).</summary>
    public static bool ApplicationHasIt => Application.Current?.TryGetResource(MarkerKey, null, out _) == true;

    /// <summary>For a root of its own (the studio host, a window): includes the bundle unless the application has it.</summary>
    public static void IncludeUnlessApplicationHasIt(StyledElement root)
    {
        if (!ApplicationHasIt) root.Styles.Add(Create());
    }

    /// <summary>
    /// For a control shown both inside the studio and on its own: includes the bundle if the tree it joins doesn't have it.
    /// Checked when it joins, before its children are styled, so they are styled once.
    /// </summary>
    public static void IncludeWhereMissing(StyledElement control)
    {
        control.AttachedToLogicalTree += OnAttached;

        static void OnAttached(object? sender, LogicalTreeAttachmentEventArgs e)
        {
            var element = (StyledElement)sender!;
            element.AttachedToLogicalTree -= OnAttached;
            if (!element.TryFindResource(MarkerKey, out _)) element.Styles.Add(Create());
        }
    }
}
