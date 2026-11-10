using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using HalerithAddonClient.Core;

namespace HalerithAddonClient.Core.Platform;

public sealed record StagedUpdatePackage(
    string StageDirectory,
    string ParentProcessId,
    string? RelaunchArgs,
    string? ExpectedVersion = null);

public interface IPlatformUpdateInstaller
{
    string Platform { get; }
    string Architecture { get; }
    string ExpectedPackageExtension { get; }
    bool UsesExternalUpdateFeed { get; }
    Task<bool> CheckForUpdatesAsync(HealthLauncherInfo? launcher, string? relaunchArgs, CancellationToken ct);
    Task<bool> InstallAsync(StagedUpdatePackage package, CancellationToken ct);
}

internal sealed class WindowsUpdateInstaller : IPlatformUpdateInstaller
{
    public string Platform => "windows";
    public string Architecture => PlatformServices.ResolveArchitecture();
    public string ExpectedPackageExtension => ".zip";
    public bool UsesExternalUpdateFeed => false;

    public Task<bool> CheckForUpdatesAsync(HealthLauncherInfo? launcher, string? relaunchArgs, CancellationToken ct)
        => Task.FromResult(false);

    public async Task<bool> InstallAsync(StagedUpdatePackage package, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(package.ExpectedVersion))
            throw new InvalidDataException("The Windows update has no verified release version.");
        var installedVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
        var verified = await Task.Run(() => WindowsUpdateTrust.VerifyAndLock(
            package.StageDirectory, package.ExpectedVersion, installedVersion), ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var installerPath = Path.Combine(package.StageDirectory, "Install.exe");
            if (!File.Exists(installerPath))
                throw new FileNotFoundException("Update package did not contain Install.exe.", installerPath);

            var argParts = new List<string>
            {
                "-update",
                "--parentPid", QuoteArg(package.ParentProcessId),
                "--expectedVersion", QuoteArg(package.ExpectedVersion)
            };

            if (!string.IsNullOrWhiteSpace(package.RelaunchArgs))
            {
                argParts.Add("--relaunchArgs");
                argParts.Add(QuoteArg(package.RelaunchArgs.Trim()));
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = string.Join(' ', argParts),
                    WorkingDirectory = package.StageDirectory,
                    UseShellExecute = true,
                    Verb = "runas"
                }
            };

            if (!process.Start())
                throw new InvalidOperationException("Failed to start Install.exe -update.");

            // Keep validated files immutable until the helper exits or this process exits.
            // The elevated helper acquires its own locks before waiting for our exit.
            _ = ReleaseAfterExitAsync(process, verified);
            return true;
        }
        catch { verified.Dispose(); throw; }
    }

    private static async Task ReleaseAfterExitAsync(Process process, IDisposable verified)
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        finally { verified.Dispose(); process.Dispose(); }
    }

    private static string QuoteArg(string value)
        => $"\"{value.Replace("\"", "\\\"")}\"";
}

internal sealed class MacUpdateInstaller : IPlatformUpdateInstaller
{
    public string Platform => "macos";
    public string Architecture => PlatformServices.ResolveArchitecture();
    public string ExpectedPackageExtension => ".dmg";
    public bool UsesExternalUpdateFeed => true;

    public Task<bool> CheckForUpdatesAsync(HealthLauncherInfo? launcher, string? relaunchArgs, CancellationToken ct)
    {
        if (launcher is not { UpdateAvailable: true })
            return Task.FromResult(false);

        ct.ThrowIfCancellationRequested();
        MacSparkleBridge.CheckForUpdates(launcher.AppcastUrl);
        return Task.FromResult(true);
    }

    public Task<bool> InstallAsync(StagedUpdatePackage package, CancellationToken ct)
    {
        throw new NotSupportedException(
            "macOS self-update is handled by Sparkle through the appcast configured in the app bundle.");
    }
}

internal sealed partial class LinuxUpdateInstaller : IPlatformUpdateInstaller
{
    public string Platform => "linux";
    public string Architecture => PlatformServices.ResolveArchitecture();
    public string ExpectedPackageExtension => ".deb";
    public bool UsesExternalUpdateFeed => false;

    public Task<bool> CheckForUpdatesAsync(HealthLauncherInfo? launcher, string? relaunchArgs, CancellationToken ct)
        => Task.FromResult(false);

    public async Task<bool> InstallAsync(StagedUpdatePackage package, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Linux update installation is only supported on Linux.");

        var debPath = FindStagedDeb(package.StageDirectory, out var expectedVersion);
        LinuxUpdateTrust.Verify(debPath,
            LinuxUpdateTrust.ReadBounded(Path.Combine(package.StageDirectory, "release.json"), 4096),
            LinuxUpdateTrust.ReadBounded(Path.Combine(package.StageDirectory, "release.json.sig"), 384),
            package.ExpectedVersion ?? throw new InvalidDataException("Expected update version is missing."),
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString());
        var helperPath = Path.Combine(PlatformServices.Current.Paths.InstallDirectory, "halerith-helper");
        if (!File.Exists(helperPath))
            throw new FileNotFoundException(
                "The Linux update helper was not found. Reinstall the latest .deb manually to repair self-update support.",
                helperPath);

        var pkexecPath = FindExecutable("pkexec")
            ?? throw new FileNotFoundException("pkexec was not found. Install policykit-1/polkit to enable graphical self-updates.");

        var result = await RunProcessAsync(
            pkexecPath,
            [helperPath, "install-deb", debPath, expectedVersion],
            package.StageDirectory,
            ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.Error)
                ? result.Output
                : result.Error;
            throw new InvalidOperationException($"Linux update helper failed with exit code {result.ExitCode}: {detail.Trim()}");
        }

        try
        {
            RelaunchInstalledApp(package.RelaunchArgs);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Linux update installed, but relaunch failed: {ex.Message}");
        }

        return true;
    }

    private string FindStagedDeb(string stageDirectory, out string expectedVersion)
    {
        var expectedArch = Architecture;
        var matches = Directory
            .EnumerateFiles(stageDirectory, "HalerithAddonClient_linux_*.deb", SearchOption.AllDirectories)
            .Select(path => new { Path = path, Match = LinuxDebNameRegex().Match(Path.GetFileName(path)) })
            .Where(x => x.Match.Success && string.Equals(x.Match.Groups["arch"].Value, expectedArch, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            throw new FileNotFoundException($"Linux update package did not contain HalerithAddonClient_linux_{expectedArch}_<version>.deb.");

        if (matches.Count > 1)
            throw new InvalidOperationException($"Linux update package contained multiple {expectedArch} .deb files.");

        expectedVersion = matches[0].Match.Groups["version"].Value;
        return Path.GetFullPath(matches[0].Path);
    }

    private static string? FindExecutable(string name)
    {
        foreach (var directory in new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static void RelaunchInstalledApp(string? relaunchArgs)
    {
        var executablePath = FindRelaunchExecutable();
        var shellPath = FindExecutable("sh")
            ?? throw new FileNotFoundException("sh was not found. Unable to relaunch after Linux update.");

        var startInfo = new ProcessStartInfo
        {
            FileName = shellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? "/",
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("sleep 1; exec \"$0\" \"$@\"");
        startInfo.ArgumentList.Add(executablePath);
        foreach (var arg in SplitRelaunchArgs(relaunchArgs))
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo);

        if (process is null)
            throw new InvalidOperationException("Failed to start Linux relaunch process.");
    }

    private static string FindRelaunchExecutable()
    {
        var candidates = new[]
        {
            "/usr/bin/halerith-addon-client",
            Path.Combine(PlatformServices.Current.Paths.InstallDirectory, "HalerithAddonClient"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("Unable to locate the installed Linux client launcher.");
    }

    private static IReadOnlyList<string> SplitRelaunchArgs(string? relaunchArgs)
    {
        if (string.IsNullOrWhiteSpace(relaunchArgs))
            return [];

        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in relaunchArgs.Trim())
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            args.Add(current.ToString());

        return args;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {fileName}.");

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return new ProcessResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    [GeneratedRegex(@"^HalerithAddonClient_linux_(?<arch>x64)_(?<version>[0-9][0-9A-Za-z.+~-]*)\.deb$", RegexOptions.IgnoreCase)]
    private static partial Regex LinuxDebNameRegex();
}

internal sealed class UnsupportedUpdateInstaller : IPlatformUpdateInstaller
{
    public string Platform => PlatformServices.ResolvePlatform();
    public string Architecture => PlatformServices.ResolveArchitecture();
    public string ExpectedPackageExtension => ".zip";
    public bool UsesExternalUpdateFeed => false;

    public Task<bool> CheckForUpdatesAsync(HealthLauncherInfo? launcher, string? relaunchArgs, CancellationToken ct)
        => Task.FromResult(false);

    public Task<bool> InstallAsync(StagedUpdatePackage package, CancellationToken ct)
    {
        throw new NotSupportedException($"Self-update is not supported on platform '{Platform}'.");
    }
}

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal static class MacSparkleBridge
{
    private static readonly object Gate = new();
    private static readonly DispatchFunction CheckForUpdatesOnMainDelegate = CheckForUpdatesOnMain;
    private static IntPtr UpdaterController;
    private static IntPtr SparkleHandle;
    private static IntPtr LibSystemHandle;
    private static IntPtr MainDispatchQueue;

    public static void CheckForUpdates(string? appcastUrl)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Sparkle updates are only available on macOS.");

        var feedUrl = string.IsNullOrWhiteSpace(appcastUrl)
            ? null
            : HttpsUrlPolicy.RequireHttpsAbsoluteUri(appcastUrl, "Sparkle appcast URL").ToString();

        var request = new SparkleCheckRequest(feedUrl);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(request);
        dispatch_async_f(GetMainDispatchQueue(), System.Runtime.InteropServices.GCHandle.ToIntPtr(handle), CheckForUpdatesOnMainDelegate);
    }

    private static void CheckForUpdatesOnMain(IntPtr context)
    {
        var handle = System.Runtime.InteropServices.GCHandle.FromIntPtr(context);
        try
        {
            var request = (SparkleCheckRequest?) handle.Target;
            CheckForUpdatesOnMainThread(request?.AppcastUrl);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Sparkle update check failed on main thread: {ex}");
        }
        finally
        {
            handle.Free();
        }
    }

    private static void CheckForUpdatesOnMainThread(string? appcastUrl)
    {
        var controller = GetUpdaterController();
        SetFeedUrl(controller, appcastUrl);
        objc_msgSend_void_intptr(controller, sel_registerName("checkForUpdates:"), IntPtr.Zero);
    }

    private static void SetFeedUrl(IntPtr controller, string? appcastUrl)
    {
        if (string.IsNullOrWhiteSpace(appcastUrl))
            return;

        var feedUri = HttpsUrlPolicy.RequireHttpsAbsoluteUri(appcastUrl, "Sparkle appcast URL");
        var updater = objc_msgSend_intptr(controller, sel_registerName("updater"));
        if (updater == IntPtr.Zero)
            throw new InvalidOperationException("Sparkle updater controller did not expose an updater instance.");

        var cfString = CFStringCreateWithCString(IntPtr.Zero, feedUri.ToString(), kCFStringEncodingUTF8);
        if (cfString == IntPtr.Zero)
            throw new InvalidOperationException("Unable to create Sparkle appcast URL string.");

        try
        {
            var urlClass = objc_getClass("NSURL");
            if (urlClass == IntPtr.Zero)
                throw new InvalidOperationException("NSURL class could not be found.");

            var feedUrl = objc_msgSend_intptr_intptr(urlClass, sel_registerName("URLWithString:"), cfString);
            if (feedUrl == IntPtr.Zero)
                throw new InvalidOperationException("Sparkle appcast URL is invalid.");

            objc_msgSend_void_intptr(updater, sel_registerName("setFeedURL:"), feedUrl);
        }
        finally
        {
            CFRelease(cfString);
        }
    }

    private static IntPtr GetUpdaterController()
    {
        lock (Gate)
        {
            if (UpdaterController != IntPtr.Zero)
                return UpdaterController;

            LoadSparkleFramework();

            var controllerClass = objc_getClass("SPUStandardUpdaterController");
            if (controllerClass == IntPtr.Zero)
                throw new InvalidOperationException("Sparkle framework is present, but SPUStandardUpdaterController could not be found.");

            var allocated = objc_msgSend_intptr(controllerClass, sel_registerName("alloc"));
            if (allocated == IntPtr.Zero)
                throw new InvalidOperationException("Unable to allocate Sparkle updater controller.");

            UpdaterController = objc_msgSend_intptr_bool_intptr_intptr(
                allocated,
                sel_registerName("initWithStartingUpdater:updaterDelegate:userDriverDelegate:"),
                true,
                IntPtr.Zero,
                IntPtr.Zero);

            if (UpdaterController == IntPtr.Zero)
                throw new InvalidOperationException("Unable to initialize Sparkle updater controller.");

            return UpdaterController;
        }
    }

    private static void LoadSparkleFramework()
    {
        if (SparkleHandle != IntPtr.Zero)
            return;

        var frameworkPath = ResolveSparkleFrameworkPath();
        if (frameworkPath is null)
            throw new FileNotFoundException(
                "Sparkle.framework was not found in the app bundle. Build the macOS .app with Sparkle.framework under Contents/Frameworks.");

        SparkleHandle = System.Runtime.InteropServices.NativeLibrary.Load(frameworkPath);
    }

    private static string? ResolveSparkleFrameworkPath()
    {
        var executablePath = PlatformServices.Current.Paths.CurrentExecutablePath;
        var executableDir = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(executableDir))
            return null;

        var contentsDir = Directory.GetParent(executableDir)?.FullName;
        if (string.IsNullOrWhiteSpace(contentsDir))
            return null;

        var frameworkPath = Path.Combine(contentsDir, "Frameworks", "Sparkle.framework", "Sparkle");
        return File.Exists(frameworkPath) ? frameworkPath : null;
    }

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_intptr(IntPtr receiver, IntPtr selector);

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_intptr_intptr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_intptr_bool_intptr_intptr(
        IntPtr receiver,
        IntPtr selector,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)] bool arg1,
        IntPtr arg2,
        IntPtr arg3);

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_intptr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    private const uint kCFStringEncodingUTF8 = 0x08000100;

    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, uint encoding);

    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    private sealed record SparkleCheckRequest(string? AppcastUrl);

    private static IntPtr GetMainDispatchQueue()
    {
        if (MainDispatchQueue != IntPtr.Zero)
            return MainDispatchQueue;

        if (TryResolveMainDispatchQueue("/usr/lib/libSystem.dylib", out var queue)
            || TryResolveMainDispatchQueue("/usr/lib/system/libdispatch.dylib", out queue))
        {
            MainDispatchQueue = queue;
            return MainDispatchQueue;
        }

        throw new EntryPointNotFoundException("Unable to find the macOS main dispatch queue symbol '_dispatch_main_q'.");
    }

    private static bool TryResolveMainDispatchQueue(string libraryPath, out IntPtr queue)
    {
        queue = IntPtr.Zero;
        if (!System.Runtime.InteropServices.NativeLibrary.TryLoad(libraryPath, out var handle))
            return false;

        if (!System.Runtime.InteropServices.NativeLibrary.TryGetExport(handle, "_dispatch_main_q", out queue))
            return false;

        LibSystemHandle = handle;
        return true;
    }

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate void DispatchFunction(IntPtr context);

    [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.dylib")]
    private static extern void dispatch_async_f(IntPtr queue, IntPtr context, DispatchFunction work);
}
