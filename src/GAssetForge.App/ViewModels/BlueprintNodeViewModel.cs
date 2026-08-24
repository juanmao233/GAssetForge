using GAssetForge.App.Infrastructure;
using GAssetForge.Core.Blueprints;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace GAssetForge.App.ViewModels;

public sealed class BlueprintNodeViewModel : ObservableObject
{
    private readonly BlueprintViewModel _owner;
    private readonly AsyncRelayCommand _executeCommand;
    private bool _isSelected;

    internal BlueprintNodeViewModel(BlueprintViewModel owner, BlueprintNode model)
    {
        _owner = owner;
        Model = model;
        AddInputPortCommand = new RelayCommand(() => _owner.AddPort(this, BlueprintPortDirection.Input));
        AddOutputPortCommand = new RelayCommand(() => _owner.AddPort(this, BlueprintPortDirection.Output));
        RemoveCommand = new RelayCommand(() => _owner.RemoveNode(this), () => !IsStart);
        CaptureFunctionMembersCommand = new RelayCommand(() => _owner.CaptureFunctionMembers(this));
        AddVariableCommand = new RelayCommand(AddVariable);
        _executeCommand = new AsyncRelayCommand(
            () => _owner.ExecuteAsync(Id),
            () => IsStart && !_owner.IsExecuting);
        ExecuteCommand = _executeCommand;
        RebuildPorts();
        RebuildVariables();
    }

    internal BlueprintNode Model { get; }

    public string Id => Model.Id;

    public bool IsStart => Model.Role == BlueprintNodeRole.Start;

    public bool IsDescription => Model.Type == BlueprintNodeType.Description;

    public bool IsTool => Model.Type == BlueprintNodeType.Tool;

    public bool IsFunctionCollection => Model.Type == BlueprintNodeType.FunctionCollection;

    public bool IsLogic => Model.Type == BlueprintNodeType.Logic;

    public string TypeLabel => IsStart
        ? "系统起始蓝图"
        : Model.Type switch
        {
            BlueprintNodeType.Description => "说明蓝图",
            BlueprintNodeType.Tool => "工具类蓝图",
            BlueprintNodeType.FunctionCollection => "功能集合蓝图",
            BlueprintNodeType.Logic => "逻辑类蓝图",
            _ => "蓝图"
        };

    public string AccentHex => IsStart
        ? "#35C98F"
        : Model.Type switch
        {
            BlueprintNodeType.Description => "#65ADF2",
            BlueprintNodeType.Tool => "#53D4C8",
            BlueprintNodeType.FunctionCollection => "#9678FF",
            BlueprintNodeType.Logic => "#E2AB63",
            _ => "#65ADF2"
        };

    public double X => Model.X;

    public double Y => Model.Y;

    public double Width => Model.Width;

    public string Title
    {
        get => Model.Title;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "未命名蓝图" : value.Trim();
            next = next[..Math.Min(next.Length, 160)];
            if (string.Equals(Model.Title, next, StringComparison.Ordinal))
            {
                return;
            }

            Model.Title = next;
            OnPropertyChanged();
            _owner.NodeChanged(this);
        }
    }

    public string TagsText
    {
        get => string.Join(", ", Model.Tags);
        set
        {
            var tags = (value ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(tag => tag.Length > 0)
                .Select(tag => tag[..Math.Min(tag.Length, 64)])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .ToList();
            if (Model.Tags.SequenceEqual(tags))
            {
                return;
            }

            Model.Tags = tags;
            OnPropertyChanged();
            _owner.NodeChanged(this);
        }
    }

    public string Description
    {
        get => Model.Description;
        set
        {
            if (string.Equals(Model.Description, value, StringComparison.Ordinal))
            {
                return;
            }

            var next = value ?? string.Empty;
            Model.Description = next[..Math.Min(next.Length, 32_000)];
            OnPropertyChanged();
            _owner.NodeChanged(this);
        }
    }

    public string ToolContractId
    {
        get => Model.ToolContractId;
        set
        {
            if (string.Equals(Model.ToolContractId, value, StringComparison.Ordinal))
            {
                return;
            }

            var definition = BlueprintToolCatalog.Find(value);
            BlueprintGraphFactory.ApplyToolDefinition(Model, definition);
            OnPropertyChanged();
            RebuildVariables();
            _owner.NodeChanged(this);
        }
    }

    public string FunctionKey
    {
        get => Model.FunctionKey;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? $"function.{Id}" : value.Trim();
            next = next[..Math.Min(next.Length, 160)];
            if (string.Equals(Model.FunctionKey, next, StringComparison.Ordinal))
            {
                return;
            }

            Model.FunctionKey = next;
            OnPropertyChanged();
            _owner.NodeChanged(this);
        }
    }

    public string FunctionMembersText => Model.FunctionMemberNodeIds.Count == 0
        ? "尚未收集成员"
        : $"已保存 {Model.FunctionMemberNodeIds.Count} 个成员：{string.Join(", ", Model.FunctionMemberNodeIds.Select(id => id[..Math.Min(8, id.Length)]))}";

    public string ConditionText
    {
        get => Model.ConditionText;
        set
        {
            if (string.Equals(Model.ConditionText, value, StringComparison.Ordinal))
            {
                return;
            }

            var next = value ?? string.Empty;
            Model.ConditionText = next[..Math.Min(next.Length, 16_000)];
            OnPropertyChanged();
            _owner.NodeChanged(this);
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ObservableCollection<BlueprintPortViewModel> InputPorts { get; } = [];

    public ObservableCollection<BlueprintPortViewModel> OutputPorts { get; } = [];

    public ObservableCollection<BlueprintVariableViewModel> Variables { get; } = [];

    public ICommand AddInputPortCommand { get; }

    public ICommand AddOutputPortCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand CaptureFunctionMembersCommand { get; }

    public ICommand AddVariableCommand { get; }

    public ICommand ExecuteCommand { get; }

    internal void SetPosition(double x, double y)
    {
        if (Model.X.Equals(x) && Model.Y.Equals(y))
        {
            return;
        }

        Model.X = x;
        Model.Y = y;
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
    }

    internal void RebuildPorts()
    {
        InputPorts.Clear();
        OutputPorts.Clear();
        foreach (var port in Model.Ports.OrderBy(port => port.Order))
        {
            var viewModel = new BlueprintPortViewModel(_owner, this, port);
            if (port.Direction == BlueprintPortDirection.Input)
            {
                InputPorts.Add(viewModel);
            }
            else
            {
                OutputPorts.Add(viewModel);
            }
        }

        OnPropertyChanged(nameof(InputPorts));
        OnPropertyChanged(nameof(OutputPorts));
    }

    internal void NotifyFunctionMembersChanged() =>
        OnPropertyChanged(nameof(FunctionMembersText));

    internal void RaiseExecutionCommandState() => _executeCommand.RaiseCanExecuteChanged();

    private void RebuildVariables()
    {
        Variables.Clear();
        foreach (var variable in Model.Variables)
        {
            Variables.Add(new BlueprintVariableViewModel(
                variable,
                requested => NormalizeVariableName(variable, requested),
                () => _owner.NodeChanged(this),
                RemoveVariable));
        }

        OnPropertyChanged(nameof(Variables));
    }

    private void AddVariable()
    {
        var index = 1;
        string name;
        do
        {
            name = $"variable{index++}";
        }
        while (Model.Variables.Any(variable => string.Equals(variable.Name, name, StringComparison.OrdinalIgnoreCase)));

        var model = new BlueprintVariable
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Value = string.Empty
        };
        Model.Variables.Add(model);
        Variables.Add(new BlueprintVariableViewModel(
            model,
            requested => NormalizeVariableName(model, requested),
            () => _owner.NodeChanged(this),
            RemoveVariable));
        _owner.NodeChanged(this);
    }

    private void RemoveVariable(BlueprintVariableViewModel variable)
    {
        Model.Variables.Remove(variable.Model);
        Variables.Remove(variable);
        _owner.NodeChanged(this);
    }

    private string NormalizeVariableName(BlueprintVariable current, string requested)
    {
        var baseName = string.IsNullOrWhiteSpace(requested) ? "variable" : requested.Trim();
        baseName = baseName[..Math.Min(baseName.Length, 120)];
        var occupied = Model.Variables
            .Where(variable => !ReferenceEquals(variable, current))
            .Select(variable => variable.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!occupied.Contains(baseName))
        {
            return baseName;
        }

        for (var index = 2; ; index++)
        {
            var suffix = $"_{index}";
            var prefixLength = Math.Min(baseName.Length, 120 - suffix.Length);
            var candidate = string.Concat(baseName.AsSpan(0, prefixLength), suffix);
            if (!occupied.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}

public sealed class BlueprintPortViewModel : ObservableObject
{
    private readonly BlueprintViewModel _owner;
    private bool _isPendingConnection;

    internal BlueprintPortViewModel(
        BlueprintViewModel owner,
        BlueprintNodeViewModel node,
        BlueprintPort model)
    {
        _owner = owner;
        Node = node;
        Model = model;
        ClickCommand = new RelayCommand(() => _owner.HandlePortClicked(this));
        RemoveCommand = new RelayCommand(() => _owner.RemovePort(this), () => CanRemove);
    }

    internal BlueprintPort Model { get; }

    public BlueprintNodeViewModel Node { get; }

    public string Id => Model.Id;

    public string Name => Model.Name;

    public BlueprintPortDirection Direction => Model.Direction;

    public BlueprintPortKind Kind => Model.Kind;

    public int Order => Model.Order;

    public string ToolTipText => $"{Name} · {Kind} · {Model.ValueKind}";

    public bool CanRemove
    {
        get
        {
            if (Node.IsStart)
            {
                if (Direction != BlueprintPortDirection.Output)
                {
                    return false;
                }

                return Kind != BlueprintPortKind.Flow ||
                       Node.OutputPorts.Count(port => port.Kind == BlueprintPortKind.Flow) > 1;
            }

            return !(Node.IsLogic &&
                     Direction == BlueprintPortDirection.Output &&
                     Kind == BlueprintPortKind.Flow &&
                     (string.Equals(Name, "True", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(Name, "False", StringComparison.OrdinalIgnoreCase)));
        }
    }

    public bool IsPendingConnection
    {
        get => _isPendingConnection;
        set => SetProperty(ref _isPendingConnection, value);
    }

    public ICommand ClickCommand { get; }

    public ICommand RemoveCommand { get; }
}

public sealed class BlueprintVariableViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<BlueprintVariableViewModel> _remove;
    private readonly Func<string, string> _normalizeName;

    internal BlueprintVariableViewModel(
        BlueprintVariable model,
        Func<string, string> normalizeName,
        Action changed,
        Action<BlueprintVariableViewModel> remove)
    {
        Model = model;
        _normalizeName = normalizeName;
        _changed = changed;
        _remove = remove;
        RemoveCommand = new RelayCommand(() => _remove(this));
    }

    internal BlueprintVariable Model { get; }

    public string Name
    {
        get => Model.Name;
        set
        {
            var next = _normalizeName(value);
            if (string.Equals(Model.Name, next, StringComparison.Ordinal))
            {
                return;
            }

            Model.Name = next;
            OnPropertyChanged();
            _changed();
        }
    }

    public string Value
    {
        get => Model.Value;
        set
        {
            if (string.Equals(Model.Value, value, StringComparison.Ordinal))
            {
                return;
            }

            var next = value ?? string.Empty;
            Model.Value = next[..Math.Min(next.Length, 16_000)];
            OnPropertyChanged();
            _changed();
        }
    }

    public ICommand RemoveCommand { get; }
}
