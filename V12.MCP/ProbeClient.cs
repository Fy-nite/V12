using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace V12.MCP;

/// <summary>
/// HTTP client for the in-game probe. Connection details come from the
/// environment so an MCP client config is just a command plus two variables:
/// <c>V12_MCP_URL</c> (default http://127.0.0.1:7420) and <c>V12_MCP_TOKEN</c>
/// (read from the probe's handshake file).
/// </summary>
internal sealed class ProbeClient : IDisposable
{
    public static ProbeClient Current { get; private set; } = null!;

    public static void Configure(ProbeClient client) => Current = client;

    private readonly HttpClient _http;

    public string BaseUrl { get; }

    private ProbeClient(string baseUrl, string token)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public static ProbeClient FromEnvironment(string[] args)
    {
        string url = Option(args, "--url")
                     ?? Environment.GetEnvironmentVariable("V12_MCP_URL")
                     ?? "http://127.0.0.1:7420";

        string? token = Option(args, "--token")
                        ?? Environment.GetEnvironmentVariable("V12_MCP_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "V12_MCP_TOKEN is required. Read it from the probe handshake file " +
                "(%TEMP%/v12-mcp/probe-<pid>.json) or pass --token.");
        }

        return new ProbeClient(url, token.Trim());
    }

    public Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, path, null, cancellationToken);

    public Task<JsonElement> PostAsync(string path, object body, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, path, body, cancellationToken);

    /// <summary>Queue one scene command and return its receipt array.</summary>
    public Task<JsonElement> SendCommandAsync(object command, CancellationToken cancellationToken)
        => PostAsync("v1/commands", new { commands = new[] { command } }, cancellationToken);

    /// <summary>Queue several scene commands in one round trip.</summary>
    public Task<JsonElement> SendCommandsAsync(IEnumerable<object> commands, CancellationToken cancellationToken)
        => PostAsync("v1/commands", new { commands }, cancellationToken);

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, Wire.Options),
                Encoding.UTF8,
                "application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"probe returned {(int)response.StatusCode} {response.ReasonPhrase}: {text}");
        }

        if (string.IsNullOrWhiteSpace(text))
            return default;

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static string? Option(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal)) return args[i][(name.Length + 1)..];
        }
        return null;
    }

    public void Dispose() => _http.Dispose();
}
