using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Material.Icons;

namespace PdfEditorApp.Plugins.CSharpEditor.Controls.Common;

/// <summary>
/// Turns an icon name ("ChevronDown") into its <see cref="MaterialIconKind"/>, remembering every name it has seen. Bind each
/// icon name kept as a string through it. Left to Avalonia, every update looks the name up among the 7,000+ icon names
/// (a reflection binding copies the whole list of names to do it) at 1–2 ms a time, which each Explorer row, Problems row
/// and tab switch paid for each of its icons. An icon that is already a <see cref="MaterialIconKind"/> passes straight through.
/// </summary>
public sealed class IconKindConverter : IValueConverter
{
    public static readonly IconKindConverter Instance = new();

    private static readonly ConcurrentDictionary<string, MaterialIconKind?> Kinds = new(StringComparer.Ordinal);

    /// <summary>The icon called <paramref name="name"/> (any case), or null when there is no such icon.</summary>
    public static MaterialIconKind? Parse(string? name) =>
        string.IsNullOrEmpty(name)
            ? null
            : Kinds.GetOrAdd(name, static n => Enum.TryParse<MaterialIconKind>(n, ignoreCase: true, out var kind) ? kind : null);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        MaterialIconKind kind => kind,
        string name when Parse(name) is { } kind => kind,
        // No icon by that name: the binding shows its FallbackValue, as it did when the conversion failed.
        _ => AvaloniaProperty.UnsetValue
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
