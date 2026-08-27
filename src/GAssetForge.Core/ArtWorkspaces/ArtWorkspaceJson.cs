using System.Text.Json;
using System.Text.Json.Serialization;

namespace GAssetForge.Core.ArtWorkspaces;

public static class ArtWorkspaceJson
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(ArtWorkspaceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArtWorkspaceValidator.Validate(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public static ArtWorkspaceDocument Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<ArtWorkspaceDocument>(json, Options)
            ?? throw new InvalidDataException("美术工作区 JSON 为空。");
        ArtWorkspaceValidator.Validate(document);
        return document;
    }
}
