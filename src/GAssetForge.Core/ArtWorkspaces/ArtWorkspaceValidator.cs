using System.Globalization;

namespace GAssetForge.Core.ArtWorkspaces;

public static class ArtWorkspaceValidator
{
    public const int MaximumAssets = 128;
    public const int MaximumImages = 128;
    public const int MaximumMessages = 1000;
    public const long MaximumSourcePixelCount = 67_108_864L;
    public const long MaximumSerializedDocumentBytes = 16L * 1024L * 1024L;

    public static void Validate(ArtWorkspaceDocument document)
    {
        if (document is null)
        {
            throw new InvalidDataException("美术工作区文档不能为空。");
        }
        if (document.SchemaVersion != ArtWorkspaceJson.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"不支持的美术工作区版本：{document.SchemaVersion}。");
        }

        ValidateIdentifier(document.Id, "工作区 ID");
        ValidateText(document.Name, "工作区名称", 160, allowEmpty: false);
        ValidateFiniteRange(document.CanvasWidth, 1024d, 65536d, "画布宽度");
        ValidateFiniteRange(document.CanvasHeight, 1024d, 65536d, "画布高度");
        ValidateViewport(document.Viewport);
        ValidateOutputFrame(document.OutputFrame);

        document.Assets ??= [];
        document.Images ??= [];
        document.Conversation ??= [];
        if (document.Assets.Count > MaximumAssets || document.Images.Count > MaximumImages)
        {
            throw new InvalidDataException($"美术工作区最多允许 {MaximumImages} 张图片。");
        }

        if (document.Conversation.Count > MaximumMessages)
        {
            throw new InvalidDataException($"美术工作区最多保留 {MaximumMessages} 条对话记录。");
        }

        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in document.Assets)
        {
            ValidateManagedAsset(asset);
            if (!assetIds.Add(asset.Id))
            {
                throw new InvalidDataException($"存在重复的素材 ID：{asset.Id}。");
            }
        }

        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        var imagesById = new Dictionary<string, ArtWorkspaceImageItem>(StringComparer.Ordinal);
        foreach (var image in document.Images)
        {
            ValidateImage(image);
            if (!imageIds.Add(image.Id))
            {
                throw new InvalidDataException($"存在重复的图片对象 ID：{image.Id}。");
            }

            if (!assetIds.Contains(image.AssetId))
            {
                throw new InvalidDataException($"图片对象 {image.Id} 引用了不存在的素材 {image.AssetId}。");
            }

            imagesById.Add(image.Id, image);
        }

        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in document.Conversation)
        {
            ValidateConversationMessage(message);
            if (!messageIds.Add(message.Id))
            {
                throw new InvalidDataException($"存在重复的对话 ID：{message.Id}。");
            }
        }

        if (!string.IsNullOrWhiteSpace(document.SelectedImageId) &&
            !imageIds.Contains(document.SelectedImageId))
        {
            throw new InvalidDataException("所选图片 ID 不属于当前工作区。");
        }


        if (document.SelectedImageRegion is { } selectedRegion)
        {
            if (string.IsNullOrWhiteSpace(document.SelectedImageId) ||
                !string.Equals(selectedRegion.ImageId, document.SelectedImageId, StringComparison.Ordinal) ||
                !imagesById.ContainsKey(selectedRegion.ImageId))
            {
                throw new InvalidDataException("框选区域必须绑定当前工作区的所选图片。");
            }

            try
            {
                ArtWorkspaceImageRegionGeometry.ValidateRegion(selectedRegion, selectedRegion.ImageId);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException($"图片框选区域无效：{exception.Message}", exception);
            }
        }
    }

    private static void ValidateViewport(ArtWorkspaceViewport viewport)
    {
        if (viewport is null)
        {
            throw new InvalidDataException("美术工作区视口不能为空。");
        }

        ValidateFiniteRange(viewport.PanX, -1_000_000d, 1_000_000d, "视口 X 偏移");
        ValidateFiniteRange(viewport.PanY, -1_000_000d, 1_000_000d, "视口 Y 偏移");
        ValidateFiniteRange(viewport.Zoom, 0.1d, 4d, "视口缩放");
    }

    private static void ValidateOutputFrame(ArtWorkspaceOutputFrame frame)
    {
        if (frame is null)
        {
            throw new InvalidDataException("美术工作区输出框不能为空。");
        }

        ValidateIdentifier(frame.Id, "输出框 ID");
        ValidateText(frame.Name, "输出框名称", 160, allowEmpty: false);
        ValidateCoordinate(frame.X, "输出框 X");
        ValidateCoordinate(frame.Y, "输出框 Y");
        ValidateFiniteRange(frame.Width, 16d, 16384d, "输出框宽度");
        ValidateFiniteRange(frame.Height, 16d, 16384d, "输出框高度");
        ValidateText(frame.BackgroundHex, "输出框背景色", 16, allowEmpty: false);
        ValidateRelativePath(frame.OutputRelativeDirectory, "输出目录");
        var normalizedOutputDirectory = frame.OutputRelativeDirectory.Replace('\\', '/').TrimEnd('/');
        if (!normalizedOutputDirectory.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("输出目录必须位于当前项目的 Assets 根目录内。");
        }
    }

    private static void ValidateManagedAsset(ArtWorkspaceManagedAsset asset)
    {
        if (asset is null)
        {
            throw new InvalidDataException("美术工作区素材记录不能为空。");
        }

        ValidateIdentifier(asset.Id, "素材 ID");
        ValidateRelativePath(asset.RelativePath, "素材相对路径");
        ValidateText(asset.OriginalFileName, "素材原始名称", 260, allowEmpty: false);
        ValidateText(asset.MediaType, "素材媒体类型", 128, allowEmpty: false);
        if (string.IsNullOrWhiteSpace(asset.Sha256) ||
            asset.Sha256.Length != 64 ||
            !asset.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("素材 SHA-256 必须是 64 位十六进制文本。");
        }

        if (asset.SizeBytes <= 0 || asset.SizeBytes > 512L * 1024L * 1024L)
        {
            throw new InvalidDataException("素材文件大小不在允许范围内。");
        }

        if (asset.PixelWidth <= 0 || asset.PixelWidth > 65536 ||
            asset.PixelHeight <= 0 || asset.PixelHeight > 65536)
        {
            throw new InvalidDataException("素材像素尺寸不在允许范围内。");
        }

        if ((long)asset.PixelWidth * asset.PixelHeight > MaximumSourcePixelCount)
        {
            throw new InvalidDataException(
                $"素材像素总数不能超过 {MaximumSourcePixelCount:N0}。请先裁切或缩小图片。");
        }
    }

    private static void ValidateImage(ArtWorkspaceImageItem image)
    {
        if (image is null)
        {
            throw new InvalidDataException("美术工作区图片对象不能为空。");
        }

        ValidateIdentifier(image.Id, "图片对象 ID");
        ValidateIdentifier(image.AssetId, "图片素材 ID");
        ValidateText(image.Name, "图片对象名称", 260, allowEmpty: false);
        ValidateCoordinate(image.X, "图片 X");
        ValidateCoordinate(image.Y, "图片 Y");
        ValidateFiniteRange(image.Width, 1d, 65536d, "图片宽度");
        ValidateFiniteRange(image.Height, 1d, 65536d, "图片高度");
        ValidateFiniteRange(image.RotationDegrees, -36000d, 36000d, "图片旋转角");
        ValidateFiniteRange(image.Opacity, 0d, 1d, "图片透明度");
        if (image.ZIndex is < 0 or > 1_000_000)
        {
            throw new InvalidDataException("图片层级不在允许范围内。");
        }
    }

    private static void ValidateConversationMessage(ArtWorkspaceConversationMessage message)
    {
        if (message is null)
        {
            throw new InvalidDataException("美术工作区对话记录不能为空。");
        }

        ValidateIdentifier(message.Id, "对话 ID");
        if (!Enum.IsDefined(message.Role))
        {
            throw new InvalidDataException("对话角色无效。");
        }

        ValidateText(message.Text, "对话内容", 16000, allowEmpty: false);
        ValidateText(message.State, "对话状态", 64, allowEmpty: false);
        ValidateConversationSegments(message.Segments);
        if (!string.IsNullOrWhiteSpace(message.ReferenceImageId))
        {
            ValidateIdentifier(message.ReferenceImageId, "对话引用图片 ID");
            if (!Guid.TryParseExact(message.ReferenceImageId, "N", out _))
            {
                throw new InvalidDataException("对话引用图片 ID 必须是 32 位 GUID。");
            }
        }

        if (!string.IsNullOrWhiteSpace(message.ReferenceSummary))
        {
            ValidateText(message.ReferenceSummary, "对话引用摘要", 512, allowEmpty: false);
        }

        if (string.IsNullOrWhiteSpace(message.ReferenceImageId) !=
            string.IsNullOrWhiteSpace(message.ReferenceSummary))
        {
            throw new InvalidDataException("对话图片引用 ID 与可见摘要必须同时存在。");
        }
    }

    private static void ValidateConversationSegments(
        IReadOnlyCollection<ArtWorkspaceConversationSegment>? segments)
    {
        if (segments is null)
        {
            throw new InvalidDataException("对话有序片段集合不能为空。 ");
        }

        if (segments.Count == 0)
        {
            return;
        }

        if (segments.Count > 256)
        {
            throw new InvalidDataException("单条对话最多保留 256 个有序片段。 ");
        }

        var tokenIds = new HashSet<string>(StringComparer.Ordinal);
        var textLength = 0;
        var imageReferenceCount = 0;
        var hasSemanticContent = false;
        foreach (var segment in segments)
        {
            if (segment is null)
            {
                throw new InvalidDataException("对话有序片段不能包含空项。 ");
            }

            if (string.Equals(
                    segment.Kind,
                    ArtWorkspaceConversationSegmentKinds.Text,
                    StringComparison.Ordinal))
            {
                ValidateText(segment.Text, "对话文字片段", 16000, allowEmpty: true);
                if (!string.IsNullOrEmpty(segment.TokenId) ||
                    !string.IsNullOrEmpty(segment.ImageId) ||
                    !string.IsNullOrEmpty(segment.ImageName) ||
                    !string.IsNullOrEmpty(segment.DisplayLabel) ||
                    segment.Region is not null)
                {
                    throw new InvalidDataException("对话文字片段不能携带图片引用字段。 ");
                }

                textLength = checked(textLength + segment.Text.Length);
                hasSemanticContent |= !string.IsNullOrWhiteSpace(segment.Text);
                continue;
            }

            if (!string.Equals(
                    segment.Kind,
                    ArtWorkspaceConversationSegmentKinds.ImageReference,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException($"未知的对话有序片段类型：{segment.Kind}。 ");
            }

            if (!string.IsNullOrEmpty(segment.Text) ||
                !Guid.TryParseExact(segment.TokenId, "N", out _) ||
                !Guid.TryParseExact(segment.ImageId, "N", out _) ||
                !tokenIds.Add(segment.TokenId))
            {
                throw new InvalidDataException("对话图片 token 的内容、tokenId 或 imageId 无效。 ");
            }

            ValidateText(segment.ImageName, "对话引用图片名称", 260, allowEmpty: false);
            ValidateText(segment.DisplayLabel, "对话引用显示标签", 16, allowEmpty: false);
            if (segment.Region is { } region)
            {
                try
                {
                    ArtWorkspaceImageRegionGeometry.ValidateRegion(region, segment.ImageId);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException(
                        $"对话图片 token 的冻结区域无效：{exception.Message}",
                        exception);
                }
            }

            imageReferenceCount++;
            if (imageReferenceCount > ArtWorkspaceInstructionSegmentSemantics.MaximumImageReferenceCount)
            {
                throw new InvalidDataException(
                    $"单条对话最多保留 {ArtWorkspaceInstructionSegmentSemantics.MaximumImageReferenceCount} 个图片引用。 ");
            }

            hasSemanticContent = true;
        }

        var semanticLength = ArtWorkspaceInstructionSegmentSemantics.MeasureSemanticLength(
            textLength,
            imageReferenceCount);
        if (!hasSemanticContent ||
            semanticLength > ArtWorkspaceInstructionSegmentSemantics.MaximumSemanticLength)
        {
            throw new InvalidDataException(
                $"对话有序片段为空或语义长度超过 {ArtWorkspaceInstructionSegmentSemantics.MaximumSemanticLength}。 ");
        }
    }

    private static void ValidateIdentifier(string value, string label) =>
        ValidateText(value, label, 128, allowEmpty: false);

    private static void ValidateCoordinate(double value, string label) =>
        ValidateFiniteRange(value, -100_000d, 100_000d, label);

    private static void ValidateFiniteRange(double value, double minimum, double maximum, string label)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"{label}必须在 {minimum.ToString(CultureInfo.InvariantCulture)} 到 " +
                $"{maximum.ToString(CultureInfo.InvariantCulture)} 之间。");
        }
    }

    private static void ValidateText(string value, string label, int maximumLength, bool allowEmpty)
    {
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maximumLength)
        {
            throw new InvalidDataException($"{label}无效或超过 {maximumLength} 个字符。");
        }
    }

    private static void ValidateRelativePath(string value, string label)
    {
        ValidateText(value, label, 32768, allowEmpty: false);
        try
        {
            if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                throw new InvalidDataException($"{label}包含无效路径字符。");
            }

            if (Path.IsPathRooted(value))
            {
                throw new InvalidDataException($"{label}必须是受管根目录内的相对路径。");
            }

            var normalized = value.Replace('\\', '/');
            if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
            {
                throw new InvalidDataException($"{label}不能包含相对路径跳转。");
            }
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"{label}包含无效路径字符。", exception);
        }
    }
}
