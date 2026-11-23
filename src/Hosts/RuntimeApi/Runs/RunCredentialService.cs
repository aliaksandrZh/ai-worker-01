using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Api;
using PiAgent.RuntimeApi.Runs;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.RuntimeApi.Runs;

/// <summary>
/// Per-Run callback credentials: 256-bit random token, delivered to the Bridge only
/// as a read-only secret file, stored exclusively as a SHA-256 hash, scoped to its single runId, revocable
/// after terminal delivery. Never an environment variable, never shown to Pi, never logged.
/// </summary>
public static class RunCredentialService
{
    public static async Task<string> IssueAsync(AppDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        // 32 random bytes → compact url-safe string
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        db.RunCredentials.Add(new RunCredential
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            TokenHash = Sha256Hex(token),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        return token;
    }

    public static async Task<bool> VerifyAsync(AppDbContext db, Guid runId, string presented, CancellationToken cancellationToken)
    {
        // Presentation is hashed, then compared byte-wise (constant-time) — the raw token is never stored
        // and hash comparison stays timing-insensitive.
        var credential = await db.RunCredentials.AsNoTracking()
            .SingleOrDefaultAsync(c => c.RunId == runId, cancellationToken);
        return credential is { RevokedAt: null }
            && credential.TokenHash.Length == 64
            && CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(Sha256Hex(presented)), Convert.FromHexString(credential.TokenHash));
    }

    public static async Task<bool> IsRevokedAsync(AppDbContext db, Guid runId, string presented, CancellationToken cancellationToken)
    {
        var hash = Sha256Hex(presented);
        return await db.RunCredentials
            .AnyAsync(c => c.RunId == runId && c.TokenHash == hash && c.RevokedAt != null, cancellationToken);
    }

    public static async Task<bool> HasHashAsync(AppDbContext db, string presented, CancellationToken cancellationToken)
    {
        var hash = Sha256Hex(presented);
        return await db.RunCredentials.AnyAsync(c => c.TokenHash == hash, cancellationToken);
    }

    public static async Task RevokeAsync(AppDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        await db.RunCredentials
            .Where(c => c.RunId == runId && c.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.RevokedAt, DateTime.UtcNow), cancellationToken);
    }

    public static string Sha256Hex(string value)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Runtime-callback bearer auth: the presented token must be the unrevoked per-Run credential
/// for the runId in the route. 401 missing/unknown, 403 valid-but-other-run scope or revoked
/// (callbacks rejected for other runIds).
/// </summary>
public sealed class RunCallbackTokenFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var routeRunId = context.HttpContext.Request.RouteValues["runId"]?.ToString();
        if (!Guid.TryParse(routeRunId, out var runId))
            return Results.Json(new { code = "validation", message = "runId is not a UUID", details = new { } }, statusCode: 400);

        var authorization = context.HttpContext.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization["Bearer ".Length..].Trim().Length == 0)
            return Results.Json(new { code = "unauthorized", message = "per-Run bearer token required", details = new { } }, statusCode: 401);
        var token = authorization["Bearer ".Length..].Trim();

        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        if (await RunCredentialService.VerifyAsync(db, runId, token, context.HttpContext.RequestAborted))
            return await next(context);
        if (await RunCredentialService.IsRevokedAsync(db, runId, token, context.HttpContext.RequestAborted))
            return Results.Json(new { code = "token_revoked", message = "credential was revoked after terminal delivery", details = new { } }, statusCode: 403);
        if (await RunCredentialService.HasHashAsync(db, token, context.HttpContext.RequestAborted))
            return Results.Json(new { code = "callback_scope_denied", message = "credential is scoped to another run", details = new { } }, statusCode: 403);
        return Results.Json(new { code = "unauthorized", message = "unknown per-Run credential", details = new { } }, statusCode: 401);
    }
}
