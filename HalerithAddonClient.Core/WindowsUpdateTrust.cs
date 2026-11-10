using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HalerithAddonClient.Core;

/// <summary>Windows update packages contain only independently authenticated, self-contained EXEs.</summary>
public static class WindowsUpdateTrust
{
    // Public certificate SHA-256, independently read from the EV certificate on the signing token.
    // Rotation requires an old-key-signed Client/installer release accepting both certificates first.
    private static readonly string[] PublisherCertificates =
    [
        "18A2C7442AD215FE00E3A8FD06AAC12E4BFE6DF9E043831B10D6E9D43F5D8CA8"
    ];
    private static readonly string[] AllowedFiles = ["HalerithAddonClient.exe", "Install.exe", "migrate.exe"];
    private const long MaximumExpandedBytes = 400L * 1024 * 1024;

    public static void ExtractPackage(string packagePath, string stageDirectory)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (!AllowedFiles.Contains(entry.FullName, StringComparer.OrdinalIgnoreCase)
                || !names.Add(entry.FullName) || entry.Length <= 0
                || entry.Length > MaximumExpandedBytes - total)
                throw new InvalidDataException("The Windows update contains unexpected, duplicate or oversized files.");
            total += entry.Length;
        }
        RequireFiles(names);
        // Validate the entire inventory before extracting; no nested paths, DLLs, scripts or metadata.
        foreach (var entry in archive.Entries)
            entry.ExtractToFile(Path.Combine(stageDirectory, entry.FullName), overwrite: false);
    }

    public static VerifiedPayload VerifyAndLock(string directory, string? expectedVersion = null, string? installedVersion = null)
        => VerifyAndLockCore(directory, expectedVersion, installedVersion, VerifyExecutable);

    /// <summary>
    /// Verifies the named manual-installer payload without treating unrelated
    /// sibling files in the launch directory as package members.
    /// </summary>
    public static VerifiedPayload VerifyInstallerPayload(string directory)
        => VerifyAndLockCore(directory, expectedVersion: null, installedVersion: null,
            VerifyExecutable, rejectUnexpectedEntries: false);

    internal static VerifiedPayload VerifyAndLockCore(string directory, string? expectedVersion,
        string? installedVersion, Action<string, FileStream> verify,
        bool rejectUnexpectedEntries = true)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Authenticode update verification requires Windows.");
        var lease = new VerifiedPayload();
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(path);
                if (!AllowedFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    if (rejectUnexpectedEntries)
                        throw new InvalidDataException("The Windows update contains an unexpected file or link.");
                    continue;
                }
                if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                    || !names.Add(name))
                    throw new InvalidDataException("The Windows update contains an unexpected file or link.");
                // Deny both replacement and modification throughout verification and installer handoff.
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                lease.Files.Add(file);
                verify(path, file);
            }
            RequireFiles(names);
            lease.IncludesMigrator = names.Contains("migrate.exe");
            var main = Path.Combine(directory, AllowedFiles[0]);
            var actual = ReadVersion(FileVersionInfo.GetVersionInfo(main).FileVersion);
            if (expectedVersion is not null && actual != ReadVersion(expectedVersion))
                throw new InvalidDataException("The signed Client version does not match the offered update.");
            if (installedVersion is not null && actual <= ReadVersion(installedVersion))
                throw new InvalidDataException("The signed Client update is not newer than the installed version.");
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void RequireFiles(HashSet<string> names)
    {
        if (!names.Contains(AllowedFiles[0]) || !names.Contains(AllowedFiles[1]))
            throw new InvalidDataException("The Windows update must contain the Client and installer.");
    }

    internal static Version ReadVersion(string? value)
    {
        if (!Version.TryParse(value, out var version) || version.Major < 0 || version.Minor < 0)
            throw new InvalidDataException("The Windows update has an invalid version.");
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    private static void VerifyExecutable(string path, FileStream file)
    {
        VerifyAuthenticode(path, file);
        VerifyIdentity(path, file);
    }

    internal static void VerifyIdentity(string path, FileStream file)
    {
        file.Position = 0;
        using var pe = new PEReader(file, PEStreamOptions.LeaveOpen);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
            throw new InvalidDataException("The Windows update is not an x64 executable.");
        var expectedName = Path.GetFileNameWithoutExtension(path) + ".dll";
        var identity = FileVersionInfo.GetVersionInfo(path);
        if (!string.Equals(identity.OriginalFilename, expectedName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The signed executable does not match its update role.");
        if (string.Equals(Path.GetFileName(path), "Install.exe", StringComparison.OrdinalIgnoreCase)
            && identity.ProductName != "Halerith Addon Client Installer")
            throw new InvalidDataException("This installer predates signed update verification. Rebuild the installer before publishing this update.");
    }

    internal static void VerifyAuthenticode(string path, FileStream file)
    {
        var info = new WinTrustFileInfo
        {
            Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), Path = path,
            Handle = file.SafeFileHandle.DangerousGetHandle()
        };
        var infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(info, infoPointer, false);
        var data = new WinTrustData
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, UnionChoice = 1,
            FileInfo = infoPointer, StateAction = 1,
            // Online/cached chain revocation except root; Windows evaluates trusted timestamps.
            ProviderFlags = 0x80 | 0x2000
        };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (status != 0)
                throw new InvalidDataException($"Windows could not verify the update signature (0x{status:X8}). Retry when certificate validation is available.");
            // Read the signer from the VERIFIED provider state, not an independently parsed signature.
            var provider = WTHelperProvDataFromStateData(data.StateData);
            var signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            var certificate = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == IntPtr.Zero)
                throw new InvalidDataException("The update has no verified publisher certificate.");
            var context = Marshal.PtrToStructure<ProviderCertificate>(certificate).Certificate;
            using var x509 = new X509Certificate2(context);
            if (!PublisherCertificates.Contains(x509.GetCertHashString(HashAlgorithmName.SHA256), StringComparer.Ordinal))
                throw new InvalidDataException("The update was not signed by an approved Halerith publisher certificate.");
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<WinTrustFileInfo>(infoPointer);
            Marshal.FreeHGlobal(infoPointer);
            GC.KeepAlive(file);
        }
    }

    public sealed class VerifiedPayload : IDisposable
    {
        internal VerifiedPayload() { }
        internal List<FileStream> Files { get; } = [];
        public bool IncludesMigrator { get; internal set; }
        public void Dispose() { foreach (var file in Files) file.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallback, SipClient;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate
    {
        public uint Size;
        public IntPtr Certificate;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
}
