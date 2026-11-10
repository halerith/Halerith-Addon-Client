using System.Reflection;

namespace HalerithAddonClient.Core.ContentSearch;

internal static class ProviderContacts
{
    internal static string ModrinthUserAgent => UserAgent("ModrinthContactEmail");
    internal static string CurseForgeUserAgent => UserAgent("CurseForgeContactEmail");

    private static string UserAgent(string key)
    {
        var email = typeof(ProviderContacts).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value;
        if (string.IsNullOrWhiteSpace(email) || email.IndexOfAny(['\r', '\n', '(', ')']) >= 0)
            throw new InvalidOperationException($"Missing or invalid provider contact project property: {key}");
        return $"halerith_addon_client ({email.Trim()})";
    }
}
