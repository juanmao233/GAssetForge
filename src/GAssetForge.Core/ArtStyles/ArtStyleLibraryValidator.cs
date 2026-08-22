using System.Text.RegularExpressions;

namespace GAssetForge.Core.ArtStyles;

public static partial class ArtStyleLibraryValidator
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumGroupCount = 2_000;
    public const int MaximumAssetsPerGroup = 10_000;
    public const int MaximumNameLength = 160;
    public const int MaximumDescriptionLength = 8_000;
    public const int MaximumPromptLength = 32_000;
    public const long MaximumAssetFileBytes = 512L * 1024L * 1024L;
    public const long MaximumAssetImportBatchBytes = 2L * 1024L * 1024L * 1024L;

    public static void Validate(ArtStyleLibraryModel library)
    {
        ArgumentNullException.ThrowIfNull(library);

        if (library.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"不支持的美术风格素材库版本：{library.SchemaVersion}。");
        }

        if (library.Groups is null || library.Groups.Count > MaximumGroupCount)
        {
            throw new InvalidDataException("美术风格组数量无效。");
        }

        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in library.Groups)
        {
            ValidateGroup(group);
            if (!groupIds.Add(group.Id))
            {
                throw new InvalidDataException($"存在重复的美术风格组 ID：{group.Id}。");
            }

            foreach (var asset in group.Assets)
            {
                ValidateAsset(asset, group.Id);
                if (!assetIds.Add(asset.Id))
                {
                    throw new InvalidDataException($"存在重复的美术素材 ID：{asset.Id}。");
                }
            }
        }

        if (library.SelectedGroupId is not null && !groupIds.Contains(library.SelectedGroupId))
        {
            throw new InvalidDataException("当前选择的美术风格组不存在。");
        }

        if (library.SelectedAssetId is not null)
        {
            if (!assetIds.Contains(library.SelectedAssetId))
            {
                throw new InvalidDataException("当前选择的美术素材不存在。");
            }

            var selectedGroup = library.Groups.FirstOrDefault(group => group.Id == library.SelectedGroupId);
            if (selectedGroup is null || selectedGroup.Assets.All(asset => asset.Id != library.SelectedAssetId))
            {
                throw new InvalidDataException("当前选择的素材不属于当前风格组。");
            }
        }
    }

    public static void ValidateGroupName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("风格组名称不能为空。", nameof(name));
        }

        if (name.Length > MaximumNameLength)
        {
            throw new ArgumentException($"风格组名称不能超过 {MaximumNameLength} 个字符。", nameof(name));
        }
    }

    private static void ValidateGroup(ArtStyleGroupModel group)
    {
        if (group is null || !IsStableId(group.Id))
        {
            throw new InvalidDataException("美术风格组 ID 无效。");
        }

        if (string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > MaximumNameLength)
        {
            throw new InvalidDataException($"美术风格组 {group.Id} 的名称无效。");
        }

        if (group.Description is null || group.Description.Length > MaximumDescriptionLength)
        {
            throw new InvalidDataException($"美术风格组 {group.Id} 的描述无效。");
        }

        if (group.Prompt is null || group.Prompt.Length > MaximumPromptLength)
        {
            throw new InvalidDataException($"美术风格组 {group.Id} 的风格提示词无效。");
        }

        if (group.CreatedAtUtc == default || group.UpdatedAtUtc < group.CreatedAtUtc)
        {
            throw new InvalidDataException($"美术风格组 {group.Id} 的时间戳无效。");
        }

        if (group.Assets is null || group.Assets.Count > MaximumAssetsPerGroup)
        {
            throw new InvalidDataException($"美术风格组 {group.Id} 的素材数量无效。");
        }
    }

    private static void ValidateAsset(ArtStyleAssetModel asset, string groupId)
    {
        if (asset is null || !IsStableId(asset.Id))
        {
            throw new InvalidDataException($"美术风格组 {groupId} 中存在无效的素材 ID。");
        }

        if (string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Length > MaximumNameLength)
        {
            throw new InvalidDataException($"美术素材 {asset.Id} 的名称无效。");
        }

        if (string.IsNullOrWhiteSpace(asset.OriginalFileName) ||
            !string.Equals(Path.GetFileName(asset.OriginalFileName), asset.OriginalFileName, StringComparison.Ordinal) ||
            asset.OriginalFileName.Length > 260)
        {
            throw new InvalidDataException($"美术素材 {asset.Id} 的原始文件名无效。");
        }

        if (!IsSafeRelativePath(asset.RelativePath))
        {
            throw new InvalidDataException($"美术素材 {asset.Id} 的相对路径无效。");
        }

        if (string.IsNullOrWhiteSpace(asset.Extension) ||
            !ExtensionPattern().IsMatch(asset.Extension))
        {
            throw new InvalidDataException($"美术素材 {asset.Id} 的扩展名无效。");
        }

        var normalizedExtension = asset.Extension.ToLowerInvariant();
        if (normalizedExtension is not
            (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".tif" or ".tiff") ||
            !string.Equals(
                Path.GetExtension(asset.OriginalFileName),
                asset.Extension,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(asset.MediaType) ||
            !asset.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"美术素材 {asset.Id} 不是受支持的图片格式。");
        }

        var expectedPath = $"assets/{groupId}/{asset.Id}{asset.Extension}";
        if (!string.Equals(
                asset.RelativePath.Replace('\\', '/'),
                expectedPath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"美术素材 {asset.Id} 的路径不属于其风格组。");
        }

        if (!Sha256Pattern().IsMatch(asset.Sha256) || asset.Size < 0 || asset.ImportedAtUtc == default)
        {
            throw new InvalidDataException($"美术素材 {asset.Id} 的文件元数据无效。");
        }
    }

    private static bool IsStableId(string? value) =>
        value is { Length: 32 } && StableIdPattern().IsMatch(value);

    private static bool IsSafeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            return false;
        }

        var segments = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3 && segments.All(segment => segment is not "." and not "..");
    }

    [GeneratedRegex("^[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdPattern();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^\\.[A-Za-z0-9]{1,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPattern();
}
