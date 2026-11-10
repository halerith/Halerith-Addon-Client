using HalerithAddonClient.Core;

namespace HalerithAddonClient.Services;

public sealed record ManagedAccessCheckResult(bool Allowed, string Message)
{
    public static ManagedAccessCheckResult Allow() => new(true, "");

    public static ManagedAccessCheckResult Block(string message) => new(false, message);
}
