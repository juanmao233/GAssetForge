using GAssetForge.Core.Projects;
using System.IO;
using System.Text;

namespace GAssetForge.App.Services;

public sealed class JsonProjectRepository
{
    private readonly WorkspacePaths _workspacePaths;
    private string _primaryPath;
    private string _backupPath;
    private ProjectManifest? _projectManifest;

    public JsonProjectRepository(WorkspacePaths paths, string? projectRoot = null)
    {
        _workspacePaths = paths ?? throw new ArgumentNullException(nameof(paths));
        _primaryPath = paths.AutosaveProjectPath;
        _backupPath = paths.ProjectBackupPath;
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            SwitchProject(projectRoot);
        }
    }

    public string? LastRecoverySource { get; private set; }

    public string? ProjectRoot { get; private set; }

    public string ProjectFilePath => _primaryPath;

    public string ProjectBackupPath => _backupPath;

    public UiProjectModel LoadOrCreate()
    {
        LastRecoverySource = null;
        if (TryRead(_primaryPath, out var primary))
        {
            return primary!;
        }

        if (TryRead(_backupPath, out var backup))
        {
            LastRecoverySource = "backup";
            return backup!;
        }

        LastRecoverySource = "default";
        var project = DefaultUiProjectFactory.Create();
        if (_projectManifest is not null)
        {
            project.Id = _projectManifest.Id;
            project.Name = _projectManifest.Name;
            ProjectValidator.Validate(project);
        }

        return project;
    }

    public UiProjectModel Import(string path)
    {
        var project = ProjectJson.Deserialize(File.ReadAllText(path));
        if (_projectManifest is not null)
        {
            project.Id = _projectManifest.Id;
            project.Name = _projectManifest.Name;
        }

        ProjectValidator.Validate(project);
        return project;
    }

    public void Export(UiProjectModel project, string path)
    {
        ProjectValidator.Validate(project);
        WriteAtomic(path, null, ProjectJson.Serialize(project));
    }

    public void Save(UiProjectModel project)
    {
        ProjectValidator.Validate(project);
        if (_projectManifest is not null &&
            (!string.Equals(project.Id, _projectManifest.Id, StringComparison.Ordinal) ||
             !string.Equals(project.Name, _projectManifest.Name, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("UI 项目身份与当前 GAssetForge 项目清单不一致。");
        }

        var backupPath = TryRead(_primaryPath, out _)
            ? _backupPath
            : null;
        WriteAtomic(_primaryPath, backupPath, ProjectJson.Serialize(project));
        LastRecoverySource = null;
    }

    public void SwitchProject(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            ProjectRoot = null;
            _projectManifest = null;
            _primaryPath = _workspacePaths.AutosaveProjectPath;
            _backupPath = _workspacePaths.ProjectBackupPath;
            LastRecoverySource = null;
            return;
        }

        var workspaceService = new ProjectWorkspaceService();
        if (!workspaceService.TryLoad(projectRoot, out var manifest, out var paths, out var reason))
        {
            throw new InvalidDataException($"无法切换 UI 项目仓库：{reason}");
        }

        ProjectRoot = paths.RootDirectory;
        _projectManifest = manifest;
        _primaryPath = paths.UiProjectPath;
        _backupPath = paths.UiProjectBackupPath;
        LastRecoverySource = null;
    }

    public void SwitchProjectRoot(string? projectRoot) => SwitchProject(projectRoot);

    private bool TryRead(string path, out UiProjectModel? project)
    {
        project = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            project = ProjectJson.Deserialize(File.ReadAllText(path));
            ProjectValidator.Validate(project);
            if (_projectManifest is not null &&
                (!string.Equals(project.Id, _projectManifest.Id, StringComparison.Ordinal) ||
                 !string.Equals(project.Name, _projectManifest.Name, StringComparison.Ordinal)))
            {
                project = null;
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteAtomic(string path, string? backupPath, string content)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("无法确定项目文件目录。");
        Directory.CreateDirectory(directory);
        EnsureNotReparsePointIfExists(fullPath, "UI 项目文件");
        if (backupPath is not null)
        {
            EnsureNotReparsePointIfExists(Path.GetFullPath(backupPath), "UI 项目备份");
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                if (backupPath is not null)
                {
                    var fullBackupPath = Path.GetFullPath(backupPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullBackupPath) ?? directory);
                    File.Replace(temporaryPath, fullPath, fullBackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, fullPath, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void EnsureNotReparsePointIfExists(string path, string label)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{label} 不能是符号链接或目录联接。");
        }
    }
}
