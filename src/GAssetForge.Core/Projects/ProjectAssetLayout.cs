using System.IO;

namespace GAssetForge.Core.Projects;

/// <summary>
/// Defines the canonical, project-local asset taxonomy. New writes are separated into
/// AI run workspaces, final deliverables and user-managed project content, while frozen
/// legacy run paths remain recognizable for recovery.
/// </summary>
public static class ProjectAssetLayout
{
    public const string AiDirectoryName = "AI";
    public const string FinalDirectoryName = "Final";
    public const string UserDirectoryName = "User";
    public const string LegacyGeneratedDirectoryName = "Generated";

    public const string ArtWorkspaceCategory = "ArtWorkspace";
    public const string TilemapCategory = "Tilemap";
    public const string IconCategory = "Icon";
    public const string BlueprintCategory = "Blueprint";
    public const string GeneralCategory = "General";

    /// <summary>
    /// Category reported for a recognized legacy run. Historical Generated run paths did
    /// not encode their workflow, so no more specific category can be recovered from the path.
    /// </summary>
    public const string LegacyUncategorizedCategory = "LegacyUncategorized";

    private static readonly string[] KnownCategories =
    [
        ArtWorkspaceCategory,
        TilemapCategory,
        IconCategory,
        BlueprintCategory,
        GeneralCategory
    ];

    private static readonly HashSet<string> ReservedWindowsNames = BuildReservedWindowsNames();

    public static string GetWorkflowCategory(string workflowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        return workflowId.Trim().ToLowerInvariant() switch
        {
            "art-workspace" => ArtWorkspaceCategory,
            "tilemap" => TilemapCategory,
            "icon-design" => IconCategory,
            _ => throw new ArgumentException($"未知的资产生成工作流：{workflowId}", nameof(workflowId))
        };
    }

    public static string BuildAiRunRelativePath(string category, string runId)
    {
        var normalizedCategory = NormalizeCategory(category, allowWorkspaceOnlyCategories: true);
        var normalizedRunId = NormalizeGuid(runId, nameof(runId));
        return $"{AiDirectoryName}/{normalizedCategory}/{normalizedRunId}";
    }

    public static string BuildFinalBatchRelativePath(
        string category,
        string batchId,
        string? collectionRelativePath = null)
    {
        var normalizedCategory = NormalizeCategory(category, allowWorkspaceOnlyCategories: false);
        var normalizedBatchId = NormalizeGuid(batchId, nameof(batchId));
        if (string.IsNullOrWhiteSpace(collectionRelativePath))
        {
            return $"{FinalDirectoryName}/{normalizedCategory}/{normalizedBatchId}";
        }

        var normalizedCollection = NormalizeRelativeDirectory(
            collectionRelativePath,
            nameof(collectionRelativePath));
        return $"{FinalDirectoryName}/{normalizedCollection}/{normalizedCategory}/{normalizedBatchId}";
    }

    public static string GetAiRootFullPath(string assetRoot) =>
        BuildCanonicalRootFullPath(assetRoot, AiDirectoryName);

    public static string GetFinalRootFullPath(string assetRoot) =>
        BuildCanonicalRootFullPath(assetRoot, FinalDirectoryName);

    public static string GetUserRootFullPath(string assetRoot) =>
        BuildCanonicalRootFullPath(assetRoot, UserDirectoryName);

    public static bool IsRecognizedRunRelativePath(
        string relativePath,
        string batchId,
        out string category)
    {
        category = string.Empty;
        if (!TryNormalizeGuid(batchId, out var normalizedBatchId) ||
            !TryNormalizeRelativeDirectory(relativePath, out var normalizedPath))
        {
            return false;
        }

        var segments = normalizedPath.Split('/');
        if (segments.Length == 3 &&
            PathSegmentEquals(segments[0], AiDirectoryName) &&
            TryNormalizeCategory(
                segments[1],
                allowWorkspaceOnlyCategories: true,
                out var canonicalCategory) &&
            string.Equals(segments[2], normalizedBatchId, StringComparison.OrdinalIgnoreCase))
        {
            category = canonicalCategory;
            return true;
        }

        if (segments.Length == 4 &&
            PathSegmentEquals(segments[0], LegacyGeneratedDirectoryName) &&
            PathSegmentEquals(segments[2], "Runs") &&
            (PathSegmentEquals(segments[1], ProductIdentity.ProductName) ||
             PathSegmentEquals(segments[1], ProductIdentity.LegacyProductName)) &&
            string.Equals(segments[3], normalizedBatchId, StringComparison.OrdinalIgnoreCase))
        {
            category = LegacyUncategorizedCategory;
            return true;
        }

        return false;
    }

    private static string BuildCanonicalRootFullPath(string assetRoot, string directoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        if (!Path.IsPathFullyQualified(assetRoot))
        {
            throw new ArgumentException("Assets 根目录必须是绝对路径。", nameof(assetRoot));
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetRoot));
        return Path.Combine(normalizedRoot, directoryName);
    }

    private static string NormalizeCategory(string category, bool allowWorkspaceOnlyCategories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        if (TryNormalizeCategory(
                category,
                allowWorkspaceOnlyCategories,
                out var normalizedCategory))
        {
            return normalizedCategory;
        }

        throw new ArgumentException($"未知的资产分类：{category}", nameof(category));
    }

    private static bool TryNormalizeCategory(
        string category,
        bool allowWorkspaceOnlyCategories,
        out string normalizedCategory)
    {
        normalizedCategory = KnownCategories.FirstOrDefault(candidate =>
            string.Equals(candidate, category, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return normalizedCategory.Length > 0 &&
               (allowWorkspaceOnlyCategories ||
                (!string.Equals(normalizedCategory, BlueprintCategory, StringComparison.Ordinal) &&
                 !string.Equals(normalizedCategory, GeneralCategory, StringComparison.Ordinal)));
    }

    private static string NormalizeGuid(string value, string parameterName)
    {
        if (!TryNormalizeGuid(value, out var normalized))
        {
            throw new ArgumentException("运行或批次 ID 必须是 32 位 GUID。", parameterName);
        }

        return normalized;
    }

    private static bool TryNormalizeGuid(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!Guid.TryParseExact(value, "N", out var parsed))
        {
            return false;
        }

        normalized = parsed.ToString("N");
        return true;
    }

    private static string NormalizeRelativeDirectory(string value, string parameterName)
    {
        if (!TryNormalizeRelativeDirectory(value, out var normalized))
        {
            throw new ArgumentException(
                "Final 集合路径必须是安全、非空且位于 Final 根目录下的相对目录。",
                parameterName);
        }

        return normalized;
    }

    private static bool TryNormalizeRelativeDirectory(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 ||
            value.StartsWith('/') || value.StartsWith('\\') ||
            Path.IsPathFullyQualified(value) ||
            (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':'))
        {
            return false;
        }

        var segments = value.Replace('\\', '/').Split('/');
        if (segments.Length == 0 || segments.Any(segment => !IsSafeDirectorySegment(segment)))
        {
            return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }

    private static bool IsSafeDirectorySegment(string segment)
    {
        if (segment.Length is < 1 or > 120 ||
            segment is "." or ".." ||
            !string.Equals(segment, segment.Trim(), StringComparison.Ordinal) ||
            segment.EndsWith('.') ||
            segment.Any(character =>
                char.IsControl(character) ||
                character is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
        {
            return false;
        }

        var deviceName = segment.Split('.', 2)[0].TrimEnd(' ');
        return !ReservedWindowsNames.Contains(deviceName);
    }

    private static bool PathSegmentEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> BuildReservedWindowsNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$"
        };
        for (var index = 1; index <= 9; index++)
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }

        return names;
    }
}
