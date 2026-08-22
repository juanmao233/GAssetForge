using System.Text.Json;
using System.Text.Json.Serialization;

namespace GAssetForge.Core.Projects;

public static class ProjectJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(UiProjectModel project) => JsonSerializer.Serialize(project, Options);

    public static UiProjectModel Deserialize(string json) =>
        JsonSerializer.Deserialize<UiProjectModel>(json, Options)
        ?? throw new InvalidDataException("项目 JSON 为空。");

    public static UiProjectModel Clone(UiProjectModel project) => Deserialize(Serialize(project));
}
