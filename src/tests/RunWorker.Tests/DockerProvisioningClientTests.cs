using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PiAgent.RunWorker.Docker;
using Xunit;

namespace PiAgent.RunWorker.Tests;

/// <summary>
/// The DockerProvisioningClient's image-inspect mapping over a fake HTTP handler (the same
/// raw-HTTP surface as the unix socket, no daemon): 200 → the image exists, 404 → it does
/// not, anything else keeps the existing failure semantics (DockerProvisioningException).
/// </summary>
public sealed class DockerProvisioningClientTests
{
    private static DockerProvisioningClient Client(HttpResponseMessage response, List<HttpRequestMessage>? requests = null) =>
        new(
            new HttpClient(new FakeHandler(_ => response) { Requests = requests }),
            NullLogger<DockerProvisioningClient>.Instance);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage>? Requests { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests?.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task ImageExists_maps_200_to_true()
    {
        var requests = new List<HttpRequestMessage>();
        var client = Client(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Id":"sha256:abc"}"""),
        }, requests);

        Assert.True(await client.ImageExistsAsync("agent-testing:latest", CancellationToken.None));
        Assert.Equal("/images/agent-testing:latest/json", requests[0].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task ImageExists_maps_404_to_false()
    {
        var client = Client(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("no such image"),
        });

        Assert.False(await client.ImageExistsAsync("agent-testing:latest", CancellationToken.None));
    }

    [Fact]
    public async Task ImageExists_throws_on_an_unexpected_status()
    {
        var client = Client(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("daemon down"),
        });

        await Assert.ThrowsAsync<DockerProvisioningException>(
            () => client.ImageExistsAsync("agent-testing:latest", CancellationToken.None));
    }
}
