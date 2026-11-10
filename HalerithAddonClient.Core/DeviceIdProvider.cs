using HalerithAddonClient.Core.Platform;

namespace HalerithAddonClient.Core;

/// <summary>
/// Provides a stable per-device identifier through the active platform service.
/// </summary>
/// <remarks>
/// <para>
/// Windows uses the system MachineGuid. macOS uses the system Hardware UUID.
/// Linux uses the system machine-id when available.
/// </para>
/// </remarks>
public static class DeviceIdProvider
{
    /// <summary>
    /// Returns the stable device identifier for this machine/user install.
    /// </summary>
    public static string GetDeviceId()
    {
        var id = PlatformServices.Current.DeviceIdentity.GetDeviceId();
#if HALERITH_SANDBOX_BUILD
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("HalerithSandboxClient:" + id))).ToLowerInvariant();
#else
        return id;
#endif
    }

    /// <summary>
    /// Returns a one-way SHA-256 hash (lowercase hex) of the device identifier.
    /// Used by signed Client service requests without sending the raw device identifier.
    /// </summary>
    public static string GetDeviceIdHash()
    {
#if HALERITH_SANDBOX_BUILD
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(GetDeviceId()))).ToLowerInvariant();
#else
        return PlatformServices.Current.DeviceIdentity.GetDeviceIdHash();
#endif
    }
}
