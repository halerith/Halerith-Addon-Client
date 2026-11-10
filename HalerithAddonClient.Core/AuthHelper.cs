namespace HalerithAddonClient.Core;

public static class AuthHelper
{
    public static string NormalizeUserId(string? userId)
    {
        return (userId ?? string.Empty).Trim().ToLowerInvariant();
    }
}
