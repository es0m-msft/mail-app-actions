using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace EdgeGoogleActions.NativeHost;

internal static class OAuth
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeGoogleActions");
    private static string ClientPath => Path.Combine(Root, "client.dpapi");
    private static string TokenPath => Path.Combine(Root, "tokens.dpapi");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private static readonly string[] Scopes =
    [
        "https://www.googleapis.com/auth/gmail.send",
        "https://www.googleapis.com/auth/contacts.readonly",
        "https://www.googleapis.com/auth/calendar.events"
    ];

    public static void ImportClient(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path));
        var installed = root?["installed"]?.AsObject() ??
            throw new InvalidDataException("Download a Google OAuth client of type Desktop app (not Web application).");
        if (string.IsNullOrWhiteSpace(installed["client_id"]?.GetValue<string>()) ||
            string.IsNullOrWhiteSpace(installed["client_secret"]?.GetValue<string>()))
            throw new InvalidDataException("Desktop client JSON is missing client_id or client_secret.");
        if (File.Exists(TokenPath))
            throw new InvalidOperationException("Disconnect Google in the extension before importing a different client.");
        Save(ClientPath, new()
        {
            ["client_id"] = installed["client_id"]!.GetValue<string>(),
            ["client_secret"] = installed["client_secret"]!.GetValue<string>()
        });
    }

    public static JsonObject Status() => new()
    {
        ["configured"] = File.Exists(ClientPath), ["connected"] = File.Exists(TokenPath)
    };

    public static async Task<JsonObject> ConnectAsync(CancellationToken cancellation)
    {
        if (File.Exists(TokenPath)) throw new InvalidOperationException("An account is already connected. Disconnect before switching accounts.");
        var client = Load(ClientPath);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = $"http://127.0.0.1:{port}/";
        var query = Encode(new()
        {
            ["client_id"] = client["client_id"]!.GetValue<string>(),
            ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = string.Join(" ", Scopes), ["state"] = state,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["access_type"] = "offline", ["prompt"] = "consent select_account"
        });
        _ = Process.Start(new ProcessStartInfo("msedge.exe", "https://accounts.google.com/o/oauth2/v2/auth?" + query)
        {
            UseShellExecute = true
        }) ?? throw new IOException("Unable to open Microsoft Edge for Google consent.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(2.5));
        string? code = null;
        while (code is null)
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = socket.GetStream();
            using var perRequest = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            perRequest.CancelAfter(TimeSpan.FromSeconds(5));
            var bytes = new List<byte>();
            var next = new byte[1];
            while (bytes.Count < 8192)
            {
                await stream.ReadExactlyAsync(next, perRequest.Token);
                bytes.Add(next[0]);
                if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
            }
            var request = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n")[0].Split(' ');
            var valid = request.Length == 3 && request[0] == "GET" &&
                Uri.TryCreate(redirect.TrimEnd('/') + request[1], UriKind.Absolute, out var uri) &&
                uri.AbsolutePath == "/";
            var parameters = valid ? ParseQuery(new Uri(redirect.TrimEnd('/') + request[1]).Query) : new();
            var match = parameters.GetValueOrDefault("state") == state;
            var text = match ? "Google sign-in received. You may close this tab and return to the extension."
                : "Invalid OAuth callback.";
            var body = Encoding.UTF8.GetBytes(text);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(match ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
            await stream.WriteAsync(header, perRequest.Token);
            await stream.WriteAsync(body, perRequest.Token);
            if (!match) continue;
            if (parameters.TryGetValue("error", out _)) throw new InvalidOperationException("Google authorization was denied.");
            code = parameters.GetValueOrDefault("code") ?? throw new InvalidDataException("OAuth callback is missing the authorization code.");
        }
        var tokens = await ExchangeAsync(new()
        {
            ["client_id"] = client["client_id"]!.GetValue<string>(),
            ["client_secret"] = client["client_secret"]!.GetValue<string>(),
            ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code"
        }, cancellation);
        var granted = (tokens["scope"]?.GetValue<string>() ?? "").Split(' ').ToHashSet();
        if (!Scopes.All(granted.Contains))
        {
            await RevokeAsync(tokens["access_token"]!.GetValue<string>(), cancellation);
            throw new InvalidOperationException("All three requested permissions are needed. Reconnect and grant all permissions.");
        }
        if (tokens["refresh_token"] is null) throw new InvalidDataException("Google did not issue a refresh token. Revoke the app in Google Account settings and reconnect.");
        Save(TokenPath, tokens);
        return new() { ["connected"] = true };
    }

    public static async Task<JsonObject> TokenAsync(CancellationToken cancellation)
    {
        var tokens = Load(TokenPath);
        if (tokens["expires_at"]!.GetValue<long>() <= DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds())
        {
            var client = Load(ClientPath);
            var updated = await ExchangeAsync(new()
            {
                ["client_id"] = client["client_id"]!.GetValue<string>(),
                ["client_secret"] = client["client_secret"]!.GetValue<string>(),
                ["refresh_token"] = tokens["refresh_token"]!.GetValue<string>(),
                ["grant_type"] = "refresh_token"
            }, cancellation);
            updated["refresh_token"] = tokens["refresh_token"]!.GetValue<string>();
            Save(TokenPath, updated);
            tokens = updated;
        }
        return new() { ["accessToken"] = tokens["access_token"]!.GetValue<string>() };
    }

    public static async Task<JsonObject> DisconnectAsync(CancellationToken cancellation)
    {
        if (File.Exists(TokenPath))
        {
            var tokens = Load(TokenPath);
            await RevokeAsync(tokens["refresh_token"]!.GetValue<string>(), cancellation);
            File.Delete(TokenPath);
        }
        return new() { ["connected"] = false };
    }

    private static async Task RevokeAsync(string token, CancellationToken cancellation)
    {
        using var response = await Http.PostAsync("https://oauth2.googleapis.com/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), cancellation);
        // A 400 also means that an already-invalid token no longer grants access.
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
            throw new HttpRequestException($"Google revocation returned HTTP {(int)response.StatusCode}. Local credentials were retained; try again.");
    }

    private static async Task<JsonObject> ExchangeAsync(Dictionary<string, string> values, CancellationToken cancellation)
    {
        using var response = await Http.PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(values), cancellation);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Google token exchange returned HTTP {(int)response.StatusCode}. Check the Desktop client and reconnect if consent expired.");
        var tokens = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))?.AsObject()
            ?? throw new InvalidDataException("Invalid Google token response.");
        if (tokens["access_token"] is null || tokens["expires_in"] is null)
            throw new InvalidDataException("Incomplete Google token response.");
        tokens["expires_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + tokens["expires_in"]!.GetValue<long>();
        return tokens;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Encode(Dictionary<string, string> values) =>
        string.Join("&", values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&')
        .Where(part => part.Contains('=')).Select(part => part.Split('=', 2))
        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1].Replace("+", " ")));

    private static JsonObject Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Google is not configured or connected. Import a Desktop OAuth client, then connect from the extension.");
        return JsonNode.Parse(Protect(File.ReadAllBytes(path), false))?.AsObject()
            ?? throw new InvalidDataException("Invalid protected credentials.");
    }

    private static void Save(string path, JsonObject value)
    {
        Directory.CreateDirectory(Root);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, Protect(Encoding.UTF8.GetBytes(value.ToJsonString()), true));
        File.Move(temporary, path, true);
    }

    // DPAPI binds credentials to this Windows user without adding a package dependency.
    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            var success = encrypt
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length);
            Marshal.FreeHGlobal(input.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob data, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
