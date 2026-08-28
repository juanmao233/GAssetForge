namespace GAssetForge.Core.ArtWorkspaces;

public enum ArtWorkspaceMessageRole
{
    User,
    Workbench,
    Agent
}

public static class ArtWorkspaceConversationSegmentKinds
{
    public const string Text = "text";

    public const string ImageReference = "image-reference";
}

/// <summary>
/// Shared size accounting for inline image chips. A chip has a non-zero semantic cost so callers
/// cannot bypass the instruction limit by replacing arbitrary text with thousands of references.
/// </summary>
public static class ArtWorkspaceInstructionSegmentSemantics
{
    public const int MaximumSemanticLength = 16_000;

    public const int ImageReferenceSemanticLength = 32;

    public const int MaximumImageReferenceCount = 64;

    public static int MeasureSemanticLength(int textLength, int imageReferenceCount)
    {
        if (textLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(textLength));
        }

        if (imageReferenceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageReferenceCount));
        }

        return checked(textLength + imageReferenceCount * ImageReferenceSemanticLength);
    }
}

public sealed class ArtWorkspaceDocument
{
    public int SchemaVersion { get; set; } = ArtWorkspaceJson.CurrentSchemaVersion;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "美术工作区";

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public double CanvasWidth { get; set; } = 8192d;

    public double CanvasHeight { get; set; } = 8192d;

    public ArtWorkspaceViewport Viewport { get; set; } = new();

    public ArtWorkspaceOutputFrame OutputFrame { get; set; } = new();

    public List<ArtWorkspaceManagedAsset> Assets { get; set; } = [];

    public List<ArtWorkspaceImageItem> Images { get; set; } = [];

    public List<ArtWorkspaceConversationMessage> Conversation { get; set; } = [];

    public string? SelectedImageId { get; set; }

    /// <summary>
    /// Optional local-space region on the currently selected image. Coordinates are normalized
    /// against the unrotated image bounds so the same source pixels remain selected when the
    /// canvas object is moved, resized, or rotated.
    /// </summary>
    public ArtWorkspaceImageRegionSelection? SelectedImageRegion { get; set; }
}

public sealed class ArtWorkspaceViewport
{
    public double PanX { get; set; } = 48d;

    public double PanY { get; set; } = 48d;

    public double Zoom { get; set; } = 0.72d;
}

public sealed class ArtWorkspaceOutputFrame
{
    public string Id { get; set; } = "output-frame";

    public string Name { get; set; } = "新美术资源输出框";

    public double X { get; set; } = 640d;

    public double Y { get; set; } = 420d;

    public double Width { get; set; } = 1024d;

    public double Height { get; set; } = 1024d;

    public string BackgroundHex { get; set; } = "#20242C";

    public string OutputRelativeDirectory { get; set; } = "Assets/Generated/ArtWorkspace";
}

public sealed class ArtWorkspaceManagedAsset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string RelativePath { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public string MediaType { get; set; } = "image/png";

    public string Sha256 { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int PixelWidth { get; set; }

    public int PixelHeight { get; set; }

    public DateTimeOffset ImportedAtUtc { get; set; }
}

public sealed class ArtWorkspaceImageItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string AssetId { get; set; } = string.Empty;

    public string Name { get; set; } = "参考图片";

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 320d;

    public double Height { get; set; } = 320d;

    public double RotationDegrees { get; set; }

    public double Opacity { get; set; } = 1d;

    public int ZIndex { get; set; }

    public bool IsLocked { get; set; }

    public bool IsVisible { get; set; } = true;
}

public sealed class ArtWorkspaceImageRegionSelection
{
    public string ImageId { get; set; } = string.Empty;

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 1d;

    public double Height { get; set; } = 1d;
}

public sealed class ArtWorkspaceConversationMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ArtWorkspaceMessageRole Role { get; set; }

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string State { get; set; } = "captured";

    /// <summary>
    /// Structured, ordered user instruction content. Older workspace JSON omits this property and
    /// safely falls back to <see cref="Text"/>. Image names and labels are frozen for coordinate-
    /// free history display even if the canvas image is renamed or removed later.
    /// </summary>
    public List<ArtWorkspaceConversationSegment> Segments { get; set; } = [];

    public string? ReferenceImageId { get; set; }

    /// <summary>
    /// User-facing, coordinate-free summary of the image/region frozen with this message.
    /// Historical conversation remains readable even if the canvas object is later removed.
    /// </summary>
    public string? ReferenceSummary { get; set; }
}

public sealed class ArtWorkspaceConversationSegment
{
    public string Kind { get; set; } = ArtWorkspaceConversationSegmentKinds.Text;

    public string Text { get; set; } = string.Empty;

    public string TokenId { get; set; } = string.Empty;

    public string ImageId { get; set; } = string.Empty;

    public string ImageName { get; set; } = string.Empty;

    public string DisplayLabel { get; set; } = string.Empty;

    public ArtWorkspaceImageRegionSelection? Region { get; set; }
}

public static class ArtWorkspaceDocumentFactory
{
    public static ArtWorkspaceDocument Create(TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new ArtWorkspaceDocument
        {
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Conversation =
            [
                new ArtWorkspaceConversationMessage
                {
                    Role = ArtWorkspaceMessageRole.Workbench,
                    Text = "工作区已就绪。可导入参考图片，并在属性面板记录希望创建的美术内容。",
                    CreatedAtUtc = now,
                    State = "ready"
                }
            ]
        };
    }
}
