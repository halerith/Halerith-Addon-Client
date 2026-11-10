using System.Text.Json;
using System.Text.Json.Serialization;

namespace HalerithAddonClient.Core;

[JsonConverter(typeof(ManifestHashInfoJsonConverter))]
public sealed class ManifestHashInfo
{
    [JsonPropertyName("algorithm")]
    public string Algorithm { get; set; } = "";

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonIgnore]
    public bool HasValue => !string.IsNullOrWhiteSpace(Algorithm) && !string.IsNullOrWhiteSpace(Hash);

    public static ManifestHashInfo Empty { get; } = new();

    public static ManifestHashInfo FromHash(string? hash)
    {
        hash = hash?.Trim() ?? "";
        if (hash.Length == 0)
            return new ManifestHashInfo();

        var algorithm = hash.Length switch
        {
            40 => "sha1",
            64 => "sha256",
            128 => "sha512",
            _ => ""
        };

        return new ManifestHashInfo
        {
            Algorithm = algorithm,
            Hash = hash.ToLowerInvariant()
        };
    }

    public ManifestHashInfo Normalized() => new()
    {
        Algorithm = (Algorithm ?? "").Trim().ToLowerInvariant().Replace("-", ""),
        Hash = (Hash ?? "").Trim().ToLowerInvariant()
    };
}

internal sealed class ManifestHashInfoJsonConverter : JsonConverter<ManifestHashInfo>
{
    public override ManifestHashInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return new ManifestHashInfo();

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected hash object with algorithm and hash fields.");

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var algorithm = root.TryGetProperty("algorithm", out var algorithmElement)
            ? algorithmElement.GetString() ?? ""
            : "";
        var hash = root.TryGetProperty("hash", out var hashElement)
            ? hashElement.GetString() ?? ""
            : "";

        return new ManifestHashInfo
        {
            Algorithm = algorithm,
            Hash = hash
        }.Normalized();
    }

    public override void Write(Utf8JsonWriter writer, ManifestHashInfo value, JsonSerializerOptions options)
    {
        var normalized = value.Normalized();
        writer.WriteStartObject();
        writer.WriteString("algorithm", normalized.Algorithm);
        writer.WriteString("hash", normalized.Hash);
        writer.WriteEndObject();
    }
}
