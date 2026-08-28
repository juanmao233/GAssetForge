using GAssetForge.Core.Generation;

namespace GAssetForge.App.ViewModels;

/// <summary>
/// One editor-local part of an Art Workspace instruction. Unlike the generation contract this
/// also freezes the user-facing image name so the chip never changes when another canvas image is
/// selected or the source image is renamed later.
/// </summary>
public sealed record ArtWorkspaceInstructionDraftSegment
{
    public string Kind { get; init; } = ArtWorkspaceGenerationInstructionSegmentKinds.Text;

    public string Text { get; init; } = string.Empty;

    public string TokenId { get; init; } = string.Empty;

    public string ImageId { get; init; } = string.Empty;

    public string ImageName { get; init; } = string.Empty;

    public ArtWorkspaceGenerationRegion? Region { get; init; }

    public bool IsText => string.Equals(
        Kind,
        ArtWorkspaceGenerationInstructionSegmentKinds.Text,
        StringComparison.Ordinal);

    public bool IsImageReference => string.Equals(
        Kind,
        ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
        StringComparison.Ordinal);

    public string DisplayText => IsImageReference
        ? Region is null
            ? $"图片：{ImageName} · 整张图片"
            : $"图片：{ImageName} · 局部框选"
        : Text;

    public static ArtWorkspaceInstructionDraftSegment CreateText(string? text) => new()
    {
        Kind = ArtWorkspaceGenerationInstructionSegmentKinds.Text,
        Text = text ?? string.Empty
    };

    public static ArtWorkspaceInstructionDraftSegment CreateImageReference(
        string imageId,
        string imageName,
        ArtWorkspaceGenerationRegion? region = null,
        string? tokenId = null) => new()
    {
        Kind = ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
        TokenId = string.IsNullOrWhiteSpace(tokenId) ? Guid.NewGuid().ToString("N") : tokenId,
        ImageId = imageId ?? string.Empty,
        ImageName = imageName ?? string.Empty,
        Region = CloneRegion(region)
    };

    public ArtWorkspaceInstructionDraftSegment DeepClone() => this with
    {
        Region = CloneRegion(Region)
    };

    public ArtWorkspaceGenerationInstructionSegment ToGenerationSegment() => new()
    {
        Kind = Kind,
        Text = IsText ? Text : string.Empty,
        TokenId = IsImageReference ? TokenId : string.Empty,
        ImageId = IsImageReference ? ImageId : string.Empty,
        Region = IsImageReference ? CloneRegion(Region) : null
    };

    private static ArtWorkspaceGenerationRegion? CloneRegion(ArtWorkspaceGenerationRegion? region) =>
        region is null
            ? null
            : new ArtWorkspaceGenerationRegion
            {
                X = region.X,
                Y = region.Y,
                Width = region.Width,
                Height = region.Height
            };
}
