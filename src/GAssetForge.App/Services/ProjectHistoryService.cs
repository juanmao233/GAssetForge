using GAssetForge.Core.Projects;

namespace GAssetForge.App.Services;

public sealed class ProjectHistoryService
{
    private readonly int _capacity;
    private readonly LinkedList<string> _undo = [];
    private readonly LinkedList<string> _redo = [];

    public ProjectHistoryService(int capacity = 60)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "历史容量必须至少为 1。");
        }

        _capacity = capacity;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Record(UiProjectModel project)
    {
        RecordSnapshot(ProjectJson.Serialize(project));
    }

    internal void RecordSnapshot(string projectJson)
    {
        PushBounded(_undo, projectJson);
        _redo.Clear();
    }

    public UiProjectModel Undo(UiProjectModel current)
    {
        if (!CanUndo)
        {
            return current;
        }

        PushBounded(_redo, ProjectJson.Serialize(current));
        return PopLatest(_undo);
    }

    public UiProjectModel Redo(UiProjectModel current)
    {
        if (!CanRedo)
        {
            return current;
        }

        PushBounded(_undo, ProjectJson.Serialize(current));
        return PopLatest(_redo);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private void PushBounded(LinkedList<string> history, string projectJson)
    {
        history.AddLast(projectJson);
        while (history.Count > _capacity)
        {
            history.RemoveFirst();
        }
    }

    private static UiProjectModel PopLatest(LinkedList<string> history)
    {
        var latest = history.Last?.Value
            ?? throw new InvalidOperationException("历史记录为空。");
        history.RemoveLast();
        return ProjectJson.Deserialize(latest);
    }
}
