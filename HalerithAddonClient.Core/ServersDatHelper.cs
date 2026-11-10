using System.Text;
using System.Net;

namespace HalerithAddonClient.Core;

/// <summary>
/// Keeps the Minecraft <c>servers.dat</c> file in sync with the panel's server
/// connection info before each game launch.
///
/// <para>
/// Behaviour:
/// <list type="bullet">
///   <item>If an entry whose <c>ip</c> field matches <c>gameHost:port</c> (or just
///         <c>gameHost</c> when <paramref name="port"/> is the default 25565) is found,
///         its display name is updated to match the panel's <paramref name="serverName"/>.</item>
///   <item>If no matching entry exists, a new one is appended.</item>
///   <item>If <c>servers.dat</c> does not exist yet, it is created from scratch.</item>
/// </list>
/// </para>
///
/// <para>
/// The method is intentionally non-fatal: any I/O or parse error is swallowed and
/// passed to the optional <paramref name="log"/> callback so the launcher can still
/// proceed even when the game data folder is missing or the file is malformed.
/// </para>
/// </summary>
public static class ServersDatHelper
{
    private const int DefaultMinecraftPort = 25565;

    /// <summary>
    /// Ensures <c>{gameDataPath}/servers.dat</c> contains a correctly-named entry
    /// for the given server.  Safe to call on every launch.
    /// </summary>
    /// <param name="gameDataPath">Root Minecraft game data folder (the one containing <c>saves/</c>, <c>mods/</c>, etc.).</param>
    /// <param name="gameHost">Public hostname or IP address players use to connect.</param>
    /// <param name="port">Minecraft server port.  When 25565 the port is omitted from the <c>ip</c> field.</param>
    /// <param name="serverName">Display name shown in the Minecraft server list.</param>
    /// <param name="log">Optional callback that receives diagnostic messages.</param>
    public static string? EnsureServerEntry(
        string          gameDataPath,
        string?         gameHost,
        int             port,
        string?         serverName,
        string?         previousAddress = null,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath))  { log?.Invoke("ServersDat: skipped — gameDataPath is empty."); return null; }
        if (string.IsNullOrWhiteSpace(gameHost))      { log?.Invoke("ServersDat: skipped — game host is empty."); return null; }
        if (string.IsNullOrWhiteSpace(serverName))    { log?.Invoke("ServersDat: skipped — serverName is empty."); return null; }
        if (port is < 1 or > 65535)                   { log?.Invoke("ServersDat: skipped — game port is invalid."); return null; }

        var address = BuildAddress(gameHost, port);
        var datPath = Path.Combine(gameDataPath, "servers.dat");

        try
        {
            List<ServerEntry> entries = File.Exists(datPath)
                ? ParseServersDat(File.ReadAllBytes(datPath))
                : [];

            var existing = entries.FirstOrDefault(e =>
                string.Equals(e.Ip, address, StringComparison.OrdinalIgnoreCase));
            if (existing is null && !string.IsNullOrWhiteSpace(previousAddress))
            {
                existing = entries.FirstOrDefault(e =>
                    string.Equals(e.Ip, previousAddress.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            if (existing is not null)
            {
                var addressChanged = !string.Equals(existing.Ip, address, StringComparison.OrdinalIgnoreCase);
                existing.Ip = address;
                if (!addressChanged && string.Equals(existing.Name, serverName, StringComparison.Ordinal))
                {
                    log?.Invoke($"ServersDat: '{address}' already has the correct name — no changes needed.");
                    return address;
                }
                log?.Invoke($"ServersDat: updating name for '{address}': '{existing.Name}' → '{serverName}'.");
                existing.Name = serverName;
            }
            else
            {
                log?.Invoke($"ServersDat: adding new entry — name='{serverName}', ip='{address}'.");
                entries.Add(new ServerEntry { Ip = address, Name = serverName });
            }

            var tempPath = Path.Combine(
                Path.GetDirectoryName(datPath) ?? gameDataPath,
                $".servers.dat.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(tempPath, WriteServersDat(entries));
                File.Move(tempPath, datPath, true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            log?.Invoke("ServersDat: file saved successfully.");
            return address;
        }
        catch (Exception ex)
        {
            log?.Invoke($"ServersDat: non-fatal error — {ex.Message}");
            return null;
        }
    }

    // ── Address helper ─────────────────────────────────────────────────────────

    private static string BuildAddress(string host, int port)
    {
        host = host.Trim();
        if (IPAddress.TryParse(host, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            host = $"[{host.Trim('[', ']')}]";

        return port == DefaultMinecraftPort ? host : $"{host}:{port}";
    }

    // ── Internal model ─────────────────────────────────────────────────────────

    private sealed class ServerEntry
    {
        public string  Name           { get; set; } = "";
        public string  Ip             { get; set; } = "";
        public byte?   AcceptTextures { get; set; }
        public string? Icon           { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  NBT parse
    // ══════════════════════════════════════════════════════════════════════════

    private static List<ServerEntry> ParseServersDat(byte[] data)
    {
        var r = new NbtReader(data);

        // Root element must be TAG_Compound with an (usually empty) name.
        var rootType = r.ReadByte();
        if (rootType != NbtTag.Compound)
            throw new InvalidDataException($"servers.dat: expected root TAG_Compound (0x0A), got 0x{rootType:X2}.");
        r.ReadString(); // root name — discard

        // Walk root compound looking for TAG_List "servers"
        List<ServerEntry>? result = null;
        while (true)
        {
            var tagType = r.ReadByte();
            if (tagType == NbtTag.End) break;

            var name = r.ReadString();
            if (tagType == NbtTag.List && name == "servers")
                result = ReadServersList(r);
            else
                r.SkipPayload(tagType);
        }

        return result ?? [];
    }

    private static List<ServerEntry> ReadServersList(NbtReader r)
    {
        var elementType = r.ReadByte();
        var count       = r.ReadInt();

        if (elementType != NbtTag.Compound)
        {
            // Unexpected element type — skip and return empty
            for (var i = 0; i < count; i++)
                r.SkipPayload(elementType);
            return [];
        }

        var entries = new List<ServerEntry>(count);
        for (var i = 0; i < count; i++)
            entries.Add(ReadServerCompound(r));
        return entries;
    }

    private static ServerEntry ReadServerCompound(NbtReader r)
    {
        var entry = new ServerEntry();
        while (true)
        {
            var tagType = r.ReadByte();
            if (tagType == NbtTag.End) break;
            var name = r.ReadString();

            if (tagType == NbtTag.String)
            {
                var value = r.ReadString();
                switch (name)
                {
                    case "name": entry.Name = value; break;
                    case "ip":   entry.Ip   = value; break;
                    case "icon": entry.Icon = value; break;
                    // any other strings are silently discarded
                }
            }
            else if (tagType == NbtTag.Byte && name == "acceptTextures")
            {
                entry.AcceptTextures = r.ReadByte();
            }
            else
            {
                r.SkipPayload(tagType);
            }
        }
        return entry;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  NBT write
    // ══════════════════════════════════════════════════════════════════════════

    private static byte[] WriteServersDat(List<ServerEntry> entries)
    {
        using var ms = new MemoryStream();
        var w = new NbtWriter(ms);

        // Root TAG_Compound (empty name)
        w.WriteNamedTag(NbtTag.Compound, "");

        // TAG_List "servers" — element type is TAG_Compound
        w.WriteNamedTag(NbtTag.List, "servers");
        w.WriteByte(NbtTag.Compound);
        w.WriteInt(entries.Count);

        foreach (var e in entries)
        {
            // Compound body (no type/name prefix inside a list)
            w.WriteNamedTag(NbtTag.String, "ip");   w.WriteString(e.Ip);
            w.WriteNamedTag(NbtTag.String, "name"); w.WriteString(e.Name);

            if (e.AcceptTextures.HasValue)
            {
                w.WriteNamedTag(NbtTag.Byte, "acceptTextures");
                w.WriteByte(e.AcceptTextures.Value);
            }
            if (e.Icon is not null)
            {
                w.WriteNamedTag(NbtTag.String, "icon");
                w.WriteString(e.Icon);
            }

            w.WriteByte(NbtTag.End); // TAG_End closes this compound
        }

        w.WriteByte(NbtTag.End); // TAG_End closes root compound

        return ms.ToArray();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  NBT tag type constants
    // ══════════════════════════════════════════════════════════════════════════

    private static class NbtTag
    {
        public const byte End       = 0x00;
        public const byte Byte      = 0x01;
        public const byte Short     = 0x02;
        public const byte Int       = 0x03;
        public const byte Long      = 0x04;
        public const byte Float     = 0x05;
        public const byte Double    = 0x06;
        public const byte ByteArray = 0x07;
        public const byte String    = 0x08;
        public const byte List      = 0x09;
        public const byte Compound  = 0x0A;
        public const byte IntArray  = 0x0B;
        public const byte LongArray = 0x0C;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Minimal NBT reader
    // ══════════════════════════════════════════════════════════════════════════

    private sealed class NbtReader(byte[] data)
    {
        private int _pos;

        public byte ReadByte()
        {
            if (_pos >= data.Length) throw new EndOfStreamException("Unexpected end of NBT data.");
            return data[_pos++];
        }

        /// <summary>Reads a 2-byte unsigned big-endian length.</summary>
        public ushort ReadUShort()
        {
            var hi = ReadByte(); var lo = ReadByte();
            return (ushort)((hi << 8) | lo);
        }

        /// <summary>Reads a 4-byte signed big-endian integer.</summary>
        public int ReadInt()
        {
            var b0 = ReadByte(); var b1 = ReadByte();
            var b2 = ReadByte(); var b3 = ReadByte();
            return (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
        }

        /// <summary>Reads a length-prefixed UTF-8 string (2-byte unsigned length).</summary>
        public string ReadString()
        {
            var len = ReadUShort();
            if (len == 0) return "";
            var s = Encoding.UTF8.GetString(data, _pos, len);
            _pos += len;
            return s;
        }

        public void Skip(int count) => _pos += count;

        /// <summary>Skips the payload of a tag of the given type (assumes name has already been read).</summary>
        public void SkipPayload(byte tagType)
        {
            switch (tagType)
            {
                case NbtTag.Byte:      Skip(1); break;
                case NbtTag.Short:     Skip(2); break;
                case NbtTag.Int:
                case NbtTag.Float:     Skip(4); break;
                case NbtTag.Long:
                case NbtTag.Double:    Skip(8); break;
                case NbtTag.ByteArray: Skip(ReadInt()); break;
                case NbtTag.String:    Skip(ReadUShort()); break;
                case NbtTag.IntArray:  Skip(ReadInt() * 4); break;
                case NbtTag.LongArray: Skip(ReadInt() * 8); break;
                case NbtTag.List:
                {
                    var elType = ReadByte();
                    var count  = ReadInt();
                    for (var i = 0; i < count; i++)
                        SkipPayload(elType);
                    break;
                }
                case NbtTag.Compound:
                    while (true)
                    {
                        var t = ReadByte();
                        if (t == NbtTag.End) break;
                        Skip(ReadUShort()); // skip tag name
                        SkipPayload(t);
                    }
                    break;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Minimal NBT writer
    // ══════════════════════════════════════════════════════════════════════════

    private sealed class NbtWriter(Stream stream)
    {
        public void WriteByte(byte value)   => stream.WriteByte(value);

        public void WriteUShort(ushort value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value & 0xFF));
        }

        public void WriteInt(int value)
        {
            stream.WriteByte((byte)((value >> 24) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >>  8) & 0xFF));
            stream.WriteByte((byte)( value        & 0xFF));
        }

        /// <summary>Writes a length-prefixed UTF-8 string.</summary>
        public void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteUShort((ushort)bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>Writes a named tag header: type byte + name string.</summary>
        public void WriteNamedTag(byte tagType, string name)
        {
            WriteByte(tagType);
            WriteString(name);
        }
    }
}
