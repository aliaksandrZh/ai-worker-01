using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiAgent.Platform.Auth;
using Xunit;

namespace PiAgent.Platform.Tests.Auth;

public class ServiceAuthTests
{
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public readonly List<string> Lines = [];
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Lines);
        public void Dispose() { }

        private sealed class CapturingLogger(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }

    private static async Task<(WebApplication app, List<string> logLines)> BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var logs = new CapturingLoggerProvider();
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton<ServiceTokenFilter>();
        builder.Services.Configure<ServiceTokenOptions>(options => options.Orchestrator = "tok-orch");
        var app = builder.Build();
        app.MapGet("/internal/echo", () => Results.Ok("reached")).RequireServiceToken();
        await app.StartAsync();
        return (app, logs.Lines);
    }

    [Fact]
    public async Task MissingToken_Returns401()
    {
        var (app, _) = await BuildApp();
        await using (app)
        {
            var response = await app.GetTestClient().GetAsync("/internal/echo");
            Assert.Equal(401, (int)response.StatusCode);
        }
    }

    [Fact]
    public async Task WrongToken_Returns401_SecretNeverInResponseOrLogs()
    {
        var (app, logLines) = await BuildApp();
        await using (app)
        {
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Add(ServiceAuthExtensions.HeaderName, "tok-wrong");
            var response = await client.GetAsync("/internal/echo");

            Assert.Equal(401, (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("tok-wrong", body);
            Assert.Contains(logLines, line => line.Contains("service token rejected"));
            Assert.All(logLines, line => Assert.DoesNotContain("tok-wrong", line));
        }
    }

    [Fact]
    public async Task CorrectToken_PassesFilter()
    {
        var (app, _) = await BuildApp();
        await using (app)
        {
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Add(ServiceAuthExtensions.HeaderName, "tok-orch");
            var response = await client.GetAsync("/internal/echo");

            Assert.Equal(200, (int)response.StatusCode);
            Assert.Equal("\"reached\"", await response.Content.ReadAsStringAsync());
        }
    }
}
