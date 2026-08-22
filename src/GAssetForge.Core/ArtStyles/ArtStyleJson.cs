using System.Text.Json;
using System.Text.Json.Serialization;

namespace GAssetForge.Core.ArtStyles;

public static class ArtStyleJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(ArtStyleLibraryModel library) =>
        JsonSerializer.Serialize(library, Options);

    public static ArtStyleLibraryModel Deserialize(string json) =>
        JsonSerializer.Deserialize<ArtStyleLibraryModel>(json, Options)
        ?? throw new InvalidDataException("美术风格素材库 JSON 为空。");

    public static ArtStyleLibraryModel Clone(ArtStyleLibraryModel library) =>
        Deserialize(Serialize(library));
}
