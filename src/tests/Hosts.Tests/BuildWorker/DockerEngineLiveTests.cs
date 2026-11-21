using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.BuildWorker;
using PiAgent.Platform.Http;
using Xunit;

namespace PiAgent.Hosts.Tests.BuildWorker;

/// <summary>
/// LIVE-ONLY check of the real Docker Engine client (unversioned HTTP paths over the unix socket, the
/// ustar build-context upload, image inspect) against the local daemon. Runs only when PI_LIVE_DOCKER=1
/// (`PI_LIVE_DOCKER=1 dotnet test tests/Hosts.Tests --filter DockerEngineLiveTests`). The socket path
/// defaults to the standard `/var/run/docker.sock`; override with PI_LIVE_DOCKER_SOCKET for a
/// non-standard daemon (e.g. Docker Desktop's ~/.docker/run/docker.sock). The check tags its image
/// uniquely and DELETES it before exiting — the daemon keeps only whatever alpine:3.20 layers were
/// needed anyway.
/// </summary>
public class DockerEngineLiveTests
{
    [Fact]
    public async Task BuildsARealImage_ReportsTheId_ThenCleansUp()
    {
        if (Environment.GetEnvironmentVariable("PI_LIVE_DOCKER") != "1")
            return; // live-only: the regular suite must stay host-independent
        var socketPath = Environment.GetEnvironmentVariable("PI_LIVE_DOCKER_SOCKET") ?? "/var/run/docker.sock";
        var engine = new DockerEngineClient(
            Options.Create(new DockerOptions { SocketPath = socketPath }),
            NullLogger<DockerEngineClient>.Instance);

        var workDirectory = Path.Combine(Path.GetTempPath(), "pi-live-build", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var tag = $"pi-live-build-check:{Guid.NewGuid().ToString("N")[..12]}";
        try
        {
            var dockerfile = "FROM alpine:3.20\nRUN echo pinned-context-verification > /pinned.txt\n";
            await File.WriteAllTextAsync(Path.Combine(workDirectory, "Dockerfile"), dockerfile);
            var tar = TarBuilder.Create(workDirectory);
            var log = Path.Combine(workDirectory, "build.log");

            await engine.BuildAsync(tag, tar, log, default);

            Assert.Contains("Successfully built", await File.ReadAllTextAsync(log));
            var image = await engine.InspectImageAsync(tag, default);
            Assert.NotNull(image);
            Assert.StartsWith("sha256:", image!.ImageId);
        }
        finally
        {
            Directory.Delete(workDirectory, recursive: true);
            RemoveImage(socketPath, tag);
        }
    }

    /// <summary>`DELETE /images/<tag>` over the socket — the engine client owns only builds/inspects.</summary>
    private static void RemoveImage(string socketPath, string tag)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.IP);
                await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath), token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        // force: remove a tag with no running container — the check image never runs
        _ = client.DeleteAsync($"http://localhost/images/{Uri.EscapeDataString(tag)}?force=true").Result;
    }
}
