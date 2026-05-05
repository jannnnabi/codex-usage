using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace codexpet;

public sealed class CodexAppServerClient : IDisposable
{
    private const string SessionSource = "codex-usage";
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _serverProcess;
    private ClientWebSocket? _webSocket;
    private int _nextId = 1;
    private int _port;
    private bool _initialized;
    private bool _disposed;

    public async Task<RateLimitSnapshot> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);
            using var response = await SendRequestAsync("account/rateLimits/read", null, cancellationToken);
            return ParseRateLimitResponse(response.RootElement);
        }
        catch
        {
            ResetConnection();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_webSocket?.State == WebSocketState.Open && _initialized)
        {
            return;
        }

        ResetConnection();
        _port = GetFreePort();
        StartServer(_port);

        var socket = new ClientWebSocket();
        var uri = new Uri($"ws://127.0.0.1:{_port}");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await socket.ConnectAsync(uri, cancellationToken);
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or SocketException or IOException)
            {
                lastError = ex;
                await Task.Delay(200, cancellationToken);
            }
        }

        if (socket.State != WebSocketState.Open)
        {
            socket.Dispose();
            throw new InvalidOperationException("codex app-server did not open its local WebSocket endpoint.", lastError);
        }

        _webSocket = socket;
        await InitializeAsync(cancellationToken);
    }

    private void StartServer(int port)
    {
        var codexPath = CodexCommandResolver.ResolveCodexPath();
        var appServerArguments = $"app-server --listen ws://127.0.0.1:{port} --session-source {SessionSource}";
        var command = CodexCommand.Create(codexPath, appServerArguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _serverProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start codex app-server.");
        _serverProcess.OutputDataReceived += (_, _) => { };
        _serverProcess.ErrorDataReceived += (_, _) => { };
        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(
            "initialize",
            writer =>
            {
                writer.WritePropertyName("clientInfo");
                writer.WriteStartObject();
                writer.WriteString("name", "codex-usage");
                writer.WriteString("title", "Codex Usage HUD");
                writer.WriteString("version", "0.1.0");
                writer.WriteEndObject();

                writer.WritePropertyName("capabilities");
                writer.WriteStartObject();
                writer.WritePropertyName("optOutNotificationMethods");
                writer.WriteStartArray();
                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            cancellationToken);

        if (response.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(ReadErrorMessage(error));
        }

        _initialized = true;
    }

    private async Task<JsonDocument> SendRequestAsync(string method, Action<Utf8JsonWriter>? writeParams, CancellationToken cancellationToken)
    {
        var socket = _webSocket ?? throw new InvalidOperationException("codex app-server is not connected.");
        var id = _nextId++;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (writeParams is not null)
            {
                writer.WritePropertyName("params");
                writer.WriteStartObject();
                writeParams(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        var request = stream.ToArray();
        await socket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, cancellationToken);
        return await ReceiveResponseAsync(id, cancellationToken);
    }

    private async Task<JsonDocument> ReceiveResponseAsync(int id, CancellationToken cancellationToken)
    {
        var socket = _webSocket ?? throw new InvalidOperationException("codex app-server is not connected.");
        var buffer = new byte[64 * 1024];

        while (true)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new IOException("codex app-server closed the WebSocket connection.");
                }

                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            stream.Position = 0;
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id)
            {
                return document;
            }

            document.Dispose();
        }
    }

    private static RateLimitSnapshot ParseRateLimitResponse(JsonElement response)
    {
        if (response.TryGetProperty("result", out var result))
        {
            var selected = SelectRateLimitSnapshot(result);
            return ParseSnapshot(selected);
        }

        if (response.TryGetProperty("error", out var error))
        {
            var message = ReadErrorMessage(error);
            if (TryParseBackendBodyFromError(message, out var fallback))
            {
                return fallback;
            }

            throw new InvalidOperationException(message);
        }

        throw new InvalidOperationException("codex app-server returned an unexpected response.");
    }

    private static JsonElement SelectRateLimitSnapshot(JsonElement result)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out var byLimitId) && byLimitId.ValueKind == JsonValueKind.Object)
        {
            if (byLimitId.TryGetProperty("codex", out var codex))
            {
                return codex;
            }

            foreach (var property in byLimitId.EnumerateObject())
            {
                if (property.Value.TryGetProperty("limitId", out var limitId)
                    && limitId.ValueKind == JsonValueKind.String
                    && string.Equals(limitId.GetString(), "codex", StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }

            foreach (var property in byLimitId.EnumerateObject())
            {
                return property.Value;
            }
        }

        if (result.TryGetProperty("rateLimits", out var rateLimits))
        {
            return rateLimits;
        }

        throw new InvalidOperationException("codex app-server response did not include rate limits.");
    }

    private static RateLimitSnapshot ParseSnapshot(JsonElement snapshot)
    {
        return new RateLimitSnapshot(
            ParseWindow(snapshot, "primary"),
            ParseWindow(snapshot, "secondary"));
    }

    private static RateLimitWindowSnapshot? ParseWindow(JsonElement snapshot, string name)
    {
        if (!snapshot.TryGetProperty(name, out var window) || window.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var used = ReadInt(window, "usedPercent") ?? ReadInt(window, "used_percent") ?? 0;
        var durationMins = ReadLong(window, "windowDurationMins") ?? ReadLong(window, "window_duration_mins");
        var resetsAtSeconds = ReadLong(window, "resetsAt") ?? ReadLong(window, "resets_at");
        return new RateLimitWindowSnapshot(
            used,
            durationMins,
            resetsAtSeconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(resetsAtSeconds.Value));
    }

    private static bool TryParseBackendBodyFromError(string message, out RateLimitSnapshot snapshot)
    {
        snapshot = new RateLimitSnapshot(null, null);
        var marker = message.IndexOf("body=", StringComparison.Ordinal);
        if (marker < 0)
        {
            return false;
        }

        var body = ExtractJsonObject(message[(marker + "body=".Length)..]);
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("rate_limit", out var rateLimit))
            {
                return false;
            }

            snapshot = new RateLimitSnapshot(
                ParseBackendWindow(rateLimit, "primary_window"),
                ParseBackendWindow(rateLimit, "secondary_window"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static RateLimitWindowSnapshot? ParseBackendWindow(JsonElement rateLimit, string name)
    {
        if (!rateLimit.TryGetProperty(name, out var window) || window.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var used = ReadInt(window, "used_percent") ?? 0;
        var durationSeconds = ReadLong(window, "limit_window_seconds");
        var resetAt = ReadLong(window, "reset_at");
        return new RateLimitWindowSnapshot(
            used,
            durationSeconds is null ? null : Math.Max(1, durationSeconds.Value / 60),
            resetAt is null ? null : DateTimeOffset.FromUnixTimeSeconds(resetAt.Value));
    }

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return string.Empty;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        return string.Empty;
    }

    private static string ReadErrorMessage(JsonElement error)
    {
        if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
        {
            return message.GetString() ?? "codex app-server returned an error.";
        }

        return error.ToString();
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value) ? value : null;
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value) ? value : null;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void ResetConnection()
    {
        _initialized = false;
        _webSocket?.Dispose();
        _webSocket = null;

        if (_serverProcess is not null)
        {
            KillProcessTree(_serverProcess.Id);
            _serverProcess.Dispose();
            _serverProcess = null;
        }
    }

    private static void KillProcessTree(int processId)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/PID {processId} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(2000);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetConnection();
        _gate.Dispose();
    }
}
