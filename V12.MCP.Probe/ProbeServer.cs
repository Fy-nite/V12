using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using V12.Core;
using V12.Core.Introspection;

namespace V12.MCP.Probe
{
    /// <summary>
    /// A deliberately small HTTP/1.1 listener over <see cref="TcpListener"/>.
    ///
    /// <para>
    /// Not <c>HttpListener</c> and not Kestrel: HttpListener needs a URL ACL on
    /// Windows for non-elevated processes, and Kestrel needs the ASP.NET shared
    /// framework which a Godot game does not ship. A game process only has to
    /// answer a handful of JSON routes, and every read/write goes straight
    /// through <see cref="SceneIntrospection"/>, which is the part that has to be
    /// correct.
    /// </para>
    /// </summary>
    internal sealed class ProbeServer : IDisposable
    {
        private const string AuthScheme = "Bearer ";

        private readonly GameRoot _root;
        private readonly ProbeOptions _options;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private int _port;

        public ProbeServer(GameRoot root, ProbeOptions options)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _listener = new TcpListener(ResolveBind(options.Bind), options.Port);
        }

        public string Url
        {
            get
            {
                string host = _options.Bind is "0.0.0.0" or "any" or "*" or "::" ? "127.0.0.1" : _options.Bind;
                return $"http://{host}:{_port}/";
            }
        }

        public bool Start()
        {
            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                GameRoot.Log.Error(ex, "MCP probe could not bind {Bind}:{Port}", _options.Bind, _options.Port);
                Console.Error.WriteLine($"[V12.MCP.Probe] could not bind {_options.Bind}:{_options.Port}: {ex.Message}");
                return false;
            }

            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
            return true;
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }

        private static IPAddress ResolveBind(string bind)
            => bind.ToLowerInvariant() switch
            {
                "localhost" => IPAddress.Loopback,
                "any" or "*" or "0.0.0.0" => IPAddress.Any,
                "::" or "::1" => IPAddress.IPv6Loopback,
                _ => IPAddress.TryParse(bind, out var address) ? address : IPAddress.Loopback,
            };

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) when (_cts.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    GameRoot.Log.Warning(ex, "MCP probe accept failed");
                    continue;
                }

                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                client.NoDelay = true;
                using var stream = client.GetStream();

                try
                {
                    var request = await ReadRequestAsync(stream, _cts.Token).ConfigureAwait(false);
                    if (request is null) return;

                    var (status, body) = await RouteAsync(request).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(body ?? new { }, body?.GetType() ?? typeof(object), ProbeJson.Options);
                    await WriteAsync(stream, status, json, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (Exception ex)
                {
                    GameRoot.Log.Warning(ex, "MCP probe request failed");
                }
            }
        }

        // ── routing ─────────────────────────────────────────────────────

        private async Task<(int Status, object? Body)> RouteAsync(HttpRequest request)
        {
            if (request.Method == "GET" && request.Path == "/v1/health")
            {
                return (200, new
                {
                    ok = true,
                    pid = Environment.ProcessId,
                    port = _port,
                    readOnly = _options.ReadOnly,
                    worlds = _root.WorldSessions.Count,
                    paused = _root.SimulationPaused,
                    tick = _root.SimulationTick,
                });
            }

            if (!Authorized(request))
                return (401, new { error = "missing or invalid bearer token" });

            try
            {
                if (request.Method == "GET" && request.Path == "/v1/worlds")
                {
                    var snapshot = await SnapshotAsync().ConfigureAwait(false);
                    return (200, snapshot.Worlds);
                }

                if (request.Method == "GET" && request.Path == "/v1/modules")
                    return (200, _root.Modules.Diagnostics);

                if (request.Method == "GET" && request.Path == "/v1/mods")
                {
                    var mods = _root.Registry.Get<V12.Core.Mods.IModHost>("Mods");
                    return (200, mods?.Mods ?? Array.Empty<V12.Core.Mods.ModInfo>());
                }

                if (request.Method == "GET" && request.Path == "/v1/snapshot")
                {
                    var snapshot = await SnapshotAsync().ConfigureAwait(false);
                    return (200, Shape(snapshot, request));
                }

                if (request.Method == "GET" && request.Path == "/v1/journal")
                {
                    int take = TryInt(request, "take") ?? 50;
                    return (200, _root.Introspection.RecentJournal(Math.Clamp(take, 0, 1024)));
                }

                if (request.Method == "GET" && request.Path.StartsWith("/v1/receipts/", StringComparison.Ordinal))
                {
                    string id = request.Path["/v1/receipts/".Length..];
                    return _root.Introspection.TryGetReceipt(id, out var receipt)
                        ? (200, receipt)
                        : (404, new { error = "no receipt for that request id" });
                }

                if (request.Method == "GET" && request.Path.StartsWith("/v1/element/", StringComparison.Ordinal))
                {
                    string idText = request.Path["/v1/element/".Length..];
                    if (!long.TryParse(idText, out long elementId))
                        return (400, new { error = "element id must be an integer" });

                    var snapshot = await SnapshotAsync().ConfigureAwait(false);
                    var element = snapshot.Elements.FirstOrDefault(e => e.Id == elementId);
                    if (element is null)
                        return (404, new { error = $"element {elementId} was not found" });

                    if (!IncludeComponents(request))
                        element.Components.Clear();

                    return (200, element);
                }

                if (request.Method == "POST" && request.Path == "/v1/commands")
                    return await ApplyCommandsAsync(request).ConfigureAwait(false);

                return (404, new { error = $"unknown route {request.Method} {request.Path}" });
            }
            catch (TimeoutException ex)
            {
                return (504, new { error = ex.Message, pending = _root.Introspection.PendingCount });
            }
            catch (JsonException ex)
            {
                return (400, new { error = "invalid JSON: " + ex.Message });
            }
        }

        private async Task<(int Status, object? Body)> ApplyCommandsAsync(HttpRequest request)
        {
            if (_options.ReadOnly)
                return (403, new { error = "probe is running in read-only mode (--mcp-readonly)" });

            var commands = ParseCommands(request.Body);
            if (commands.Count == 0)
                return (400, new { error = "no commands in the request body" });

            var tasks = new List<Task<CommandReceipt>>(commands.Count);
            foreach (var command in commands)
                tasks.Add(_root.Introspection.Enqueue(command));

            var all = Task.WhenAll(tasks);
            if (await Task.WhenAny(all, Task.Delay(_options.CommandTimeout)).ConfigureAwait(false) != all)
            {
                return (504, new
                {
                    error = $"commands were queued but did not drain within {_options.CommandTimeout.TotalMilliseconds:0}ms",
                    pending = _root.Introspection.PendingCount,
                    requestIds = commands.Select(c => c.RequestId).ToArray(),
                });
            }

            return (200, await all.ConfigureAwait(false));
        }

        private static List<SceneCommand> ParseCommands(byte[] body)
        {
            var commands = new List<SceneCommand>();
            if (body.Length == 0) return commands;

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                    if (item.Deserialize<SceneCommand>(ProbeJson.Options) is { } command) commands.Add(command);
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("commands", out var list))
            {
                foreach (var item in list.EnumerateArray())
                    if (item.Deserialize<SceneCommand>(ProbeJson.Options) is { } command) commands.Add(command);
            }
            else if (root.Deserialize<SceneCommand>(ProbeJson.Options) is { } single)
            {
                commands.Add(single);
            }

            return commands;
        }

        private async Task<WorldSnapshot> SnapshotAsync()
            => await _root.Introspection
                .RequestSnapshotAsync(_options.SnapshotTimeout)
                .ConfigureAwait(false);

        private static object Shape(WorldSnapshot snapshot, HttpRequest request)
        {
            bool includeComponents = IncludeComponents(request);

            IEnumerable<ElementSnapshot> elements = snapshot.Elements;

            if (TryInt(request, "depth") is { } maxDepth)
                elements = elements.Where(e => e.Depth <= maxDepth);

            if (request.Query.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
                elements = elements.Where(e => e.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true);

            if (request.Query.TryGetValue("component", out var component) && !string.IsNullOrWhiteSpace(component))
            {
                elements = elements.Where(e => e.Components.Any(c =>
                    string.Equals(c.Type, component, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.FullType, component, StringComparison.OrdinalIgnoreCase)));
            }

            if (request.Query.TryGetValue("active", out var activeText) && bool.TryParse(activeText, out bool active))
                elements = elements.Where(e => e.Active == active);

            if (TryInt(request, "limit") is { } limit)
                elements = elements.Take(limit);

            var list = elements.ToList();
            if (!includeComponents)
                foreach (var element in list) element.Components.Clear();

            return new
            {
                sequence = snapshot.Sequence,
                tick = snapshot.Tick,
                simulationTime = snapshot.SimulationTime,
                paused = snapshot.Paused,
                worlds = snapshot.Worlds,
                elements = list,
            };
        }

        private static int? TryInt(HttpRequest request, string key)
            => request.Query.TryGetValue(key, out var text) && int.TryParse(text, out int value) ? value : null;

        private static bool IncludeComponents(HttpRequest request)
            => !request.Query.TryGetValue("components", out var value)
               || !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

        private bool Authorized(HttpRequest request)
            => request.Headers.TryGetValue("Authorization", out var header)
               && header.StartsWith(AuthScheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(header[AuthScheme.Length..].Trim(), _options.Token, StringComparison.Ordinal);

        // ── wire ────────────────────────────────────────────────────────

        private sealed class HttpRequest
        {
            public string Method = "";
            public string Path = "/";
            public Dictionary<string, string> Query = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
            public byte[] Body = Array.Empty<byte>();
        }

        private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using var accumulated = new MemoryStream();
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read <= 0) return null;

                accumulated.Write(buffer, 0, read);
                if (accumulated.Length > 1024 * 1024) return null;

                headerEnd = FindHeaderEnd(accumulated.GetBuffer(), (int)accumulated.Length);
            }

            byte[] raw = accumulated.GetBuffer();
            string headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);
            string[] lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return null;

            string[] parts = lines[0].Split(' ', 3);
            if (parts.Length < 2) return null;

            var request = new HttpRequest { Method = parts[0].ToUpperInvariant() };

            string target = parts[1];
            int queryStart = target.IndexOf('?');
            request.Path = (queryStart >= 0 ? target[..queryStart] : target).TrimEnd('/');
            if (string.IsNullOrEmpty(request.Path)) request.Path = "/";
            if (queryStart >= 0) ParseQuery(target[(queryStart + 1)..], request.Query);

            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                request.Headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }

            int contentLength = 0;
            if (request.Headers.TryGetValue("Content-Length", out var lengthText))
                int.TryParse(lengthText, out contentLength);

            if (contentLength <= 0)
                return request;

            if (contentLength > 16 * 1024 * 1024) return null;

            var body = new byte[contentLength];
            int bodyStart = headerEnd + 4;
            int already = Math.Min((int)accumulated.Length - bodyStart, contentLength);
            if (already > 0)
                Array.Copy(raw, bodyStart, body, 0, already);

            int received = already;
            while (received < contentLength)
            {
                int read = await stream.ReadAsync(body.AsMemory(received, contentLength - received), ct).ConfigureAwait(false);
                if (read <= 0) break;
                received += read;
            }

            request.Body = received == contentLength ? body : body[..received];
            return request;
        }

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            var terminator = "\r\n\r\n"u8;
            for (int i = 0; i + terminator.Length <= length; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    return i;
            }
            return -1;
        }

        private static void ParseQuery(string query, Dictionary<string, string> into)
        {
            foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=');
                string key = equals >= 0 ? pair[..equals] : pair;
                string value = equals >= 0 ? pair[(equals + 1)..] : "";
                if (key.Length == 0) continue;
                into[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
            }
        }

        private static async Task WriteAsync(NetworkStream stream, int status, string json, CancellationToken ct)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            string head =
                $"HTTP/1.1 {status} {Reason(status)}\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Server: V12.MCP.Probe/1\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static string Reason(int status) => status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            500 => "Internal Server Error",
            504 => "Gateway Timeout",
            _ => "Error",
        };
    }

    /// <summary>One serializer configuration shared by every response.</summary>
    internal static class ProbeJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            IncludeFields = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
    }
}
