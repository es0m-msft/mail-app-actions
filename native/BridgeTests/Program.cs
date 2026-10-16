using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using EdgeGoogleActions;

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
}
static async Task Reject<T>(Func<Task> operation, string description) where T : Exception
{
    try { await operation(); }
    catch (T) { Check(true, description); return; }
    throw new Exception("FAIL: " + description);
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var cancellation = deadline.Token;
var value = new JsonObject { ["text"] = "Unicode: \u00e9 \ud83c\udf0e", ["ok"] = true };
using (var stream = new FragmentedStream())
{
    await Wire.WriteAsync(stream, value, cancellation);
    stream.Position = 0;
    Check((await Wire.ReadAsync(stream, cancellation))!.ToJsonString() == value.ToJsonString(),
        "Native frames survive fragmented reads and Unicode");
    Check(await Wire.ReadAsync(stream, cancellation) is null, "Clean EOF returns no frame");
}
await Reject<EndOfStreamException>(async () =>
    await Wire.ReadAsync(new MemoryStream([10, 0]), cancellation), "Truncated frame is rejected");
var oversized = new byte[4];
BinaryPrimitives.WriteUInt32LittleEndian(oversized, Wire.MaxBytes + 1);
await Reject<InvalidDataException>(async () =>
    await Wire.ReadAsync(new MemoryStream(oversized), cancellation), "Oversized incoming frame is rejected");
await Reject<InvalidDataException>(async () =>
    await Wire.WriteAsync(new MemoryStream(), new() { ["huge"] = new string('x', Wire.MaxBytes) }, cancellation),
    "Oversized outgoing frame is rejected");

var source = Path.GetFullPath(@"native\NativeHost\bin\Debug\net10.0-windows");
var fixture = Path.GetFullPath(Path.Combine("artifacts", "native-test-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(fixture);
try
{
    foreach (var file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)));
    const string origin = "chrome-extension://abcdefghijklmnopabcdefghijklmnop/";
    await File.WriteAllTextAsync(Path.Combine(fixture, "host.json"),
        new JsonObject { ["allowed_origins"] = new JsonArray(origin) }.ToJsonString(), cancellation);
    ProcessStartInfo StartInfo(string caller) => new("dotnet")
    {
        ArgumentList = { Path.Combine(fixture, "EdgeGoogleActions.NativeHost.dll"), caller },
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false
    };
    using (var invalid = Process.Start(StartInfo("chrome-extension://pppppppppppppppppppppppppppppppp/"))!)
    {
        await invalid.WaitForExitAsync(cancellation);
        Check(invalid.ExitCode != 0 && (await invalid.StandardError.ReadToEndAsync(cancellation)).Contains("not registered"),
            "Host rejects an unregistered extension origin");
    }
    using var host = Process.Start(StartInfo(origin))!;
    var errors = host.StandardError.ReadToEndAsync(cancellation);
    try
    {
        var input = host.StandardInput.BaseStream;
        var output = host.StandardOutput.BaseStream;
        await Wire.WriteAsync(input, new()
        {
            ["type"] = "nativeCall", ["id"] = "status", ["action"] = "auth.status"
        }, cancellation);
        var status = await Wire.ReadAsync(output, cancellation);
        Check(status?["type"]?.GetValue<string>() == "nativeResult" && status["ok"]!.GetValue<bool>(),
            "Native host is responsive and handles account status without exposing credentials");

        var invocation = BridgeClient.InvokeAsync("contacts.list", new(), cancellation);
        var request = await Wire.ReadAsync(output, cancellation);
        Check(request?["type"]?.GetValue<string>() == "invoke" &&
            request["action"]!.GetValue<string>() == "contacts.list" &&
            request["deadline"]!.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "External named-pipe request reaches the extension with a deadline");
        await Wire.WriteAsync(input, new()
        {
            ["type"] = "result", ["id"] = request!["id"]!.GetValue<string>(),
            ["ok"] = true, ["result"] = new JsonObject { ["contacts"] = new JsonArray("Example") }
        }, cancellation);
        var response = await invocation;
        Check(response["ok"]!.GetValue<bool>() && response["result"]!["contacts"]!.AsArray().Count == 1,
            "Extension result returns to the external caller");

        var concurrent = Enumerable.Range(0, 8)
            .Select(_ => BridgeClient.InvokeAsync("meetings.list", new(), cancellation)).ToArray();
        var activeIds = new List<string>();
        for (var index = 0; index < 8; index++)
            activeIds.Add((await Wire.ReadAsync(output, cancellation))!["id"]!.GetValue<string>());
        foreach (var activeId in activeIds.AsEnumerable().Reverse())
            await Wire.WriteAsync(input, new()
            {
                ["type"] = "result", ["id"] = activeId, ["ok"] = true,
                ["result"] = new JsonObject { ["eventId"] = activeId }
            }, cancellation);
        var concurrentResults = await Task.WhenAll(concurrent);
        Check(concurrentResults.All(item => item["ok"]!.GetValue<bool>()) &&
            concurrentResults.Select(item => item["result"]!["eventId"]!.GetValue<string>()).Distinct().Count() == 8,
            "Eight simultaneous external calls remain isolated and do not exhaust the pipe server");

        using var pipe = new NamedPipeClientStream(".", Wire.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellation);
        await Wire.WriteAsync(pipe, new() { ["action"] = "auth.token" }, cancellation);
        var rejected = await Wire.ReadAsync(pipe, cancellation);
        Check(rejected?["ok"]?.GetValue<bool>() == false &&
            rejected["error"]!["message"]!.GetValue<string>().Contains("Unknown external action"),
            "External callers cannot retrieve OAuth tokens");

        using var duplicate = Process.Start(StartInfo(origin))!;
        await duplicate.WaitForExitAsync(cancellation);
        Check(duplicate.ExitCode != 0 && (await duplicate.StandardError.ReadToEndAsync(cancellation)).Contains("already owns"),
            "A second profile cannot silently take over the active bridge");
        host.StandardInput.Close();
        await host.WaitForExitAsync(cancellation);
        Check(host.ExitCode == 0, "Host shuts down cleanly when Edge disconnects");
    }
    finally
    {
        if (!host.HasExited) { host.Kill(entireProcessTree: true); await host.WaitForExitAsync(); }
        var text = await errors;
        if (!string.IsNullOrWhiteSpace(text)) Console.WriteLine("Host diagnostic: " + text.Trim());
    }
}
finally { Directory.Delete(fixture, recursive: true); }

sealed class FragmentedStream : MemoryStream
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
}
