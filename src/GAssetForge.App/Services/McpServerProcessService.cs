using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GAssetForge.Core;

namespace GAssetForge.App.Services;

public enum McpServerProcessState
{
    Stopped,
    Starting,
    WaitingForAgent,
    Connected,
    Stopping,
    Faulted
}

public sealed class McpServerStateChangedEventArgs : EventArgs
{
    public McpServerStateChangedEventArgs(
        McpServerProcessState state,
        bool isProcessRunning,
        DateTimeOffset? lastHandshakeUtc,
        string? lastClientName,
        string? lastClientVersion,
        string? detail)
    {
        State = state;
        IsProcessRunning = isProcessRunning;
        LastHandshakeUtc = lastHandshakeUtc;
        LastClientName = lastClientName;
        LastClientVersion = lastClientVersion;
        Detail = detail;
    }

    public McpServerProcessState State { get; }

    public bool IsProcessRunning { get; }

    public DateTimeOffset? LastHandshakeUtc { get; }

    public string? LastClientName { get; }

    public string? LastClientVersion { get; }

    public string? Detail { get; }
}

public sealed class McpServerLogEventArgs : EventArgs
{
    public McpServerLogEventArgs(DateTimeOffset timestamp, string message, bool isError)
    {
        Timestamp = timestamp;
        Message = message;
        IsError = isError;
    }

    public DateTimeOffset Timestamp { get; }

    public string Message { get; }

    public bool IsError { get; }
}

public sealed class McpServerProcessService : IDisposable
{
    private const string ServerExecutableName = "GAssetForge.McpServer.exe";
    private const string ServerAssemblyName = "GAssetForge.McpServer.dll";
    public const string DefaultBearerTokenEnvironmentVariableName = ProductIdentity.BearerTokenEnvironmentVariableName;
    private const int BearerTokenBytes = 32;
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly WorkspacePaths _paths;
    private readonly HttpClient _httpClient;
    private readonly string _bearerToken;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateLock = new();
    private Process? _process;
    private CancellationTokenSource? _runCancellation;
    private McpServerProcessState _state = McpServerProcessState.Stopped;
    private DateTimeOffset? _lastHandshakeUtc;
    private string? _lastClientName;
    private string? _lastClientVersion;
    private string? _detail;
    private int _port;
    private bool _disposed;

    public McpServerProcessService(WorkspacePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _bearerToken = CreateBearerToken();
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        })
        {
            Timeout = TimeSpan.FromMilliseconds(850)
        };
    }

    public event EventHandler<McpServerStateChangedEventArgs>? StateChanged;

    public event EventHandler<McpServerLogEventArgs>? LogReceived;

    public McpServerProcessState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public bool IsProcessRunning
    {
        get
        {
            lock (_stateLock)
            {
                return IsProcessAlive(_process);
            }
        }
    }

    public Uri? Endpoint => _port is >= 1024 and <= 65535
        ? new Uri($"http://127.0.0.1:{_port}/mcp", UriKind.Absolute)
        : null;

    public string TokenEnvironmentVariableName => DefaultBearerTokenEnvironmentVariableName;

    public string TokenEnvironmentVariableValue => _bearerToken;

    public string BearerToken => _bearerToken;

    public async Task StartAsync(int port, string expectedAgent, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateStartArguments(port, expectedAgent);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsProcessRunning)
            {
                throw new InvalidOperationException("MCP Server 已经在运行。请先停止当前服务再重新启动。");
            }

            EnsurePortAvailable(port);
            var launch = ResolveServerLaunch();
            var startInfo = CreateStartInfo(launch, port, expectedAgent);
            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            var runCancellation = new CancellationTokenSource();

            lock (_stateLock)
            {
                _port = port;
                _lastHandshakeUtc = null;
                _lastClientName = null;
                _lastClientVersion = null;
                _detail = $"正在启动 {expectedAgent} 连接";
                _state = McpServerProcessState.Starting;
            }

            RaiseStateChanged();
            WriteLog($"正在启动 MCP Server：{launch.DisplayPath}", false);
            WriteLog($"监听地址：http://127.0.0.1:{port}/mcp；预期 Agent：{expectedAgent}", false);

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("操作系统未能启动 MCP Server 进程。");
                }
            }
            catch (Exception exception)
            {
                process.Dispose();
                runCancellation.Dispose();
                SetState(McpServerProcessState.Faulted, false, $"启动失败：{exception.Message}");
                WriteLog($"启动失败：{exception.Message}", true);
                throw;
            }

            lock (_stateLock)
            {
                _process = process;
                _runCancellation = runCancellation;
            }

            RaiseStateChanged();
            _ = ReadOutputAsync(process.StandardError, "server", true, runCancellation.Token);
            _ = ReadOutputAsync(process.StandardOutput, "stdout", false, runCancellation.Token);
            _ = MonitorProcessExitAsync(process);
            _ = MonitorHealthAsync(process, port, runCancellation.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Process? process;
            CancellationTokenSource? runCancellation;
            var wasAlreadyStopped = false;
            lock (_stateLock)
            {
                process = _process;
                runCancellation = _runCancellation;
                if (!IsProcessAlive(process))
                {
                    _process = null;
                    _runCancellation = null;
                    _state = McpServerProcessState.Stopped;
                    _detail = "服务器未启动";
                    wasAlreadyStopped = true;
                }
                else
                {
                    _state = McpServerProcessState.Stopping;
                    _detail = "正在停止服务器";
                }
            }

            if (wasAlreadyStopped)
            {
                process?.Dispose();
                runCancellation?.Dispose();
                RaiseStateChanged();
                return;
            }

            RaiseStateChanged();
            WriteLog("正在停止 MCP Server…", false);
            runCancellation?.Cancel();

            try
            {
                process!.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(StopTimeout);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the liveness check and Kill.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                WriteLog("等待 MCP Server 退出超时，已释放本地进程句柄。", true);
            }
            finally
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _process = null;
                        _runCancellation = null;
                        _state = McpServerProcessState.Stopped;
                        _detail = "服务器已停止";
                    }
                }

                process?.Dispose();
                runCancellation?.Dispose();
            }

            WriteLog("MCP Server 已停止。", false);
            RaiseStateChanged();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            WriteLog($"关闭 MCP Server 时发生错误：{exception.Message}", true);
        }

        _httpClient.Dispose();
        _lifecycleGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private ProcessStartInfo CreateStartInfo(ServerLaunch launch, int port, string expectedAgent)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.FileName,
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        if (launch.ManagedAssemblyPath is not null)
        {
            startInfo.ArgumentList.Add(launch.ManagedAssemblyPath);
        }

        startInfo.ArgumentList.Add("--transport");
        startInfo.ArgumentList.Add("http");
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--expected-agent");
        startInfo.ArgumentList.Add(expectedAgent);
        startInfo.ArgumentList.Add("--token-env");
        startInfo.ArgumentList.Add(TokenEnvironmentVariableName);
        startInfo.ArgumentList.Add("--data-root");
        startInfo.ArgumentList.Add(_paths.RootDirectory);
        startInfo.Environment[TokenEnvironmentVariableName] = TokenEnvironmentVariableValue;
        return startInfo;
    }

    private static string CreateBearerToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(BearerTokenBytes);
        try
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private ServerLaunch ResolveServerLaunch()
    {
        var executablePath = Path.Combine(AppContext.BaseDirectory, ServerExecutableName);
        if (IsUsableServerExecutable(executablePath))
        {
            return new ServerLaunch(
                executablePath,
                Path.GetDirectoryName(executablePath)!,
                null,
                executablePath);
        }

        var checkedPaths = new List<string> { executablePath };
        foreach (var repositoryRoot in FindRepositoryRoots())
        {
            var configurationOrder = AppContext.BaseDirectory.Contains(
                $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
                ? new[] { "Debug", "Release" }
                : new[] { "Release", "Debug" };

            foreach (var configuration in configurationOrder)
            {
                var assemblyPath = Path.Combine(
                    repositoryRoot,
                    "src",
                    "GAssetForge.McpServer",
                    "bin",
                    configuration,
                    "net8.0",
                    ServerAssemblyName);
                checkedPaths.Add(assemblyPath);
                if (File.Exists(assemblyPath))
                {
                    return new ServerLaunch(
                        "dotnet",
                        Path.GetDirectoryName(assemblyPath)!,
                        assemblyPath,
                        $"dotnet {assemblyPath}");
                }
            }
        }

        throw new FileNotFoundException(
            "找不到 GAssetForge MCP Server。请重新执行正式发布，或先构建 " +
            "src/GAssetForge.McpServer。已检查：" + Environment.NewLine +
            string.Join(Environment.NewLine, checkedPaths));
    }

    private static bool IsUsableServerExecutable(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            return false;
        }

        var managedAssemblyPath = Path.ChangeExtension(executablePath, ".dll");
        var runtimeConfigPath = Path.ChangeExtension(executablePath, ".runtimeconfig.json");

        // A framework-dependent apphost needs its companion DLL. Published single-file
        // executables do not emit an external runtimeconfig and remain valid here.
        return !File.Exists(runtimeConfigPath) || File.Exists(managedAssemblyPath);
    }

    private static IEnumerable<string> FindRepositoryRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var startPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(startPath));
            while (current is not null)
            {
                var solutionPath = Path.Combine(current.FullName, "GAssetForge.sln");
                var serverProjectPath = Path.Combine(
                    current.FullName,
                    "src",
                    "GAssetForge.McpServer",
                    "GAssetForge.McpServer.csproj");
                if ((File.Exists(solutionPath) || File.Exists(serverProjectPath)) && roots.Add(current.FullName))
                {
                    yield return current.FullName;
                    break;
                }

                current = current.Parent;
            }
        }
    }

    private async Task MonitorProcessExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            var exitCode = process.ExitCode;
            CancellationTokenSource? cancellation = null;
            var shouldReport = false;

            lock (_stateLock)
            {
                if (ReferenceEquals(_process, process))
                {
                    shouldReport = _state != McpServerProcessState.Stopping && !_disposed;
                    _process = null;
                    cancellation = _runCancellation;
                    _runCancellation = null;
                    _state = shouldReport
                        ? McpServerProcessState.Faulted
                        : McpServerProcessState.Stopped;
                    _detail = shouldReport
                        ? $"MCP Server 异常退出（代码 {exitCode}）"
                        : "服务器已停止";
                }
            }

            cancellation?.Cancel();
            cancellation?.Dispose();
            if (shouldReport)
            {
                WriteLog($"MCP Server 异常退出，退出代码：{exitCode}。", true);
            }

            RaiseStateChanged();
        }
        catch (ObjectDisposedException)
        {
            // StopAsync owns cleanup when it has already disposed the process.
        }
    }

    private async Task MonitorHealthAsync(Process process, int port, CancellationToken cancellationToken)
    {
        var healthUri = new Uri($"http://127.0.0.1:{port}/health", UriKind.Absolute);
        var previouslyHealthy = false;
        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested && IsCurrentProcess(process))
        {
            try
            {
                using var response = await _httpClient.GetAsync(healthUri, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var health = ParseHealth(json);
                consecutiveFailures = 0;

                if (!previouslyHealthy)
                {
                    WriteLog("MCP Server 健康检查通过，正在等待 Agent 握手。", false);
                    previouslyHealthy = true;
                }

                lock (_stateLock)
                {
                    if (!ReferenceEquals(_process, process))
                    {
                        return;
                    }

                    _lastHandshakeUtc = health.LastHandshakeUtc;
                    _lastClientName = health.LastClientName;
                    _lastClientVersion = health.LastClientVersion;
                    _state = health.IsConnected
                        ? McpServerProcessState.Connected
                        : McpServerProcessState.WaitingForAgent;
                    _detail = health.IsConnected
                        ? "Agent 已完成 MCP 握手"
                        : "服务器运行中，等待 Agent 握手";
                }

                RaiseStateChanged();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                consecutiveFailures++;
                if (previouslyHealthy && consecutiveFailures == 3)
                {
                    SetState(
                        McpServerProcessState.Faulted,
                        true,
                        $"健康检查连续失败：{exception.Message}");
                    WriteLog($"MCP Server 健康检查连续失败：{exception.Message}", true);
                }
            }

            try
            {
                await Task.Delay(HealthInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ReadOutputAsync(
        StreamReader reader,
        string source,
        bool isError,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    var message = source == "server" ? line : $"[{source}] {line}";
                    WriteLog(message, isError && IsErrorLogLine(line));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Normal shutdown.
        }
    }

    private static McpHealthSnapshot ParseHealth(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var status = ReadString(root, "status");
        var lastHandshakeUtc = ReadDateTimeOffset(root, "lastHandshakeUtc");
        return new McpHealthSnapshot(
            string.Equals(status, "connected", StringComparison.OrdinalIgnoreCase),
            lastHandshakeUtc,
            ReadString(root, "lastClientName"),
            ReadString(root, "lastClientVersion"));
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var timestamp)
            ? timestamp
            : null;
    }

    private static bool IsErrorLogLine(string line) =>
        line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("失败", StringComparison.Ordinal) ||
        line.Contains("异常", StringComparison.Ordinal);

    private static void ValidateStartArguments(int port, string expectedAgent)
    {
        if (port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "端口必须位于 1024 到 65535 之间。");
        }

        if (!string.Equals(expectedAgent, "codex", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(expectedAgent, "opencode", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("当前只支持 Codex 与 OpenCode Agent。", nameof(expectedAgent));
        }
    }

    private static void EnsurePortAvailable(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException(
                $"端口 {port} 已被其他程序占用，请更换端口后重试。",
                exception);
        }
        finally
        {
            listener?.Stop();
        }
    }

    private bool IsCurrentProcess(Process process)
    {
        lock (_stateLock)
        {
            return ReferenceEquals(_process, process) && IsProcessAlive(process);
        }
    }

    private static bool IsProcessAlive(Process? process)
    {
        if (process is null)
        {
            return false;
        }

        try
        {
            return !process.HasExited;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void SetState(McpServerProcessState state, bool requireRunningProcess, string detail)
    {
        lock (_stateLock)
        {
            if (requireRunningProcess && !IsProcessAlive(_process))
            {
                return;
            }

            _state = state;
            _detail = detail;
        }

        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        McpServerStateChangedEventArgs args;
        lock (_stateLock)
        {
            args = CreateStateEventArgs();
        }

        StateChanged?.Invoke(this, args);
    }

    private McpServerStateChangedEventArgs CreateStateEventArgs() => new(
        _state,
        IsProcessAlive(_process),
        _lastHandshakeUtc,
        _lastClientName,
        _lastClientVersion,
        _detail);

    private void WriteLog(string message, bool isError) =>
        LogReceived?.Invoke(this, new McpServerLogEventArgs(DateTimeOffset.Now, message, isError));

    private sealed record ServerLaunch(
        string FileName,
        string WorkingDirectory,
        string? ManagedAssemblyPath,
        string DisplayPath);

    private sealed record McpHealthSnapshot(
        bool IsConnected,
        DateTimeOffset? LastHandshakeUtc,
        string? LastClientName,
        string? LastClientVersion);
}
