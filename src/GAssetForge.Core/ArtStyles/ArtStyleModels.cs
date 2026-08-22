namespace GAssetForge.Core.ArtStyles;

public sealed class ArtStyleLibraryModel
{
    public int SchemaVersion { get; set; } = 1;

    public string? SelectedGroupId { get; set; }

    public string? SelectedAssetId { get; set; }

    public List<ArtStyleGroupModel> Groups { get; set; } = [];
}

public sealed class ArtStyleGroupModel
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public List<ArtStyleAssetModel> Assets { get; set; } = [];
}

public sealed class ArtStyleAssetModel
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public string MediaType { get; set; } = "application/octet-stream";

    public string Sha256 { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTimeOffset ImportedAtUtc { get; set; }
}

public static class ArtStyleLibraryFactory
{
    public static ArtStyleLibraryModel CreateDefault(TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var group = new ArtStyleGroupModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "默认风格组",
            Description = "放入能够代表同一画面语言的参考素材。",
            Prompt = string.Empty,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        return new ArtStyleLibraryModel
        {
            SelectedGroupId = group.Id,
            Groups = [group]
        };
    }
}
