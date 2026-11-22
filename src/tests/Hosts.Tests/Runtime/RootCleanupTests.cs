using System.Net;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Hosts.Tests.Config;
using Xunit;

namespace PiAgent.Hosts.Tests.Runtime;

public class RootCleanupTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task StopsRemovesAndRetainsRows()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        var started = await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        var workflowId = preparation.Task.WorkflowId;
        var response = await api.ServiceClient.PostAsync($"/internal/runtime/workflows/{workflowId}/cleanup", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"\"workflowId\":\"{workflowId}\"", await response.Content.ReadAsStringAsync());
        Assert.Single(api.Komodo.Removed);

        var session = await api.Db.AgentSessions.AsNoTracking().SingleAsync(s => s.RunId == preparation.Run.Id);
        var workspace = await api.Db.Workspaces.AsNoTracking().SingleAsync(w => w.TaskId == preparation.Task.Id);
        Assert.Equal(AgentSessionStorageStatuses.Deleted, session.StorageStatus);
        Assert.Equal(WorkspaceStorageStatuses.Deleted, workspace.StorageStatus);
        Assert.NotNull(session.FileDeletedAt);
        Assert.NotNull(workspace.DeletedAt);
    }

    [Fact]
    public async Task MissingWorkflowTree_IsRecordedWithoutDeletingDatabaseRows()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        var started = await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        var workflowId = preparation.Task.WorkflowId;
        Directory.Delete(Path.Combine(api.StorageRoot, "workflows", workflowId.ToString()), recursive: true);
        var response = await api.ServiceClient.PostAsync($"/internal/runtime/workflows/{workflowId}/cleanup", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("missing", await response.Content.ReadAsStringAsync());

        var session = await api.Db.AgentSessions.AsNoTracking().SingleAsync(s => s.RunId == preparation.Run.Id);
        var workspace = await api.Db.Workspaces.AsNoTracking().SingleAsync(w => w.TaskId == preparation.Task.Id);
        Assert.Equal(AgentSessionStorageStatuses.Missing, session.StorageStatus);
        Assert.Equal(WorkspaceStorageStatuses.Missing, workspace.StorageStatus);
    }
}
