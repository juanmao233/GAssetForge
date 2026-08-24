using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GAssetForge.App.Services;

public sealed class AvalonDockLayoutService(WorkspacePaths paths)
{
    private const int SchemaVersion = 1;
    private const int PresetRevision = 10;
    private const string Engine = "AvalonDock";
    private static readonly string[] RequiredContentIds =
        ["ui-tools", "ui-properties", "ui-layers", "ui-project", "art-style-library", "asset-generation", "background-removal", "pixel-grid-alignment", "workflow-blueprint", "mcp-agent-connection", "command-console"];
    private static readonly string[] OptionalDocumentContentIds =
        ["ui-canvas", "workspace-tilemap", "workspace-card-design", "workspace-parallel-perspective"];
    private static readonly string[] KnownContentIds =
        [.. RequiredContentIds, .. OptionalDocumentContentIds];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string? LastRecoverySource { get; private set; }

    public bool TryRestore(DockingManager manager, IReadOnlyDictionary<string, object> panelContent)
    {
        LastRecoverySource = null;
        if (TryRead(paths.LayoutPath, out var primary) && TryDeserialize(manager, primary!, panelContent))
        {
            return true;
        }

        if (TryRead(paths.LayoutBackupPath, out var backup) && TryDeserialize(manager, backup!, panelContent))
        {
            LastRecoverySource = "backup";
            return true;
        }

        LastRecoverySource = "default";
        return false;
    }

    public void Save(DockingManager manager)
    {
        var serializer = new XmlLayoutSerializer(manager);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        serializer.Serialize(writer);
        var envelope = new WorkspaceLayoutEnvelope
        {
            SchemaVersion = SchemaVersion,
            PresetRevision = PresetRevision,
            Engine = Engine,
            SavedAt = DateTimeOffset.Now,
            KnownContentIds = [.. KnownContentIds],
            LayoutXml = writer.ToString()
        };

        var backupPath = LastRecoverySource is null && TryRead(paths.LayoutPath, out _)
            ? paths.LayoutBackupPath
            : null;
        WriteAtomic(paths.LayoutPath, backupPath, JsonSerializer.Serialize(envelope, Options));
        LastRecoverySource = null;
    }

    public void Clear()
    {
        if (File.Exists(paths.LayoutPath))
        {
            File.Delete(paths.LayoutPath);
        }

        if (File.Exists(paths.LayoutBackupPath))
        {
            File.Delete(paths.LayoutBackupPath);
        }
    }

    private static bool TryDeserialize(
        DockingManager manager,
        WorkspaceLayoutEnvelope envelope,
        IReadOnlyDictionary<string, object> panelContent)
    {
        try
        {
            if (!KnownContentIds.All(contentId =>
                    envelope.KnownContentIds.Contains(contentId, StringComparer.Ordinal)) ||
                !RequiredContentIds.All(contentId =>
                    envelope.LayoutXml.Contains(contentId, StringComparison.Ordinal)))
            {
                return false;
            }

            var serializer = new XmlLayoutSerializer(manager);
            serializer.LayoutSerializationCallback += (_, args) =>
            {
                var contentId = args.Model.ContentId;
                if (contentId is not null && panelContent.TryGetValue(contentId, out var content))
                {
                    args.Content = content;
                }
                else
                {
                    args.Cancel = true;
                }
            };

            using var reader = new StringReader(envelope.LayoutXml);
            serializer.Deserialize(reader);
            var restoredContents = manager.Layout.Descendents()
                .OfType<LayoutContent>()
                .Concat(manager.Layout.Hidden)
                .Distinct()
                .ToArray();
            return restoredContents.All(content =>
                       KnownContentIds.Contains(content.ContentId, StringComparer.Ordinal)) &&
                   RequiredContentIds.All(requiredId => restoredContents.Count(content =>
                       string.Equals(content.ContentId, requiredId, StringComparison.Ordinal)) == 1) &&
                   OptionalDocumentContentIds.All(optionalId => restoredContents.Count(content =>
                       string.Equals(content.ContentId, optionalId, StringComparison.Ordinal)) <= 1);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRead(string path, out WorkspaceLayoutEnvelope? envelope)
    {
        envelope = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            envelope = JsonSerializer.Deserialize<WorkspaceLayoutEnvelope>(File.ReadAllText(path), Options);
            return envelope is
            {
                SchemaVersion: SchemaVersion,
                PresetRevision: PresetRevision,
                Engine: Engine,
                LayoutXml.Length: > 0
            };
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
            ?? throw new InvalidOperationException("无法确定布局文件目录。");
        Directory.CreateDirectory(directory);

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
                    File.Replace(temporaryPath, fullPath, Path.GetFullPath(backupPath), ignoreMetadataErrors: true);
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

    private sealed class WorkspaceLayoutEnvelope
    {
        public int SchemaVersion { get; set; }
        public int PresetRevision { get; set; }
        public string Engine { get; set; } = string.Empty;
        public DateTimeOffset SavedAt { get; set; }
        public List<string> KnownContentIds { get; set; } = [];
        public string LayoutXml { get; set; } = string.Empty;
    }
}
