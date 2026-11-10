using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HalerithAddonClient.Converters;

public sealed class ConsoleLineBrushConverter : IValueConverter
{
    private static readonly ISolidColorBrush DefaultBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xD4, 0xE4));
    private static readonly ISolidColorBrush WarningBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly ISolidColorBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var line = value?.ToString() ?? "";
        if (line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
            return ErrorBrush;

        if (line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase))
            return WarningBrush;

        return DefaultBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
