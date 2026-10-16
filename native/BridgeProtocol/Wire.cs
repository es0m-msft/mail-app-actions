using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace EdgeGoogleActions;

public static class Wire
{
    public const int MaxBytes = 900_000;
    public static readonly string PipeName = "EdgeGoogleActions-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Environment.UserDomainName + "\\" + Environment.UserName)))[..24];

    public static async Task<JsonObject?> ReadAsync(Stream stream, CancellationToken cancellation = default)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellation);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellation);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaxBytes) throw new InvalidDataException("Invalid message length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellation);
        return JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("Expected a JSON object.");
    }

    public static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken cancellation = default)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Result exceeds the 900 KB bridge limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        await stream.WriteAsync(header, cancellation);
        await stream.WriteAsync(bytes, cancellation);
        await stream.FlushAsync(cancellation);
    }

    public static JsonObject Error(string message, string? id = null) => new()
    {
        ["id"] = id, ["ok"] = false, ["error"] = new JsonObject { ["message"] = message }
    };

    public static void ValidateAction(JsonObject request)
    {
        var action = request["action"]?.GetValue<string>();
        if (action is not ("contacts.list" or "meetings.list" or "mail.send" or "meetings.move"))
            throw new InvalidDataException("Unknown external action.");
        if (request["args"] is not null and not JsonObject)
            throw new InvalidDataException("args must be an object.");
    }
}

public static class BridgeClient
{
    public static async Task<JsonObject> InvokeAsync(string action, JsonObject args, CancellationToken cancellation = default)
    {
        var request = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["action"] = action, ["args"] = args
        };
        Wire.ValidateAction(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(170));
        using var pipe = new NamedPipeClientStream(".", Wire.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(5000, timeout.Token);
            await Wire.WriteAsync(pipe, request, timeout.Token);
            return await Wire.ReadAsync(pipe, timeout.Token) ?? throw new IOException("Bridge closed without a result.");
        }
        catch (TimeoutException)
        {
            return Wire.Error("Edge bridge unavailable. Open Edge, enable the extension, and check its account page.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return Wire.Error("Action timed out. A write may have completed; verify in Google before retrying.");
        }
    }
}
