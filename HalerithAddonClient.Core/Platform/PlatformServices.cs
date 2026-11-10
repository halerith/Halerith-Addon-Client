namespace HalerithAddonClient.Core.Platform;

public interface IPlatformServices
{
    IPlatformPaths Paths { get; }
    ISecretProtector Secrets { get; }
    IDeviceIdentity DeviceIdentity { get; }
    IDiagnosticsService Diagnostics { get; }
    IPlatformUpdateInstaller Updates { get; }
    IProcessInspector Processes { get; }
}

public sealed class PlatformServices : IPlatformServices
{
    private static readonly Lazy<IPlatformServices> LazyCurrent = new(Create);

    private PlatformServices(
        IPlatformPaths paths,
        ISecretProtector secrets,
        IDeviceIdentity deviceIdentity,
        IDiagnosticsService diagnostics,
        IPlatformUpdateInstaller updates,
        IProcessInspector processes)
    {
        Paths = paths;
        Secrets = secrets;
        DeviceIdentity = deviceIdentity;
        Diagnostics = diagnostics;
        Updates = updates;
        Processes = processes;
    }

    public static IPlatformServices Current => LazyCurrent.Value;

    public IPlatformPaths Paths { get; }
    public ISecretProtector Secrets { get; }
    public IDeviceIdentity DeviceIdentity { get; }
    public IDiagnosticsService Diagnostics { get; }
    public IPlatformUpdateInstaller Updates { get; }
    public IProcessInspector Processes { get; }

    private static IPlatformServices Create()
    {
        var paths = new DefaultPlatformPaths();
        ISecretProtector secrets = OperatingSystem.IsMacOS()
            ? new MacKeychainSecretProtector()
            : OperatingSystem.IsLinux()
                ? new LinuxSecretProtector()
#if WINDOWS
                : new WindowsSecretProtector();
#else
                : new UnsupportedSecretProtector();
#endif
        var diagnostics = new FileDiagnosticsService(paths);

        IPlatformUpdateInstaller updates = OperatingSystem.IsWindows()
            ? new WindowsUpdateInstaller()
            : OperatingSystem.IsMacOS()
                ? new MacUpdateInstaller()
                : OperatingSystem.IsLinux()
                    ? new LinuxUpdateInstaller()
                    : new UnsupportedUpdateInstaller();

        return new PlatformServices(
            paths,
            secrets,
            new PlatformDeviceIdentity(),
            diagnostics,
            updates,
            new DefaultProcessInspector());
    }

    public static string ResolvePlatform()
    {
        if (OperatingSystem.IsWindows())
            return "windows";
        if (OperatingSystem.IsMacOS())
            return "macos";
        if (OperatingSystem.IsLinux())
            return "linux";
        return System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    }

    public static string ResolveArchitecture() =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            _ => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };
}
