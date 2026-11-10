using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace HalerithAddonClient.Core;
public static class SerializerOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
}
