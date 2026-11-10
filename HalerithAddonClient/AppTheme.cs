using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace HalerithAddonClient;

public static class AppTheme
{
    public static void SetMode()
    {
        if (Application.Current is not null)
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
    }

    public static bool IsDark => true;

    // Brush helpers
    private static ISolidColorBrush Brush(string key)
    {
        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var res) == true && res is ISolidColorBrush b)
            return b;
        return new SolidColorBrush(Colors.Gray);
    }

    public static ISolidColorBrush StatusIdle    => Brush("StatusIdleBrush");
    public static ISolidColorBrush StatusOk      => Brush("StatusOkBrush");
    public static ISolidColorBrush StatusError   => Brush("StatusErrorBrush");
    public static ISolidColorBrush StatusWarning => Brush("StatusWarningBrush");
    public static ISolidColorBrush StatusInfo    => Brush("StatusInfoBrush");
    public static ISolidColorBrush TextMuted     => Brush("TextMutedBrush");
    public static ISolidColorBrush TextSecondary => Brush("TextSecondaryBrush");
    public static ISolidColorBrush LogForeground => Brush("LogForegroundBrush");
    public static ISolidColorBrush LogWarning    => Brush("LogWarningBrush");
    public static ISolidColorBrush LogError      => Brush("LogErrorBrush");
}
