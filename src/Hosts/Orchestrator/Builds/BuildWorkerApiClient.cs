using PiAgent.Platform.Contracts;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace PiAgent.Orchestrator.Builds;

/// <summary>Decision-65 contour for Orchestrator→Build Worker calls: the Orchestrator presents its own
/// service credential, which the Build Worker's endpoints accept as `ServiceTokens__Orchestrator`
/// (the same direction/shape as task 09's RuntimeApi calls).</summary>
public sealed class BuildWorkerOptions
{
    public string BaseUrl { get; set; } = "http://build-worker:8080";
    /// <summary>The token presented as `X-Service-Token`; must equal the Build Worker's `ServiceTokens__Orchestrator`.</summary>
    public string ServiceToken { get; set; } = "orchestrator-dev-token";
    /// <summary>Transient dispatch/poll failures back off exponentially: initial × 2^(attempts-1), capped.</summary>
    public int InitialBackoffSeconds { get; set; } = 10;
    public int MaxBackoffSeconds { get; set; } = 3600;
    /// <summary>Per-tick cap of BuildJobs dispatched/polled (bounds one tick's work).</summary>
    public int BatchLimit { get; set; } = 10;
}

/// <summary>HTTP client for the Build Worker internal build API (04 §6). POST is expected to answer
/// `202` quickly; the outcome is always polled on a later tick — never held open.</summary>
public sealed class BuildWorkerApiClient(HttpClient http, IOptions<BuildWorkerOptions> options)
{
    public async Task<HttpResponseMessage> DispatchAsync(BuildDispatchRequest request, CancellationToken cancellationToken)
    {
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post,
            $"{options.Value.BaseUrl.TrimEnd('/')}/internal/builds")
        {
            Content = new StringContent(JsonSerializer.Serialize(request, BuildStatuses.PayloadJson), Encoding.UTF8, "application/json"),
        };
        requestMessage.Headers.Add("X-Service-Token", options.Value.ServiceToken);
        return await http.SendAsync(requestMessage, cancellationToken);
    }

    public async Task<HttpResponseMessage> PollAsync(Guid buildId, CancellationToken cancellationToken)
    {
        using var requestMessage = new HttpRequestMessage(HttpMethod.Get,
            $"{options.Value.BaseUrl.TrimEnd('/')}/internal/builds/{buildId}");
        requestMessage.Headers.Add("X-Service-Token", options.Value.ServiceToken);
        return await http.SendAsync(requestMessage, cancellationToken);
    }
}
