using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using EdgeGoogleActions;

namespace EdgeGoogleActions.NativeHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--import-client", var path])
            {
                OAuth.ImportClient(path);
                Console.Error.WriteLine("Desktop OAuth client imported for the current Windows user. Connect Google in the extension.");
                return 0;
            }
            if (args is ["--invoke", var requestPath])
            {
                var request = JsonNode.Parse(await File.ReadAllTextAsync(requestPath)) as JsonObject
                    ?? throw new InvalidDataException("Expected a request object.");
                Wire.ValidateAction(request);
                var result = await BridgeClient.InvokeAsync(request["action"]!.GetValue<string>(),
                    request["args"] as JsonObject ?? new());
                Console.WriteLine(result.ToJsonString());
                return result["ok"]?.GetValue<bool>() == true ? 0 : 1;
            }
            if (args.Length == 0 || !Uri.TryCreate(args[0], UriKind.Absolute, out var origin) ||
                origin.Scheme != "chrome-extension" || origin.AbsolutePath != "/")
                throw new InvalidDataException("This host must be launched by Edge. Use --import-client or --invoke for setup and scripting.");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "host.json")));
            var origins = manifest?["allowed_origins"]?.AsArray();
            if (origins is null || !origins.Any(item => item?.GetValue<string>() == origin.AbsoluteUri))
                throw new UnauthorizedAccessException("Extension origin is not registered.");

            using var mutex = new Mutex(true, "Local\\" + Wire.PipeName, out var created);
            if (!created) throw new IOException("Another Edge profile already owns the bridge. Disable this extension in the other profile.");
            using var lifetime = new CancellationTokenSource();
            var host = new Host(Console.OpenStandardInput(), Console.OpenStandardOutput());
            await host.RunAsync(lifetime);
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or
            InvalidOperationException or ArgumentException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or System.Net.Http.HttpRequestException or
            OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}

internal sealed class Host(Stream input, Stream output)
{
    private readonly SemaphoreSlim writer = new(1);
    private readonly SemaphoreSlim authLock = new(1);
    private readonly SemaphoreSlim clientSlots = new(8);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> pending = new();
    private readonly ConcurrentDictionary<long, Task> jobs = new();
    private long nextJob;

    private async Task SendAsync(JsonObject message, CancellationToken cancellation)
    {
        await writer.WaitAsync(cancellation);
        try { await Wire.WriteAsync(output, message, cancellation); }
        finally { writer.Release(); }
    }

    private void Track(Task task)
    {
        var id = Interlocked.Increment(ref nextJob);
        jobs[id] = task;
        _ = task.ContinueWith(completed =>
        {
            jobs.TryRemove(id, out _);
            if (completed.Exception is { } error) Console.Error.WriteLine(error.GetBaseException().Message);
        }, TaskScheduler.Default);
    }

    public async Task RunAsync(CancellationTokenSource lifetime)
    {
        var cancellation = lifetime.Token;
        var server = ServeAsync(cancellation);
        try
        {
            while (true)
            {
                var read = Wire.ReadAsync(input, cancellation);
                if (await Task.WhenAny(read, server) == server)
                {
                    await server;
                    throw new IOException("Named-pipe server stopped unexpectedly.");
                }
                var message = await read;
                if (message is null) break;
                var id = message["id"]?.GetValue<string>() ?? throw new InvalidDataException("Missing message ID.");
                switch (message["type"]?.GetValue<string>())
                {
                    case "result":
                        if (pending.TryGetValue(id, out var result)) result.TrySetResult(message);
                        break;
                    case "nativeCall":
                        Track(HandleNativeAsync(message, cancellation));
                        break;
                    default: throw new InvalidDataException("Unknown native message.");
                }
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            foreach (var call in pending.Values) call.TrySetException(new IOException("Edge disconnected."));
            try { await server; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            await Task.WhenAll(jobs.Values.Select(async task =>
            {
                try { await task; }
                catch (Exception error) { Console.Error.WriteLine(error.Message); }
            }));
        }
    }

    private async Task ServeAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            await clientSlots.WaitAsync(cancellation);
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(Wire.PipeName, PipeDirection.InOut, 8,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation);
                Track(HandleClientSlotAsync(pipe, cancellation));
            }
            catch
            {
                if (pipe is not null) await pipe.DisposeAsync();
                clientSlots.Release();
                throw;
            }
        }
    }

    private async Task HandleClientSlotAsync(NamedPipeServerStream pipe, CancellationToken cancellation)
    {
        try { await HandleClientAsync(pipe, cancellation); }
        finally { clientSlots.Release(); }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellation)
    {
        await using (pipe)
        {
            string? id = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(160));
            try
            {
                var request = await Wire.ReadAsync(pipe, deadline.Token) ?? throw new InvalidDataException("Missing request.");
                Wire.ValidateAction(request);
                id = Guid.NewGuid().ToString();
                var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (pending.Count >= 8) throw new IOException("Too many concurrent requests.");
                pending[id] = completion;
                request["id"] = id;
                request["type"] = "invoke";
                request["deadline"] = DateTimeOffset.UtcNow.AddSeconds(150).ToUnixTimeMilliseconds();
                await SendAsync(request, deadline.Token);
                var response = await completion.Task.WaitAsync(deadline.Token);
                response.Remove("type");
                await Wire.WriteAsync(pipe, response, deadline.Token);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or
                System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
            {
                Console.Error.WriteLine(error.Message);
                if (pipe.IsConnected && !cancellation.IsCancellationRequested)
                {
                    try
                    {
                        await Wire.WriteAsync(pipe, Wire.Error(
                            error is OperationCanceledException
                                ? "Request timed out. Verify Google before retrying a write." : error.Message, id), cancellation);
                    }
                    catch (IOException writeError) { Console.Error.WriteLine(writeError.Message); }
                }
            }
            finally
            {
                if (id is not null) pending.TryRemove(id, out _);
            }
        }
    }

    private async Task HandleNativeAsync(JsonObject request, CancellationToken cancellation)
    {
        var id = request["id"]!.GetValue<string>();
        JsonObject response;
        await authLock.WaitAsync(cancellation);
        try
        {
            var result = request["action"]?.GetValue<string>() switch
            {
                "auth.status" => OAuth.Status(),
                "auth.connect" => await OAuth.ConnectAsync(cancellation),
                "auth.token" => await OAuth.TokenAsync(cancellation),
                "auth.disconnect" => await OAuth.DisconnectAsync(cancellation),
                _ => throw new InvalidDataException("Unknown native operation.")
            };
            response = new() { ["id"] = id, ["ok"] = true, ["result"] = result };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or
            System.Text.Json.JsonException or InvalidOperationException or ArgumentException or
            System.Security.Cryptography.CryptographicException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(error.Message);
            response = Wire.Error(error.Message, id);
        }
        finally { authLock.Release(); }
        response["type"] = "nativeResult";
        await SendAsync(response, cancellation);
    }
}
