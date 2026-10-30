using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Mcp;

public static class McpBridgeServer
{
    private const string LOG_IDENT = "McpBridgeServer";

    private static readonly CancellationTokenSource ShutdownCts = new();

    private static volatile NamedPipeServerStream? _currentServer;

    private static int _started;

    public static string PipeName
    {
        get
        {
            if (!Voidstrap.Utility.Platform.IsWindows)
                return PortableSocketPath();
            using Process current = Process.GetCurrentProcess();
            return "Voidstrap.Mcp." + current.SessionId;
        }
    }

    private static string PortableSocketPath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string folder = Voidstrap.Utility.Platform.IsMacOS ? Path.Combine(home, "Library", "Caches") : Path.Combine(home, ".cache");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "voidstrap-mcp.sock");
    }

    public static void Stop()
    {
        try
        {
            if (!ShutdownCts.IsCancellationRequested)
                ShutdownCts.Cancel();
        }
        catch { }
        try
        {
            _currentServer?.Dispose();
            _currentServer = null;
        }
        catch { }
    }

    public static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        _ = Task.Run(McpConnectInfo.RefreshConnectedClients);

        CancellationToken token = ShutdownCts.Token;
        string pipeName = PipeName;
        var thread = new Thread(() => AcceptLoopAsync(pipeName, token).GetAwaiter().GetResult())
        {
            IsBackground = true,
            Name = "Voidstrap MCP bridge",
        };
        thread.Start();
        App.Logger?.WriteLine(LOG_IDENT, "Listening for MCP tools on pipe " + pipeName);
    }

    private static async Task AcceptLoopAsync(string pipeName, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;

            try
            {
                server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
                );

                _currentServer = server;
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                NamedPipeServerStream connected = server;
                server = null;
                _currentServer = null;
                _ = Task.Run(() => HandleClientAsync(connected, token), token);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                _currentServer = null;
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                _currentServer = null;

                if (token.IsCancellationRequested)
                    break;

                App.Logger?.WriteLine(LOG_IDENT, "Pipe accept failed: " + ex.Message.Replace('-', ' '));

                try
                {
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken token)
    {
        try
        {
            using (stream)
            using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true))
            {
                while (stream.IsConnected && !token.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);

                    if (line == null)
                        break;

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    McpToolResult result;
                    try
                    {
                        result = await RunAsync(line, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        result = new McpToolResult("Voidstrap could not run the tool: " + ex.Message, true);
                    }
                    var response = new JsonObject { ["text"] = result.Text, ["isError"] = result.IsError };

                    if (!stream.IsConnected)
                        break;
                    byte[] data = Encoding.UTF8.GetBytes(response.ToJsonString() + "\n");
                    await stream.WriteAsync(data, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Client handling failed: " + ex.Message.Replace('-', ' '));
        }
    }

    private const int MaxReplyChars = 2 * 1024 * 1024;

    private static async Task<McpToolResult> RunAsync(string line, CancellationToken token)
    {
        string name;
        string? client;
        JsonObject args;

        try
        {
            if (line.Length > 32 * 1024 * 1024)
                throw new FormatException("Request is too large");
            JsonObject request = JsonNode.Parse(line) as JsonObject ?? throw new FormatException("Request is not an object");
            name = McpArgs.String(request, "name")?.Trim() ?? throw new FormatException("Request has no tool name");
            client = McpArgs.String(request, "client");
            args = request["arguments"] is JsonObject given ? (JsonObject)given.DeepClone() : new JsonObject();
        }
        catch (Exception ex)
        {
            return new McpToolResult("Bad request: " + ex.Message, true);
        }

        if (!McpToolCatalog.Exists(name))
            return new McpToolResult($"Unknown tool '{name}'. Available tools: {string.Join(", ", McpToolCatalog.Names)}.", true);

        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return new McpToolResult("Voidstrap is shutting down.", true);

        McpActivity.Report(client, name, args);

        Stopwatch timer = Stopwatch.StartNew();
        McpToolResult result;
        try
        {
            result = McpToolCatalog.RunsOffUiThread(name)
                ? await RunOffUiThreadAsync(name, args, dispatcher, token).ConfigureAwait(false)
                : await RunOnUiThreadAsync(name, args, dispatcher, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new McpToolResult("Voidstrap is shutting down.", true);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteException(LOG_IDENT + "::" + name, ex);
            result = new McpToolResult($"The tool {name} failed: {ex.Message}", true);
        }

        App.Logger?.WriteLine(
            LOG_IDENT,
            $"Tool {name} {(result.IsError ? "returned an error" : "finished")} in {timer.ElapsedMilliseconds} ms"
        );

        if (result.Text.Length > MaxReplyChars)
            result = result with
            {
                Text = result.Text[..MaxReplyChars] + "\n... (reply cut at 2 MB, narrow the request with a filter or line range)",
            };

        return result;
    }

    private static async Task<McpToolResult> RunOnUiThreadAsync(
        string name,
        JsonObject args,
        System.Windows.Threading.Dispatcher dispatcher,
        CancellationToken token
    )
    {
        var operation = dispatcher.InvokeAsync(() => McpTools.Execute(name, args));
        Task<McpToolResult> task = operation.Task;
        Task finished = await Task.WhenAny(task, Task.Delay(McpToolCatalog.Timeout(name), token)).ConfigureAwait(false);

        if (finished != task)
        {
            operation.Abort();
            _ = task.ContinueWith(ObserveFault, TaskContinuationOptions.OnlyOnFaulted);
            return new McpToolResult("Voidstrap is busy, a dialog may be waiting for the user. Try again in a moment.", true);
        }

        return await task.ConfigureAwait(false);
    }

    private static async Task<McpToolResult> RunOffUiThreadAsync(
        string name,
        JsonObject args,
        System.Windows.Threading.Dispatcher dispatcher,
        CancellationToken token
    )
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(McpToolCatalog.Timeout(name));
        Task<McpToolResult> task = name switch
        {
            "search_fastflag_tracker" => McpTools.SearchFastFlagTrackerAsync(args, dispatcher, limit.Token),
            "manage_mod_generator" => McpTools.ManageModGeneratorAsync(args, dispatcher, limit.Token),
            "list_accounts" => McpTools.ListAccountsAsync(limit.Token),
            "manage_voidstrap_mods" => Task.Run(() => McpTools.ManageVoidstrapMods(args), limit.Token),
            "read_voidstrap_file" => Task.Run(() => McpTools.ReadVoidstrapFileSafe(args), limit.Token),
            "get_docs" => Task.Run(() => McpTools.GetDocs(args), limit.Token),
            "manage_app_control" => Task.Run(() => McpTools.ManageAppControl(args), limit.Token),
            "manage_app_code" => Task.Run(() => McpTools.ManageAppCode(args), limit.Token),
            _ => Task.FromResult(new McpToolResult($"The tool {name} has no background handler.", true)),
        };

        Task finished = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, limit.Token)).ConfigureAwait(false);
        if (finished != task)
        {
            _ = task.ContinueWith(ObserveFault, TaskContinuationOptions.OnlyOnFaulted);
            if (token.IsCancellationRequested)
                throw new OperationCanceledException(token);
            return new McpToolResult(
                $"The tool {name} took longer than {McpToolCatalog.Timeout(name).TotalSeconds:0} seconds and was stopped.",
                true
            );
        }

        return await task.ConfigureAwait(false);
    }

    private static void ObserveFault(Task task)
    {
        App.Logger?.WriteLine(LOG_IDENT, "A timed out tool failed later: " + task.Exception?.GetBaseException().Message);
    }
}
