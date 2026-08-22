using GAssetForge.Core.Generation;
using GAssetForge.McpServer;

try
{
    if (DirectToolInvocationExecutor.IsRequested(args))
    {
        var invocationResult = await DirectToolInvocationExecutor.ExecuteAsync(args)
            .ConfigureAwait(false);
        Console.Out.WriteLine(invocationResult.Output);
        return invocationResult.ExitCode;
    }

    var options = ServerOptions.Parse(args);
    var contextResolver = new WorkspaceContextResolver(options);
    var taskStore = new JsonGenerationTaskStore(options.GenerationStorageRoot);
    var toolProvider = new GenerationToolProvider(
        taskStore,
        contextResolver,
        options,
        taskStore.StoreFilePath);
    var dispatcher = new McpRequestDispatcher(toolProvider.CreateCatalog());

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    if (options.UsesHttp)
    {
        var server = new HttpMcpServer(options, dispatcher);
        await server.RunAsync(shutdown.Token).ConfigureAwait(false);
    }
    else
    {
        var server = new StdioMcpServer(dispatcher);
        await server.RunAsync(shutdown.Token).ConfigureAwait(false);
    }

    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"[GAssetForge MCP] 启动失败：{exception}");
    return 1;
}
