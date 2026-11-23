using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PiAgent.Platform.Http;

public sealed class DockerOptions
{
    /// <summary>Host Docker Engine unix socket (Host Monitor + Build Worker dependency).</summary>
    public string SocketPath { get; set; } = "/var/run/docker.sock";
}

/// <summary>Health check: Docker Engine reachable over its unix socket (GET /_ping).</summary>
public sealed class DockerSocketReadinessCheck(IOptions<DockerOptions> options) : IReadinessCheck
{
    public string Name => "docker";

    public async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.Value.SocketPath), token);
                    return new NetworkStream(socket, ownsSocket: true);
                },
                ConnectTimeout = TimeSpan.FromSeconds(2),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync("http://localhost/_ping", cancellationToken);
            return response.IsSuccessStatusCode
                ? ReadinessResult.Ready()
                : ReadinessResult.NotReady($"docker /_ping responded {(int)response.StatusCode}");
        }
        catch (OperationCanceledException)
        {
            return ReadinessResult.NotReady("check cancelled or timed out");
        }
        catch (Exception exception)
        {
            return ReadinessResult.NotReady(exception.Message);
        }
    }
}

public static class DockerRegistrationExtensions
{
    public static IServiceCollection AddDockerSocket(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.AddOptions<DockerOptions>().Bind(configuration.GetSection("Docker"));
        services.AddSingleton<IReadinessCheck, DockerSocketReadinessCheck>();
        return services;
    }
}

/// <summary>HTTP over the docker unix socket, shared by every socket-mounted component (readiness
/// check, Build Worker engine client, Host Monitor observer) — one connect callback, no duplication.</summary>
public static class SocketHttpClient
{
    public static HttpClient Create(IOptions<DockerOptions> options, int connectTimeoutSeconds = 5)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.Value.SocketPath), token);
                return new NetworkStream(socket, ownsSocket: true);
            },
            ConnectTimeout = TimeSpan.FromSeconds(connectTimeoutSeconds),
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; // callers bound via cancellation/timeout
    }
}
