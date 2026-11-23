using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PiAgent.Platform.Auth;

public sealed class ServiceTokenOptions
{
    public string? Orchestrator { get; set; }
    public string? HostMonitor { get; set; }
    public string? BuildWorker { get; set; }
}

/// <summary>
/// Internal contour: service-to-service credential check on internal endpoints.
/// Constant-time comparison; rejection logs never contain the presented token value.
/// </summary>
public sealed class ServiceTokenFilter(IOptions<ServiceTokenOptions> options, ILogger<ServiceTokenFilter> logger)
    : IEndpointFilter
{
    private readonly byte[][] _acceptedTokens =
        [.. new[] { options.Value.Orchestrator, options.Value.HostMonitor, options.Value.BuildWorker }
            .Where(token => !string.IsNullOrEmpty(token))
            .Select(token => Encoding.UTF8.GetBytes(token!))];

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var provided = context.HttpContext.Request.Headers[ServiceAuthExtensions.HeaderName].ToString();
        if (provided.Length == 0 || !MatchesAny(provided, _acceptedTokens))
        {
            logger.LogWarning("service token rejected {path}", context.HttpContext.Request.Path);
            return Results.Unauthorized();
        }
        return await next(context);
    }

    private static bool MatchesAny(string provided, byte[][] accepted)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return accepted.Any(token =>
            token.Length == providedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(token, providedBytes));
    }
}

public static class ServiceAuthExtensions
{
    public const string HeaderName = "X-Service-Token";

    /// <summary>Registers accepted service tokens (config section "ServiceTokens") for RequireServiceToken endpoints.</summary>
    public static IServiceCollection AddServiceTokens(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ServiceTokenOptions>().BindConfiguration("ServiceTokens");
        services.AddSingleton<ServiceTokenFilter>();
        return services;
    }

    public static RouteHandlerBuilder RequireServiceToken(this RouteHandlerBuilder builder)
    {
        return builder.AddEndpointFilter<ServiceTokenFilter>();
    }
}
