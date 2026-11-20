using Microsoft.Extensions.Options;
using PiAgent.Platform.Http;

namespace PiAgent.RuntimeApi;

public sealed class KomodoOptions
{
    public string Url { get; set; } = "http://komodo-core:9120";
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
}

/// <summary>§13 health check: Komodo Core reachable. Credentials are exercised by later internal API calls;
/// for reachability any HTTP answer (even a 4xx) means the core is up.</summary>
public sealed class KomodoReadinessCheck(IOptions<KomodoOptions> options, IHttpClientFactory httpClientFactory)
    : IReadinessCheck
{
    public string Name => "komodo";

    public async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("komodo");
            client.Timeout = TimeSpan.FromSeconds(3);
            using var response = await client.GetAsync(options.Value.Url, cancellationToken);
            return ReadinessResult.Ready();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ReadinessResult.NotReady("check cancelled");
        }
        catch (OperationCanceledException)
        {
            return ReadinessResult.NotReady("komodo check timed out");
        }
        catch (Exception exception)
        {
            return ReadinessResult.NotReady(exception.Message);
        }
    }
}
