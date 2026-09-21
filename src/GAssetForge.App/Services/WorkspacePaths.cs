using System.IO;
using GAssetForge.Core;
using GAssetForge.Core.Projects;

namespace GAssetForge.App.Services;

public sealed class WorkspacePaths
{
    public WorkspacePaths()
        : this(ResolveDefaultRootDirectory())
    {
    }

    public WorkspacePaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        ProjectDirectory = Path.Combine(RootDirectory, "Projects");
        AssetDirectory = Path.Combine(RootDirectory, "Assets");
        AiAssetDirectory = ProjectAssetLayout.GetAiRootFullPath(AssetDirectory);
        FinalAssetDirectory = ProjectAssetLayout.GetFinalRootFullPath(AssetDirectory);
        UserAssetDirectory = ProjectAssetLayout.GetUserRootFullPath(AssetDirectory);
        LegacyGeneratedAssetDirectory = Path.Combine(
            AssetDirectory,
            ProjectAssetLayout.LegacyGeneratedDirectoryName);
        GeneratedAssetDirectory = LegacyGeneratedAssetDirectory;
        GenerationDirectory = Path.Combine(RootDirectory, "Generation");
        GenerationReferenceSnapshotDirectory = Path.Combine(GenerationDirectory, "ReferenceSnapshots");
        WorkspaceDirectory = Path.Combine(RootDirectory, "Workspace");
        SettingsDirectory = Path.Combine(RootDirectory, "Settings");

        Directory.CreateDirectory(ProjectDirectory);
        Directory.CreateDirectory(AssetDirectory);
        Directory.CreateDirectory(AiAssetDirectory);
        Directory.CreateDirectory(FinalAssetDirectory);
        Directory.CreateDirectory(UserAssetDirectory);
        Directory.CreateDirectory(GenerationDirectory);
        Directory.CreateDirectory(GenerationReferenceSnapshotDirectory);
        Directory.CreateDirectory(WorkspaceDirectory);
        Directory.CreateDirectory(SettingsDirectory);
    }

    public string RootDirectory { get; }
    public string ProjectDirectory { get; }
    public string AssetDirectory { get; }

    public string AiAssetDirectory { get; }

    public string FinalAssetDirectory { get; }

    public string UserAssetDirectory { get; }

    /// <summary>Historical Assets/Generated location retained for compatibility reads.</summary>
    public string LegacyGeneratedAssetDirectory { get; }

    /// <summary>Compatibility alias for <see cref="LegacyGeneratedAssetDirectory"/>.</summary>
    public string GeneratedAssetDirectory { get; }
    public string GenerationDirectory { get; }
    public string GenerationReferenceSnapshotDirectory { get; }
    public string WorkspaceDirectory { get; }
    public string SettingsDirectory { get; }
    public string AutosaveProjectPath => Path.Combine(ProjectDirectory, "default.pfui.json");
    public string ProjectBackupPath => Path.Combine(ProjectDirectory, "default.pfui.json.bak");
    public string LayoutPath => Path.Combine(WorkspaceDirectory, "ui-layout-v1.json");
    public string LayoutBackupPath => Path.Combine(WorkspaceDirectory, "ui-layout-v1.json.bak");
    public string ImageProcessingPreviewDirectory =>
        Path.Combine(WorkspaceDirectory, ImageProcessingPreviewCache.DefaultDirectoryName);
    public string UserSettingsPath => Path.Combine(SettingsDirectory, "user-settings.json");

    private static string ResolveDefaultRootDirectory()
        => ResolveDefaultRootDirectory(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static string ResolveDefaultRootDirectory(string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        var canonicalRoot = Path.Combine(localApplicationData, ProductIdentity.DataDirectoryName, "Desktop");
        var legacyRoot = Path.Combine(localApplicationData, ProductIdentity.LegacyDataDirectoryName, "Desktop");

        // Preserve existing settings, projects, task queue and style library without a destructive
        // move. Fresh installations always start in the canonical GAssetForge root.
        return Directory.Exists(canonicalRoot) || !Directory.Exists(legacyRoot)
            ? canonicalRoot
            : legacyRoot;
    }
}
