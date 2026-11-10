using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace HalerithAddonClient.Core.Platform;

public interface IDeviceIdentity
{
    string GetDeviceId();
    string GetDeviceIdHash();
}

internal sealed class PlatformDeviceIdentity : IDeviceIdentity
{
    private const string CryptographyKeyPath = @"SOFTWARE\Microsoft\Cryptography";
    private const string MachineGuidValue = "MachineGuid";
    private const int HardwareLookupTimeoutMs = 2_000;

    public string GetDeviceId() => ResolveDeviceId();
    public string GetDeviceIdHash() => Sha256Hex(GetDeviceId());

    private string ResolveDeviceId()
    {
        if (OperatingSystem.IsWindows())
        {
            var machineGuid = TryGetWindowsMachineGuid();
            if (!string.IsNullOrWhiteSpace(machineGuid))
                return machineGuid;
        }

        if (OperatingSystem.IsMacOS())
        {
            var hardwareUuid = TryGetMacHardwareUuid();
            if (!string.IsNullOrWhiteSpace(hardwareUuid))
                return hardwareUuid;
        }

        if (OperatingSystem.IsLinux())
        {
            var machineId = TryGetLinuxMachineId();
            if (!string.IsNullOrWhiteSpace(machineId))
                return machineId;
        }

        throw new InvalidOperationException("Unable to resolve a stable hardware device identifier for this platform.");
    }

    private static string? TryGetWindowsMachineGuid()
    {
#if WINDOWS
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CryptographyKeyPath, writable: false);
            return key?.GetValue(MachineGuidValue) as string;
        }
        catch
        {
            return null;
        }
#else
        return null;
#endif
    }

    private static string? TryGetMacHardwareUuid()
    {
        var ioregOutput = RunProcess("/usr/sbin/ioreg", [
            "-rd1",
            "-c",
            "IOPlatformExpertDevice",
        ]);
        var uuid = ExtractQuotedValue(ioregOutput, "IOPlatformUUID");
        if (Guid.TryParse(uuid, out var parsedUuid))
            return parsedUuid.ToString("D");

        var profilerOutput = RunProcess("/usr/sbin/system_profiler", [
            "SPHardwareDataType",
            "-detailLevel",
            "mini",
        ]);
        uuid = ExtractColonValue(profilerOutput, "Hardware UUID");
        return Guid.TryParse(uuid, out parsedUuid)
            ? parsedUuid.ToString("D")
            : null;
    }

    private static string? TryGetLinuxMachineId()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var value = File.ReadAllText(path).Trim();
                if (value.Length >= 16 && value.All(IsHex))
                    return value.ToLowerInvariant();
            }
            catch
            {
                // Try the next well-known machine-id location.
            }
        }

        return null;
    }

    private static string? RunProcess(string fileName, string[] args)
    {
        try
        {
            if (!File.Exists(fileName))
                return null;

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            process.Start();
            if (!process.WaitForExit(HardwareLookupTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); }
                catch { }
                return null;
            }

            return process.ExitCode == 0
                ? process.StandardOutput.ReadToEnd()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractQuotedValue(string? text, string key)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.Contains(key, StringComparison.Ordinal))
                continue;

            var separator = line.IndexOf('=');
            if (separator < 0)
                continue;

            var firstQuote = line.IndexOf('"', separator);
            var lastQuote = line.LastIndexOf('"');
            if (firstQuote >= 0 && lastQuote > firstQuote)
                return line[(firstQuote + 1)..lastQuote].Trim();
        }

        return null;
    }

    private static string? ExtractColonValue(string? text, string key)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var separator = line.IndexOf(':');
            if (separator < 0)
                continue;

            var candidateKey = line[..separator].Trim();
            if (candidateKey.Equals(key, StringComparison.Ordinal))
                return line[(separator + 1)..].Trim();
        }

        return null;
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static string Sha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
