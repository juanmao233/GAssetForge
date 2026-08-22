using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace GAssetForge.App.ViewModels;

public sealed record CommandConsoleInvocation(
    string Name,
    string RawText,
    IReadOnlyList<string> Arguments,
    IWorkbenchLogSink Log)
{
    public string ArgumentText => string.Join(' ', Arguments);
}

public sealed record CommandConsoleResult(
    bool IsSuccess,
    string Message,
    WorkbenchLogLevel Level)
{
    public static CommandConsoleResult Success(string message) =>
        new(true, message ?? string.Empty, WorkbenchLogLevel.Success);

    public static CommandConsoleResult Info(string message) =>
        new(true, message ?? string.Empty, WorkbenchLogLevel.Information);

    public static CommandConsoleResult Failure(string message) =>
        new(false, message ?? string.Empty, WorkbenchLogLevel.Error);
}

public delegate Task<CommandConsoleResult?> CommandConsoleHandler(
    CommandConsoleInvocation invocation,
    CancellationToken cancellationToken);

public sealed class CommandConsoleViewModel : ObservableObject, IDisposable
{
    private const int DefaultEntryCapacity = 2_000;
    private const int HistoryCapacity = 200;
    private const string ConsoleSource = "Console";

    private readonly WorkbenchConsoleService _service;
    private readonly Dispatcher _dispatcher;
    private readonly int _entryCapacity;
    private readonly object _commandGate = new();
    private readonly Dictionary<string, RegisteredCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RegisteredCommand> _commandLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _history = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly AsyncRelayCommand _executeCommandCommand;
    private readonly RelayCommand _previousHistoryCommand;
    private readonly RelayCommand _nextHistoryCommand;

    private string _inputText = string.Empty;
    private string _searchText = string.Empty;
    private string _historyDraft = string.Empty;
    private int _historyIndex;
    private bool _showInformation = true;
    private bool _showWarnings = true;
    private bool _showErrors = true;
    private bool _showCommands = true;
    private bool _autoScroll = true;
    private bool _isExecuting;
    private bool _disposed;
    private int _informationCount;
    private int _warningCount;
    private int _errorCount;
    private int _commandCount;
    private long _clearEpoch;
    private long _clearSequenceBarrier;
    private WorkbenchLogEntry? _selectedEntry;

    public CommandConsoleViewModel(
        WorkbenchConsoleService service,
        int entryCapacity = DefaultEntryCapacity)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        if (entryCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(entryCapacity), "控制台显示容量必须大于零。");
        }

        _entryCapacity = entryCapacity;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Entries = [];

        FilteredEntries = CollectionViewSource.GetDefaultView(Entries);
        FilteredEntries.Filter = MatchesFilter;

        _service.EntryPublished += OnEntryPublished;
        _service.BufferCleared += OnBufferCleared;

        foreach (var entry in service.GetSnapshot().TakeLast(_entryCapacity))
        {
            Entries.Add(entry);
        }

        _executeCommandCommand = new AsyncRelayCommand(
            ExecuteCurrentCommandAsync,
            () => !IsExecuting && !string.IsNullOrWhiteSpace(InputText));
        ClearDisplayCommand = new RelayCommand(ClearDisplay);
        OpenLogDirectoryCommand = new RelayCommand(OpenLogDirectory);
        _previousHistoryCommand = new RelayCommand(
            () => NavigateHistory(-1),
            () => _history.Count > 0);
        _nextHistoryCommand = new RelayCommand(
            () => NavigateHistory(1),
            () => _history.Count > 0 && _historyIndex < _history.Count);

        ExecuteCommandCommand = _executeCommandCommand;
        PreviousHistoryCommand = _previousHistoryCommand;
        NextHistoryCommand = _nextHistoryCommand;

        RegisterBuiltInCommands();
        RecalculateStatistics();

    }

    public event EventHandler? ScrollToEndRequested;

    public ObservableCollection<WorkbenchLogEntry> Entries { get; }

    public ICollectionView FilteredEntries { get; }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value ?? string.Empty))
            {
                _executeCommandCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                RefreshFilter();
            }
        }
    }

    public bool ShowInformation
    {
        get => _showInformation;
        set
        {
            if (SetProperty(ref _showInformation, value))
            {
                RefreshFilter();
            }
        }
    }

    public bool ShowWarnings
    {
        get => _showWarnings;
        set
        {
            if (SetProperty(ref _showWarnings, value))
            {
                RefreshFilter();
            }
        }
    }

    public bool ShowErrors
    {
        get => _showErrors;
        set
        {
            if (SetProperty(ref _showErrors, value))
            {
                RefreshFilter();
            }
        }
    }

    public bool ShowCommands
    {
        get => _showCommands;
        set
        {
            if (SetProperty(ref _showCommands, value))
            {
                RefreshFilter();
            }
        }
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (SetProperty(ref _isExecuting, value))
            {
                _executeCommandCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public WorkbenchLogEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value))
            {
                OnPropertyChanged(nameof(SelectedEntryText));
            }
        }
    }

    public string SelectedEntryText => SelectedEntry is null
        ? "选择一条日志可查看完整信息"
        : string.Join(
            Environment.NewLine,
            new[]
            {
                SelectedEntry.Message,
                SelectedEntry.Context?.Summary,
                string.IsNullOrWhiteSpace(SelectedEntry.Details) ? null : SelectedEntry.Details
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public int InformationCount => _informationCount;

    public int WarningCount => _warningCount;

    public int ErrorCount => _errorCount;

    public int CommandCount => _commandCount;

    public int VisibleCount => FilteredEntries.Cast<object>().Count();

    public string InformationFilterLabel => $"信息 {_informationCount}";

    public string WarningFilterLabel => $"警告 {_warningCount}";

    public string ErrorFilterLabel => $"错误 {_errorCount}";

    public string CommandFilterLabel => $"命令 {_commandCount}";

    public string StatusText => IsExecuting
        ? $"正在执行命令 · 显示 {VisibleCount}/{Entries.Count}"
        : $"显示 {VisibleCount}/{Entries.Count} · 上限 {_entryCapacity}";

    public string LogDirectory => _service.LogDirectory;

    public string SessionLogPath => _service.SessionLogPath;

    public string PersistenceStatusText => _service.IsFileLoggingAvailable
        ? $"会话日志：{Path.GetFileName(SessionLogPath)}"
        : "会话文件不可用，日志仍保留在当前工作台内存中";

    public ICommand ExecuteCommandCommand { get; }

    public ICommand ClearDisplayCommand { get; }

    public ICommand OpenLogDirectoryCommand { get; }

    public ICommand PreviousHistoryCommand { get; }

    public ICommand NextHistoryCommand { get; }

    public void RegisterCommand(
        string name,
        string usage,
        string description,
        CommandConsoleHandler handler,
        params string[] aliases)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var canonicalName = NormalizeCommandName(name, nameof(name));
        usage = string.IsNullOrWhiteSpace(usage) ? canonicalName : usage.Trim();
        description = string.IsNullOrWhiteSpace(description) ? "未提供说明。" : description.Trim();
        aliases ??= [];

        var normalizedAliases = aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => NormalizeCommandName(alias, nameof(aliases)))
            .Where(alias => !string.Equals(alias, canonicalName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_commandGate)
        {
            var keys = normalizedAliases.Prepend(canonicalName).ToArray();
            var duplicate = keys.FirstOrDefault(key => _commandLookup.ContainsKey(key));
            if (duplicate is not null)
            {
                throw new InvalidOperationException($"控制台命令或别名“{duplicate}”已注册。");
            }

            var command = new RegisteredCommand(
                canonicalName,
                usage,
                description,
                normalizedAliases,
                handler);
            _commands.Add(canonicalName, command);
            foreach (var key in keys)
            {
                _commandLookup.Add(key, command);
            }
        }
    }

    public void RegisterCommand(
        string name,
        string usage,
        string description,
        Func<CommandConsoleInvocation, CancellationToken, Task> handler,
        params string[] aliases)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterCommand(
            name,
            usage,
            description,
            async (invocation, cancellationToken) =>
            {
                await handler(invocation, cancellationToken).ConfigureAwait(true);
                return null;
            },
            aliases);
    }

    public void WriteInfo(string source, string message, DateTimeOffset? timestamp = null) =>
        _service.WriteInfo(source, message, timestamp);

    public void WriteInfo(string message) =>
        _service.WriteInfo(ConsoleSource, message);

    public void WriteWarning(string source, string message, DateTimeOffset? timestamp = null) =>
        _service.WriteWarning(source, message, timestamp);

    public void WriteWarning(string message) =>
        _service.WriteWarning(ConsoleSource, message);

    public void WriteError(
        string source,
        string message,
        Exception? exception = null,
        DateTimeOffset? timestamp = null) =>
        _service.WriteError(source, message, exception, timestamp);

    public void WriteError(string message, Exception? exception = null) =>
        _service.WriteError(ConsoleSource, message, exception);

    public async Task ExecuteCurrentCommandAsync()
    {
        if (IsExecuting)
        {
            return;
        }

        var rawText = InputText.Trim();
        if (rawText.Length == 0)
        {
            return;
        }

        AddToHistory(rawText);
        InputText = string.Empty;
        _service.Write(WorkbenchLogLevel.Command, ConsoleSource, $"> {rawText}");

        if (!TryTokenize(rawText, out var tokens, out var parseError))
        {
            _service.WriteError(ConsoleSource, parseError);
            return;
        }

        RegisteredCommand? command;
        lock (_commandGate)
        {
            _commandLookup.TryGetValue(tokens[0], out command);
        }

        if (command is null)
        {
            _service.WriteError(
                ConsoleSource,
                $"未知命令“{tokens[0]}”。输入 help 查看允许执行的命令。");
            return;
        }

        IsExecuting = true;
        try
        {
            var invocation = new CommandConsoleInvocation(
                command.Name,
                rawText,
                tokens.Skip(1).ToArray(),
                _service);
            var result = await command.Handler(invocation, _lifetimeCancellation.Token).ConfigureAwait(true);
            if (result is { Message.Length: > 0 })
            {
                _service.Write(result.Level, command.Name, result.Message);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            _service.WriteWarning(command.Name, "命令因工作台关闭而取消。");
        }
        catch (Exception exception)
        {
            _service.WriteError(command.Name, $"命令执行失败：{exception.Message}", exception);
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public void NavigateHistory(int offset)
    {
        if (_history.Count == 0 || offset == 0)
        {
            return;
        }

        if (_historyIndex == _history.Count)
        {
            _historyDraft = InputText;
        }

        _historyIndex = Math.Clamp(_historyIndex + offset, 0, _history.Count);
        InputText = _historyIndex == _history.Count
            ? _historyDraft
            : _history[_historyIndex];

        _previousHistoryCommand.RaiseCanExecuteChanged();
        _nextHistoryCommand.RaiseCanExecuteChanged();
    }

    public void ClearDisplay() => _service.ClearInMemory();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.EntryPublished -= OnEntryPublished;
        _service.BufferCleared -= OnBufferCleared;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    private void RegisterBuiltInCommands()
    {
        RegisterCommand(
            "help",
            "help [命令]",
            "列出白名单命令，或查看指定命令的用法。",
            HandleHelpAsync,
            "?");
        RegisterCommand(
            "clear",
            "clear",
            "清空当前显示缓冲区；会话日志文件不会删除。",
            (_, _) =>
            {
                ClearDisplay();
                return Task.FromResult<CommandConsoleResult?>(null);
            },
            "cls");
        RegisterCommand(
            "logs.open",
            "logs.open",
            "在 Windows 文件资源管理器中打开会话日志目录。",
            (_, _) =>
            {
                OpenLogDirectory();
                return Task.FromResult<CommandConsoleResult?>(null);
            },
            "openlogs");
    }

    private Task<CommandConsoleResult?> HandleHelpAsync(
        CommandConsoleInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (invocation.Arguments.Count > 0)
        {
            RegisteredCommand? requested;
            lock (_commandGate)
            {
                _commandLookup.TryGetValue(invocation.Arguments[0], out requested);
            }

            if (requested is null)
            {
                return Task.FromResult<CommandConsoleResult?>(
                    CommandConsoleResult.Failure($"未注册命令“{invocation.Arguments[0]}”。"));
            }

            var aliases = requested.Aliases.Count == 0
                ? string.Empty
                : $"；别名：{string.Join(", ", requested.Aliases)}";
            _service.WriteInfo(ConsoleSource, $"用法：{requested.Usage}{aliases}");
            _service.WriteInfo(ConsoleSource, requested.Description);
            return Task.FromResult<CommandConsoleResult?>(null);
        }

        RegisteredCommand[] commands;
        lock (_commandGate)
        {
            commands = _commands.Values.OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        _service.WriteInfo(ConsoleSource, $"允许执行的命令（{commands.Length}）：");
        foreach (var command in commands)
        {
            _service.WriteInfo(ConsoleSource, $"  {command.Usage} — {command.Description}");
        }

        return Task.FromResult<CommandConsoleResult?>(null);
    }

    private void OpenLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true });
            _service.WriteInfo(ConsoleSource, $"已打开日志目录：{LogDirectory}");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            NotSupportedException or Win32Exception)
        {
            _service.WriteError(ConsoleSource, $"无法打开日志目录：{exception.Message}", exception);
        }
    }

    private void OnEntryPublished(object? sender, WorkbenchLogEntryEventArgs eventArgs) =>
        RunOnDispatcher(() => AddEntry(eventArgs.Entry));

    private void OnBufferCleared(
        object? sender,
        WorkbenchLogBufferClearedEventArgs eventArgs) =>
        RunOnDispatcher(() =>
        {
            _clearEpoch = Math.Max(_clearEpoch, eventArgs.Epoch);
            _clearSequenceBarrier = Math.Max(_clearSequenceBarrier, eventArgs.SequenceBarrier);
            for (var index = Entries.Count - 1; index >= 0; index--)
            {
                var entry = Entries[index];
                if (entry.Epoch < _clearEpoch || entry.Sequence <= _clearSequenceBarrier)
                {
                    Entries.RemoveAt(index);
                }
            }

            if (SelectedEntry is not null && !Entries.Contains(SelectedEntry))
            {
                SelectedEntry = null;
            }

            RecalculateStatistics();
            RefreshFilter();
        });

    private void AddEntry(WorkbenchLogEntry entry)
    {
        if (_disposed ||
            entry.Epoch < _clearEpoch ||
            entry.Sequence <= _clearSequenceBarrier ||
            Entries.Any(existing => existing.Sequence == entry.Sequence))
        {
            return;
        }

        var insertIndex = Entries.Count;
        while (insertIndex > 0 && Entries[insertIndex - 1].Sequence > entry.Sequence)
        {
            insertIndex--;
        }

        Entries.Insert(insertIndex, entry);
        while (Entries.Count > _entryCapacity)
        {
            Entries.RemoveAt(0);
        }

        RecalculateStatistics();
        RefreshFilter();
        if (AutoScroll && MatchesFilter(entry))
        {
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void AddToHistory(string command)
    {
        if (_history.Count == 0 || !string.Equals(_history[^1], command, StringComparison.Ordinal))
        {
            _history.Add(command);
            if (_history.Count > HistoryCapacity)
            {
                _history.RemoveAt(0);
            }
        }

        _historyIndex = _history.Count;
        _historyDraft = string.Empty;
        _previousHistoryCommand.RaiseCanExecuteChanged();
        _nextHistoryCommand.RaiseCanExecuteChanged();
    }

    private bool MatchesFilter(object item)
    {
        if (item is not WorkbenchLogEntry entry)
        {
            return false;
        }

        var levelVisible = entry.Level switch
        {
            WorkbenchLogLevel.Warning => ShowWarnings,
            WorkbenchLogLevel.Error => ShowErrors,
            WorkbenchLogLevel.Command => ShowCommands,
            _ => ShowInformation
        };
        if (!levelVisible)
        {
            return false;
        }

        var search = SearchText.Trim();
        return search.Length == 0 ||
               entry.Source.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.Details.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               (entry.Context?.Summary.Contains(search, StringComparison.OrdinalIgnoreCase) == true) ||
               entry.LevelText.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshFilter()
    {
        FilteredEntries.Refresh();
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(StatusText));
    }

    private void RecalculateStatistics()
    {
        _informationCount = Entries.Count(entry =>
            entry.Level is WorkbenchLogLevel.Trace or WorkbenchLogLevel.Information or WorkbenchLogLevel.Success);
        _warningCount = Entries.Count(entry => entry.Level == WorkbenchLogLevel.Warning);
        _errorCount = Entries.Count(entry => entry.Level == WorkbenchLogLevel.Error);
        _commandCount = Entries.Count(entry => entry.Level == WorkbenchLogLevel.Command);

        OnPropertyChanged(nameof(InformationCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(CommandCount));
        OnPropertyChanged(nameof(InformationFilterLabel));
        OnPropertyChanged(nameof(WarningFilterLabel));
        OnPropertyChanged(nameof(ErrorFilterLabel));
        OnPropertyChanged(nameof(CommandFilterLabel));
        OnPropertyChanged(nameof(StatusText));
    }

    private void RunOnDispatcher(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
        }
    }

    private static string NormalizeCommandName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new ArgumentException("命令名称不能包含空白或控制字符。", parameterName);
        }

        return normalized;
    }

    private static bool TryTokenize(string command, out string[] tokens, out string error)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        char? quote = null;

        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (quote is not null)
            {
                if (character == '\\' && index + 1 < command.Length && command[index + 1] == quote)
                {
                    current.Append(quote.Value);
                    index++;
                }
                else if (character == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (quote is not null)
        {
            tokens = [];
            error = "命令中的引号没有闭合。";
            return false;
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        if (result.Count == 0)
        {
            tokens = [];
            error = "命令不能为空。";
            return false;
        }

        tokens = result.ToArray();
        error = string.Empty;
        return true;
    }

    private sealed record RegisteredCommand(
        string Name,
        string Usage,
        string Description,
        IReadOnlyList<string> Aliases,
        CommandConsoleHandler Handler);
}
