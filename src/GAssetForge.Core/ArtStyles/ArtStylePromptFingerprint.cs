using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GAssetForge.Core.ArtStyles;

/// <summary>
/// Builds the stable source identity for an analyzed style prompt. The fingerprint intentionally
/// excludes the group name and the generated sample so harmless renames and result replacement do
/// not invalidate the visual source definition.
/// </summary>
public static class ArtStylePromptFingerprint
{
    private const string FingerprintDomain = "gassetforge.art-style-prompt-source.v1";

    public static string Compute(ArtStyleGroupModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return Compute(group.Description, group.Prompt, group.Assets ?? []);
    }

    public static string Compute(
        string? description,
        string? manualPrompt,
        IEnumerable<ArtStyleAssetModel> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, "domain", FingerprintDomain);
        AppendField(hash, "description", NormalizeText(description));
        AppendField(hash, "manualPrompt", NormalizeText(manualPrompt));

        foreach (var asset in assets
                     .Where(static asset => asset is not null)
                     .OrderBy(static asset => asset.Id, StringComparer.Ordinal)
                     .ThenBy(static asset => asset.Sha256, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static asset => asset.MediaType, StringComparer.OrdinalIgnoreCase))
        {
            AppendField(hash, "assetId", NormalizeIdentity(asset.Id));
            AppendField(hash, "assetSha256", NormalizeIdentity(asset.Sha256));
            AppendField(hash, "assetMediaType", NormalizeIdentity(asset.MediaType));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var source = value.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(source.Length);
        var pendingSpace = false;
        foreach (var character in source)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string NormalizeIdentity(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static void AppendField(IncrementalHash hash, string name, string value)
    {
        AppendBytes(hash, Encoding.UTF8.GetBytes(name));
        AppendBytes(hash, Encoding.UTF8.GetBytes(value));
    }

    private static void AppendBytes(IncrementalHash hash, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
