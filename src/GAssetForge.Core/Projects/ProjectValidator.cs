namespace GAssetForge.Core.Projects;

public static class ProjectValidator
{
    public static void Validate(UiProjectModel project)
    {
        if (project.SchemaVersion != 2)
        {
            throw new InvalidDataException($"不支持的项目版本：{project.SchemaVersion}。");
        }

        if (!project.DocumentsById.TryGetValue(project.ActiveDocumentId, out var document))
        {
            throw new InvalidDataException("缺少活动文档。");
        }

        if (document.Canvas.Width <= 0 || document.Canvas.Height <= 0)
        {
            throw new InvalidDataException("画布尺寸无效。");
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string id, string? expectedParent)
        {
            if (visited.Contains(id))
            {
                throw new InvalidDataException($"图层被重复挂载：{id}。");
            }

            if (!document.LayersById.TryGetValue(id, out var layer))
            {
                throw new InvalidDataException($"图层不存在：{id}。");
            }

            if (!string.Equals(layer.ParentId, expectedParent, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"图层父级引用无效：{id}。");
            }

            if (!visiting.Add(id))
            {
                throw new InvalidDataException($"图层存在循环引用：{id}。");
            }

            if (layer.Transform is { } transform &&
                (transform.Width <= 0 || transform.Height <= 0 || !double.IsFinite(transform.X) || !double.IsFinite(transform.Y)))
            {
                throw new InvalidDataException($"图层尺寸或位置无效：{id}。");
            }

            foreach (var childId in layer.ChildIds)
            {
                Visit(childId, id);
            }

            visiting.Remove(id);
            visited.Add(id);
        }

        foreach (var rootId in document.RootIds)
        {
            Visit(rootId, null);
        }

        if (visited.Count != document.LayersById.Count)
        {
            throw new InvalidDataException("项目包含未挂载图层。");
        }

        if (!project.Library.FoldersById.ContainsKey(project.Library.ActiveFolderId))
        {
            throw new InvalidDataException("活动资源文件夹不存在。");
        }
    }
}
