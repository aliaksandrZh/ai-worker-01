using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PiAgent.Platform;
using PiAgent.Platform.Http;
using PiAgent.Platform.Logging;
using Xunit;

namespace PiAgent.Platform.Tests.Http;

public class PlatformEndpointsTests
{
    private sealed class FakeCheck(string name, bool ok, string? error = null) : IReadinessCheck
    {
        public string Name => name;
        public Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ok ? ReadinessResult.Ready() : ReadinessResult.NotReady(error ?? "unavailable"));
    }

    private static WebApplicationBuilder NewBuilder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.Configure<PiJsonConsoleFormatterOptions>(options => options.ServiceName = "test-service");
        return builder;
    }

    [Fact]
    public async Task Healthz_Returns200_Healthy()
    {
        var builder = NewBuilder();
        await using var app = builder.Build();
        app.MapPiEndpoints();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/healthz");

        Assert.True(response.IsSuccessStatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("healthy", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readyz_Returns200_WhenAllChecksReady()
    {
        var builder = NewBuilder();
        builder.Services.AddSingleton<IReadinessCheck>(new FakeCheck("db", ok: true));
        await using var app = builder.Build();
        app.MapPiEndpoints();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/readyz");

        Assert.Equal(200, (int)response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ready", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("ok", doc.RootElement.GetProperty("checks").GetProperty("db").GetString());
    }

    [Fact]
    public async Task Readyz_Returns503_AndNamesFailingCheck_WhenCheckNotReady()
    {
        var builder = NewBuilder();
        builder.Services.AddSingleton<IReadinessCheck>(new FakeCheck("db", ok: true));
        builder.Services.AddSingleton<IReadinessCheck>(new FakeCheck("docker", ok: false, error: "connection refused"));
        await using var app = builder.Build();
        app.MapPiEndpoints();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/readyz");

        Assert.Equal(503, (int)response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("notReady", doc.RootElement.GetProperty("status").GetString());
        var failed = doc.RootElement.GetProperty("failed")[0];
        Assert.Equal("docker", failed.GetProperty("name").GetString());
        Assert.Equal("connection refused", failed.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Metrics_ContainsService_AndGeneratedAt()
    {
        var builder = NewBuilder();
        await using var app = builder.Build();
        app.MapPiEndpoints();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/metrics");

        Assert.Equal(200, (int)response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("test-service", doc.RootElement.GetProperty("service").GetString());
        var generatedAt = doc.RootElement.GetProperty("generatedAtUtc").GetString();
        Assert.True(
            DateTime.TryParse(generatedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out _),
            $"generatedAtUtc not ISO-8601: {generatedAt}");
    }
}
