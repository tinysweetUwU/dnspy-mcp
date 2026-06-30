using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.MCP.Settings;

namespace dnSpy.MCP.Mcp
{
    /// <summary>
    /// MCP HTTP server supporting both:
    ///   1. Streamable HTTP (POST /)         — MCP spec 2024-11-05, used by Claude Code, Cursor
    ///   2. HTTP+SSE (GET /sse + POST /messages) — legacy MCP, used by RooCode, Cline, Continue.dev
    ///
    /// RooCode/Cline connect with:
    ///   { "mcpServers": { "dnspy": { "url": "http://127.0.0.1:5150/sse" } } }
    ///
    /// Claude Code / Cursor connect with:
    ///   { "mcpServers": { "dnspy": { "url": "http://127.0.0.1:5150/" } } }
    /// </summary>
    public sealed class McpServerHost : IDisposable
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly McpSettings _settings;
        private readonly ToolRegistry _registry;
        private volatile bool _running;
        private readonly SemaphoreSlim _concurrency;
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private int _activeConnections;
        private readonly TaskCompletionSource _stoppedTcs = new();

        // SSE session management: sessionId -> response stream writer
        private readonly ConcurrentDictionary<string, SseSession> _sseSessions = new();

        private bool _authRequired;
        private byte[]? _authExpectedToken;

        private static readonly SemaphoreSlim _mutationLock = new(1, 1);

        public bool IsRunning => _running;

        public McpServerHost(McpSettings settings)
        {
            _settings = settings;
            _concurrency = new SemaphoreSlim(settings.MaxConcurrency);
            _registry = new ToolRegistry();
        }

        public async Task StartAsync()
        {
            if (_running) return;

            _cts = new CancellationTokenSource();

            var ipAddress = _settings.Host switch {
                "0.0.0.0" or "*" => IPAddress.Any,
                "127.0.0.1" or "localhost" => IPAddress.Loopback,
                _ => IPAddress.Parse(_settings.Host)
            };

            _listener = new TcpListener(ipAddress, _settings.Port);
            _listener.Start();

            McpLogger.Info($"Server started on http://{_settings.Host}:{_settings.Port}/");
            McpLogger.Info($"SSE endpoint: http://{_settings.Host}:{_settings.Port}/sse (RooCode/Cline)");
            McpLogger.Info($"HTTP endpoint: http://{_settings.Host}:{_settings.Port}/ (Claude Code/Cursor)");
            McpLogger.Info($"Registered {(_registry.ListTools().Count)} tools");

            _running = true;

            _authRequired = _settings.RequireAuth;
            _authExpectedToken = _authRequired && !string.IsNullOrEmpty(_settings.ApiToken)
                ? Encoding.UTF8.GetBytes("Bearer " + _settings.ApiToken)
                : null;

            if (_authRequired && _authExpectedToken == null)
                throw new InvalidOperationException(
                    "RequireAuth is enabled but ApiToken is empty. Set a token in MCP Settings or disable RequireAuth.");

            _ = Task.Run(() => ListenAsync(_cts.Token));
        }

        private async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _running)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync().WaitAsync(ct);
                    await _concurrency.WaitAsync(ct);
                    Interlocked.Increment(ref _activeConnections);
                    _ = Task.Run(async () => {
                        try { await HandleConnection(client, _cts!.Token); }
                        finally { _concurrency.Release(); client.Dispose(); }
                    }).ContinueWith(_ => {
                        if (Interlocked.Decrement(ref _activeConnections) == 0 && !_running)
                            _stoppedTcs.TrySetResult();
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        }

        private async Task HandleConnection(TcpClient client, CancellationToken ct)
        {
            try
            {
                using var stream = client.GetStream();
                stream.ReadTimeout = 30_000;
                stream.WriteTimeout = 30_000;
                var reader = new BufferedLineReader(stream);

                var requestLine = await reader.ReadLineAsync(ct);
                if (requestLine == null) return;

                var spaceIdx = requestLine.IndexOf(' ');
                if (spaceIdx < 0) return;
                var method = requestLine.Substring(0, spaceIdx);

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? headerLine;
                while ((headerLine = await reader.ReadLineAsync(ct)) != null && headerLine.Length > 0)
                {
                    var colonIdx = headerLine.IndexOf(':');
                    if (colonIdx > 0)
                        headers[headerLine.Substring(0, colonIdx).Trim()] = headerLine.Substring(colonIdx + 1).Trim();
                }

                var requestPath = requestLine.Substring(spaceIdx + 1);
                var queryIdx = requestPath.IndexOf(' ');
                var fullPath = queryIdx > 0 ? requestPath.Substring(0, queryIdx) : requestPath.Trim();

                // Split path and query string
                var qmarkIdx = fullPath.IndexOf('?');
                var path = qmarkIdx >= 0 ? fullPath.Substring(0, qmarkIdx) : fullPath;
                var queryString = qmarkIdx >= 0 ? fullPath.Substring(qmarkIdx + 1) : "";

                // Health check
                if (method == "GET" && (path == "/health" || path == "/ping"))
                {
                    var health = new JsonObject
                    {
                        ["status"] = "healthy",
                        ["port"] = _settings.Port,
                        ["uptime_seconds"] = (long)_uptime.Elapsed.TotalSeconds,
                        ["tools_count"] = _registry.ListTools().Count,
                        ["active_connections"] = _activeConnections,
                        ["sse_sessions"] = _sseSessions.Count
                    };
                    await WriteJsonResponseAsync(stream, 200, health);
                    return;
                }

                // CORS preflight
                if (method == "OPTIONS")
                {
                    var preflightHeaders = new List<(string, string)> {
                        ("Access-Control-Allow-Methods", "POST, GET, OPTIONS"),
                        ("Access-Control-Allow-Headers", "Content-Type, Authorization, Accept"),
                        ("Access-Control-Allow-Origin", GetCorsOrigin()),
                        ("Access-Control-Max-Age", "86400"),
                    };
                    await WriteResponseAsync(stream, 204, "No Content", null, preflightHeaders.ToArray());
                    return;
                }

                // Auth check
                if (_authRequired)
                {
                    headers.TryGetValue("Authorization", out var auth);
                    var provided = auth is null ? null : Encoding.UTF8.GetBytes(auth);
                    var authorized = provided != null && _authExpectedToken != null
                        && provided.Length == _authExpectedToken.Length
                        && CryptographicOperations.FixedTimeEquals(provided, _authExpectedToken);
                    if (!authorized)
                    {
                        await WriteJsonResponseAsync(stream, 401, new { error = "Unauthorized" });
                        return;
                    }
                }

                // ── SSE endpoint: GET /sse  (RooCode / Cline / Continue.dev) ──────────────
                if (method == "GET" && path == "/sse")
                {
                    await HandleSseConnectionAsync(stream, client, queryString, ct);
                    return;
                }

                // ── Message endpoint: POST /messages  (SSE transport messages) ─────────────
                if (method == "POST" && (path == "/messages" || path == "/message"))
                {
                    // Parse sessionId from query string: ?sessionId=xxx
                    var sessionId = ParseQueryParam(queryString, "sessionId");

                    var contentLength = headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var cl) ? cl : 0;
                    var maxBytes = (long)_settings.MaxRequestSizeMB * 1024 * 1024;
                    if (contentLength > maxBytes)
                    {
                        await WriteJsonResponseAsync(stream, 413, new { error = $"Payload too large (max {_settings.MaxRequestSizeMB}MB)" });
                        return;
                    }

                    string body;
                    if (contentLength > 0)
                    {
                        var buffer = new byte[contentLength];
                        await reader.ReadExactlyAsync(buffer, 0, contentLength, ct);
                        body = Encoding.UTF8.GetString(buffer);
                    }
                    else
                    {
                        body = string.Empty;
                    }

                    // Process message and send response through SSE session
                    if (sessionId != null && _sseSessions.TryGetValue(sessionId, out var session))
                    {
                        // Acknowledge the POST immediately
                        await WriteResponseAsync(stream, 202, "Accepted", null);

                        // Process and push result to SSE stream
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var responseJson = await ProcessJsonRpcBodyAsync(body);
                                if (responseJson != null)
                                    await session.SendEventAsync("message", responseJson.ToJsonString());
                            }
                            catch (Exception ex)
                            {
                                McpLogger.Error(ex, "SSE message processing");
                            }
                        });
                    }
                    else
                    {
                        // No session: process inline and respond (fallback for direct POST to /messages)
                        var responseJson = await ProcessJsonRpcBodyAsync(body);
                        if (responseJson == null)
                            await WriteResponseAsync(stream, 204, "No Content", null);
                        else
                            await WriteJsonResponseAsync(stream, 200, responseJson);
                    }
                    return;
                }

                // ── Streamable HTTP endpoint: POST /  (Claude Code, Cursor) ──────────────
                if (method == "POST" && (path == "/" || path == ""))
                {
                    var contentLength = headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var cl) ? cl : 0;
                    var maxBytes = (long)_settings.MaxRequestSizeMB * 1024 * 1024;
                    if (contentLength > maxBytes)
                    {
                        await WriteJsonResponseAsync(stream, 413, new { error = $"Payload too large (max {_settings.MaxRequestSizeMB}MB)" });
                        return;
                    }

                    string body;
                    if (contentLength > 0)
                    {
                        var buffer = new byte[contentLength];
                        await reader.ReadExactlyAsync(buffer, 0, contentLength, ct);
                        body = Encoding.UTF8.GetString(buffer);
                    }
                    else
                    {
                        body = string.Empty;
                    }

                    var responseJson = await ProcessJsonRpcBodyAsync(body);
                    if (responseJson == null)
                        await WriteResponseAsync(stream, 204, "No Content", null);
                    else
                        await WriteJsonResponseAsync(stream, 200, responseJson);
                    return;
                }

                await WriteJsonResponseAsync(stream, 405, new { error = "Method not allowed" });
            }
            catch (Exception ex)
            {
                McpLogger.Error(ex, "Connection handler error");
            }
        }

        /// <summary>
        /// Handle SSE connection (GET /sse).
        /// Sends an "endpoint" event with the POST URL for this session, then keeps the
        /// connection alive sending "ping" events every 30s until the client disconnects.
        /// </summary>
        private async Task HandleSseConnectionAsync(Stream stream, TcpClient client, string queryString, CancellationToken ct)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var session = new SseSession(stream, sessionId);

            _sseSessions[sessionId] = session;
            McpLogger.Info($"SSE session opened: {sessionId} (total: {_sseSessions.Count})");

            try
            {
                // Send SSE headers
                var sb = new StringBuilder();
                sb.Append("HTTP/1.1 200 OK\r\n");
                sb.Append("Content-Type: text/event-stream\r\n");
                sb.Append("Cache-Control: no-cache\r\n");
                sb.Append("Connection: keep-alive\r\n");
                sb.Append($"Access-Control-Allow-Origin: {GetCorsOrigin()}\r\n");
                sb.Append("Access-Control-Allow-Headers: Content-Type, Authorization\r\n");
                sb.Append("\r\n");
                var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct);
                await stream.FlushAsync(ct);

                // Send "endpoint" event — tells the client where to POST messages
                var endpointUrl = $"http://{_settings.Host}:{_settings.Port}/messages?sessionId={sessionId}";
                await session.SendEventAsync("endpoint", endpointUrl, ct);

                // Keep-alive loop: ping every 25s, exit when client disconnects or server stops
                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.DisconnectToken);
                while (!pingCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(25_000, pingCts.Token);
                        await session.SendEventAsync("ping", $"{{\"time\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}", pingCts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (IOException) { break; }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                McpLogger.Error(ex, $"SSE session {sessionId}");
            }
            finally
            {
                _sseSessions.TryRemove(sessionId, out _);
                session.Dispose();
                McpLogger.Info($"SSE session closed: {sessionId} (remaining: {_sseSessions.Count})");
            }
        }

        /// <summary>
        /// Parse a single query parameter from a query string like "sessionId=abc&foo=bar".
        /// </summary>
        private static string? ParseQueryParam(string queryString, string key)
        {
            if (string.IsNullOrEmpty(queryString)) return null;
            foreach (var part in queryString.Split('&'))
            {
                var eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq) == key)
                    return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return null;
        }

        /// <summary>
        /// Process a JSON-RPC body (single or batch) and return the response node.
        /// Returns null for notification-only requests.
        /// </summary>
        private async Task<JsonNode?> ProcessJsonRpcBodyAsync(string body)
        {
            JsonNode? requestNode;
            try { requestNode = JsonNode.Parse(body); }
            catch { return MakeError(null, -32700, "Parse error"); }

            JsonNode?[] requests;
            if (requestNode is JsonArray jsonArray)
            {
                var list = new List<JsonNode?>();
                for (int i = 0; i < jsonArray.Count; i++)
                    list.Add(jsonArray[i]);
                requests = list.ToArray();
            }
            else
            {
                requests = new[] { requestNode };
            }

            var results = new List<JsonNode?>();

            foreach (var req in requests)
            {
                if (req == null)
                {
                    results.Add(MakeError(null, -32600, "Invalid Request"));
                    continue;
                }

                var rpcMethod = req["method"]?.GetValue<string>();
                if (string.IsNullOrEmpty(rpcMethod))
                {
                    results.Add(MakeError(req["id"], -32600, "Invalid Request"));
                    continue;
                }

                var id = req["id"];
                var isNotification = id == null;

                switch (rpcMethod)
                {
                    case "initialize":
                        McpLogger.Info("Client initialized");
                        results.Add(isNotification ? null : CreateResponse(id, CreateServerCapabilities()));
                        break;

                    case "tools/list":
                        McpLogger.Info("Client requested tool list");
                        var tools = _registry.ListTools();
                        var toolListResult = new JsonObject { ["tools"] = JsonSerializer.SerializeToNode(tools) };
                        results.Add(isNotification ? null : CreateResponse(id, toolListResult));
                        break;

                    case "tools/call":
                        var toolName = req["params"]?["name"]?.GetValue<string>() ?? "";
                        McpLogger.Info($"Tool call: {toolName}");
                        var callResult = await HandleToolCallAsync(req);
                        results.Add(isNotification ? null : callResult);
                        break;

                    case "notifications/initialized":
                    case "shutdown":
                        results.Add(isNotification ? null : CreateResponse(id, new JsonObject()));
                        break;

                    case "ping":
                        results.Add(isNotification ? null : CreateResponse(id, new JsonObject()));
                        break;

                    default:
                        McpLogger.Warn($"Unknown method: {rpcMethod}");
                        results.Add(isNotification ? null : MakeError(id, -32601, $"Method not found: {rpcMethod}"));
                        break;
                }
            }

            // Filter nulls (notifications have no response)
            var nonNull = results.FindAll(r => r != null);
            if (nonNull.Count == 0) return null;
            if (nonNull.Count == 1) return nonNull[0];

            var batch = new JsonArray();
            foreach (var r in nonNull) batch.Add(r);
            return batch;
        }

        private string GetCorsOrigin()
        {
            var o = _settings.AllowedOrigins;
            return string.IsNullOrWhiteSpace(o) ? "*" : o;
        }

        // ── SSE Session ────────────────────────────────────────────────────────────────

        private sealed class SseSession : IDisposable
        {
            private readonly Stream _stream;
            private readonly SemaphoreSlim _writeLock = new(1, 1);
            private readonly CancellationTokenSource _disconnectCts = new();

            public string SessionId { get; }
            public CancellationToken DisconnectToken => _disconnectCts.Token;

            public SseSession(Stream stream, string sessionId)
            {
                _stream = stream;
                SessionId = sessionId;
            }

            public async Task SendEventAsync(string eventType, string data, CancellationToken ct = default)
            {
                // SSE format:
                //   event: <type>\n
                //   data: <payload>\n
                //   \n
                var sb = new StringBuilder();
                sb.Append($"event: {eventType}\n");
                // Multi-line data: each line must be prefixed with "data: "
                foreach (var line in data.Split('\n'))
                    sb.Append($"data: {line}\n");
                sb.Append("\n");

                var bytes = Encoding.UTF8.GetBytes(sb.ToString());

                await _writeLock.WaitAsync(ct);
                try
                {
                    await _stream.WriteAsync(bytes, 0, bytes.Length, ct);
                    await _stream.FlushAsync(ct);
                }
                catch (IOException)
                {
                    _disconnectCts.TryCancel();
                    throw;
                }
                finally
                {
                    _writeLock.Release();
                }
            }

            public void Dispose()
            {
                _disconnectCts.TryCancel();
                _disconnectCts.Dispose();
                _writeLock.Dispose();
            }
        }

        // ── Buffered reader ────────────────────────────────────────────────────────────

        private sealed class BufferedLineReader
        {
            private readonly Stream _stream;
            private readonly byte[] _buf = new byte[4096];
            private int _bufPos, _bufLen;

            public BufferedLineReader(Stream stream) => _stream = stream;

            public async Task<string?> ReadLineAsync(CancellationToken ct = default)
            {
                var sb = new StringBuilder(256);
                while (true)
                {
                    if (_bufPos >= _bufLen)
                    {
                        _bufLen = await _stream.ReadAsync(_buf, 0, _buf.Length, ct);
                        if (_bufLen == 0) return sb.Length > 0 ? sb.ToString() : null;
                        _bufPos = 0;
                    }
                    var b = _buf[_bufPos++];
                    if (b == '\r')
                    {
                        if (_bufPos >= _bufLen)
                        {
                            _bufLen = await _stream.ReadAsync(_buf, 0, _buf.Length, ct);
                            _bufPos = 0;
                        }
                        if (_bufLen > 0 && _buf[_bufPos] == '\n') _bufPos++;
                        break;
                    }
                    if (b == '\n') break;
                    sb.Append((char)b);
                }
                return sb.ToString();
            }

            public async Task ReadExactlyAsync(byte[] dest, int offset, int count, CancellationToken ct = default)
            {
                var buffered = Math.Min(count, _bufLen - _bufPos);
                if (buffered > 0)
                {
                    Array.Copy(_buf, _bufPos, dest, offset, buffered);
                    _bufPos += buffered;
                    offset += buffered;
                    count -= buffered;
                }
                if (count > 0)
                    await _stream.ReadAtLeastAsync(new Memory<byte>(dest, offset, count), count, cancellationToken: ct, throwOnEndOfStream: true);
            }
        }

        // ── HTTP response helpers ──────────────────────────────────────────────────────

        private async Task WriteJsonResponseAsync(Stream stream, int statusCode, object data)
        {
            var json = data is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(data);
            var body = Encoding.UTF8.GetBytes(json);

            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {statusCode} {GetReasonPhrase(statusCode)}\r\n");
            sb.Append("Content-Type: application/json\r\n");
            sb.Append($"Content-Length: {body.Length}\r\n");
            sb.Append($"Access-Control-Allow-Origin: {GetCorsOrigin()}\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type, Authorization\r\n");
            sb.Append("\r\n");

            var header = Encoding.UTF8.GetBytes(sb.ToString());
            await stream.WriteAsync(header, 0, header.Length);
            await stream.WriteAsync(body, 0, body.Length);
        }

        private static async Task WriteResponseAsync(Stream stream, int statusCode, string reason, byte[]? body, params (string name, string value)[] headers)
        {
            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {statusCode} {reason}\r\n");
            if (body != null)
                sb.Append($"Content-Length: {body.Length}\r\n");
            foreach (var (name, value) in headers)
                sb.Append($"{name}: {value}\r\n");
            sb.Append("\r\n");

            var header = Encoding.UTF8.GetBytes(sb.ToString());
            await stream.WriteAsync(header, 0, header.Length);
            if (body != null)
                await stream.WriteAsync(body, 0, body.Length);
        }

        private static string GetReasonPhrase(int code) => code switch
        {
            200 => "OK",
            202 => "Accepted",
            204 => "No Content",
            401 => "Unauthorized",
            405 => "Method Not Allowed",
            413 => "Payload Too Large",
            _ => "Unknown"
        };

        // ── Tool dispatch ──────────────────────────────────────────────────────────────

        private async Task<JsonNode> HandleToolCallAsync(JsonNode request)
        {
            var toolName = request["params"]?["name"]?.GetValue<string>();
            var arguments = request["params"]?["arguments"] as JsonObject;

            if (string.IsNullOrEmpty(toolName))
                return MakeError(request["id"], -32602, "Missing tool name");

            var tool = _registry.GetTool(toolName);
            if (tool == null)
                return MakeError(request["id"], -32601, $"Unknown tool: {toolName}");

            try
            {
                var timeout = TimeSpan.FromSeconds(_settings.ToolTimeoutSeconds);

                SemaphoreSlim? heldLock = null;
                if (tool.IsMutation)
                {
                    await _mutationLock.WaitAsync(timeout);
                    heldLock = _mutationLock;
                }
                try
                {
                    var result = await Task.Run(() => tool.Invoke(arguments)).WaitAsync(timeout);
                    var content = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = result }
                    };
                    return CreateResponse(request["id"], new JsonObject { ["content"] = content });
                }
                finally
                {
                    heldLock?.Release();
                }
            }
            catch (TimeoutException)
            {
                McpLogger.Warn($"Tool '{toolName}' timed out after {_settings.ToolTimeoutSeconds}s");
                return MakeError(request["id"], -32603, $"Tool execution timed out after {_settings.ToolTimeoutSeconds} seconds");
            }
            catch (Exception ex)
            {
                McpLogger.Error(ex, $"Tool '{toolName}'");
                return MakeError(request["id"], -32603, $"Tool execution failed: {ex.Message}");
            }
        }

        // ── JSON-RPC helpers ───────────────────────────────────────────────────────────

        private static JsonObject CreateServerCapabilities()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            return new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "dnSpy-MCP", ["version"] = version }
            };
        }

        private static JsonObject CreateResponse(JsonNode? id, JsonNode result)
        {
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["result"] = result };
            if (id != null) response["id"] = id.DeepClone();
            return response;
        }

        private static JsonObject MakeError(JsonNode? id, int code, string message)
        {
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
            };
            if (id != null) response["id"] = id.DeepClone();
            return response;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────────────

        public void Stop()
        {
            if (!_running) return;

            _running = false;
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;

            // Close all SSE sessions
            foreach (var session in _sseSessions.Values)
                session.Dispose();
            _sseSessions.Clear();

            if (_activeConnections > 0)
            {
                _ = Task.Run(async () => {
                    try
                    {
                        await _stoppedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        McpLogger.Info("Server stopped (all connections drained)");
                    }
                    catch (TimeoutException)
                    {
                        McpLogger.Warn("Shutdown grace (3s) elapsed: some connections were not closed gracefully");
                    }
                    catch (OperationCanceledException) { }
                });
            }

            McpLogger.Info("Server stopped");
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }

    // Extension method for CancellationTokenSource to avoid NRE in .NET 8
    internal static class CtsExtensions
    {
        public static void TryCancel(this CancellationTokenSource cts)
        {
            try { cts.Cancel(); } catch { }
        }
    }
}
