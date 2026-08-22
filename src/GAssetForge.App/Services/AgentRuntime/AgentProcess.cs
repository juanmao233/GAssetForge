using System.Diagnostics;
using System.IO;

namespace GAssetForge.App.Services.AgentRuntime;

internal interface IAgentChildProcess : IAsyncDisposable
{
    int Id { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    TextWriter StandardInput { get; }

    TextReader StandardOutput { get; }

    TextReader StandardError { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void Kill(bool entireProcessTree);
}

internal interface IAgentProcessFactory
{
    IAgentChildProcess Start(ProcessStartInfo startInfo);
}

internal sealed class SystemAgentProcessFactory : IAgentProcessFactory
{
    public static SystemAgentProcessFactory Instance { get; } = new();

    public IAgentChildProcess Start(ProcessStartInfo startInfo)
    {
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("操作系统未能启动 Agent CLI 进程。");
            }

            return new SystemAgentChildProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

internal sealed class SystemAgentChildProcess(Process process) : IAgentChildProcess
{
    private readonly Process _process = process;

    public int Id => _process.Id;

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public TextWriter StandardInput => _process.StandardInput;

    public TextReader StandardOutput => _process.StandardOutput;

    public TextReader StandardError => _process.StandardError;

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Kill(bool entireProcessTree)
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree);
        }
    }

    public ValueTask DisposeAsync()
    {
        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
