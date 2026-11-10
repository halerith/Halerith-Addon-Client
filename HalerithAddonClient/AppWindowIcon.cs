using Avalonia.Controls;

namespace HalerithAddonClient;

internal static class AppWindowIcon
{
    private static readonly string[] ResourceNames =
    [
        "HalerithAddonClient.HalerithAddonClient.png",
        "HalerithAddonClient.HalerithAddonClient.ico",
    ];

    public static void Apply(Window window)
    {
        foreach (var resourceName in ResourceNames)
        {
            try
            {
                using var stream = typeof(AppWindowIcon).Assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                    continue;

                window.Icon = new WindowIcon(stream);
                return;
            }
            catch
            {
                // Try the next embedded icon format.
            }
        }
    }
}
