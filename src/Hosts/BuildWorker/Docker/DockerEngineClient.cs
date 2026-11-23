using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Platform.Http;

namespace PiAgent.BuildWorker.Docker;

// ---- Docker Engine HTTP API over the read-only-mounted unix socket (§13: Build Worker dependency) ----
// The container has no docker CLI; the engine is driven over raw HTTP like the Platform readiness check.
// Paths are unversioned (same convention as PlatformEndpointExtensions' /_ping probe); verified against
// the daemon during the ticket walkthrough.

public sealed record EngineImageInfo(string ImageId);

/// <summary>The docker build itself failed: terminal (04 §6 decision: a real build failure ends the contract).</summary>
public sealed class DockerBuildException(string message) : Exception(message);

/// <summary>Ambiguous execution failure (daemon unreachable / transport / client timeout): retryable,
/// bounded by `BuildOptions.MaxExecutionAttempts` — never reported as a "real" build failure marker.</summary>
public sealed class BuildExecutionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The pinned build context cannot be used (missing Dockerfile, hash mismatch): terminal.</summary>
public sealed class BuildContextException(string message) : Exception(message);

public interface IDockerEngine
{
    /// <summary>`GET /images/{tag}/json`: the image when it exists, else null.</summary>
    Task<EngineImageInfo?> InspectImageAsync(string tag, CancellationToken cancellationToken);
    /// <summary>`POST /build?t=<tag>` with the context tar; the streamed progress goes to <paramref name="logFile"/>.</summary>
    Task BuildAsync(string tag, byte[] contextTar, string logFile, CancellationToken cancellationToken);
}

public sealed class DockerEngineClient : IDockerEngine
{
    private readonly HttpClient _http;
    private readonly ILogger<DockerEngineClient> _logger;

    public DockerEngineClient(IOptions<DockerOptions> options, ILogger<DockerEngineClient> logger)
    {
        _logger = logger;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.IP);
                await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(options.Value.SocketPath), token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            },
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; // callers bound with a cancellation token
    }

    public async Task<EngineImageInfo?> InspectImageAsync(string tag, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"http://localhost/images/{Uri.EscapeDataString(tag)}/json", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new BuildExecutionException($"docker image inspect {tag} answered {response.StatusCode:D}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return new EngineImageInfo(document.RootElement.GetProperty("Id").GetString() ?? "");
    }

    public async Task BuildAsync(string tag, byte[] contextTar, string logFile, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"http://localhost/build?t={Uri.EscapeDataString(tag)}&nocache=false")
        {
            Content = new ByteArrayContent(contextTar),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using (var log = new BuildLogExtensions.LogFileSession(logFile))
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var message = ReadDockerError(body) ?? $"docker build answered {response.StatusCode:D}: {Truncate(Minimize(body))}";
                throw new DockerBuildException(message);
            }

            var errors = new List<string>();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                await log.AppendLineAsync(line);
                if (TryReadError(line, out var error))
                {
                    errors.Add(error);
                    _logger.LogWarning("docker build {tag} reported an error: {error}", tag, error);
                }
            }

            if (errors.Count > 0)
                throw new DockerBuildException($"docker build for '{tag}' failed: {Truncate(Minimize(errors[^1]))}");
        }
    }

    private static bool TryReadError(string line, out string error)
    {
        error = "";
        if (line.Length == 0 || line[0] != '{')
            return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var property) && property.ValueKind == JsonValueKind.String)
            {
                error = property.GetString() ?? "";
                return error.Length > 0;
            }
        }
        catch (JsonException)
        {
            // non-JSON line: progress output only
        }
        return false;
    }

    private static string ReadDockerError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
                return message.GetString() ?? "";
        }
        catch (JsonException)
        {
        }
        return "";
    }

    private static string Minimize(string text) => text.Replace("\n", " ");
    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}

/// <summary>Sequential log-file helpers: the docker progress stream and section markers append as-is.</summary>
public static class BuildLogExtensions
{
    public static LogFileSession AsLog(this string path) => new(path);

    public readonly struct LogFileSession : IAsyncDisposable
    {
        private readonly StreamWriter _writer;
        public LogFileSession(string path)
        {
            var directory = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        }
        public Task AppendLineAsync(string line) => _writer.WriteLineAsync(line);
        public ValueTask DisposeAsync() => _writer.DisposeAsync();
    }
}
