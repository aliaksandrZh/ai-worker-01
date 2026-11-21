using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Config;
using PiAgent.Orchestrator.Orchestration;
using PiAgent.Platform.Auth;

namespace PiAgent.Orchestrator.Api;

/// <summary>
/// The Orchestration API read surface (04 §2) plus the internal command test hook, served by the
/// Orchestrator (it owns WorkItem/Workflow/Task state). DTOs stay deliberately close to the 04 §2
/// examples (camelCase, Guid ids — the exact transports were left open there). Every route requires
/// the decision-65 service token (`X-Service-Token`); the standard error shape is 04 §7
/// (`{ code, message, details }`). `POST /internal/commands` ingests a synthetic comment through the
/// identical pipeline as poller-delivered comments (CommandIngestion → admission → control replies):
/// the GitHub collaborator-permission check is skipped (owner ruling — the endpoint is service-token
/// protected and exists to test without GitHub), while the repository-policy checks, the config-
/// derived allowlist, admission gates and outbox replies apply unchanged. Idempotent by commentId.
/// </summary>
public static class OrchestrationApi
{
    public static WebApplication MapOrchestrationApi(this WebApplication app)
    {
        app.MapGet("/api/v1/work-items/{workItemId:guid}", (Guid workItemId, AppDbContext db, CancellationToken ct) =>
            GetWorkItemAsync(workItemId, db, ct)).RequireServiceToken();

        app.MapGet("/api/v1/workflows/{workflowId:guid}", (Guid workflowId, AppDbContext db, CancellationToken ct) =>
            GetWorkflowAsync(workflowId, db, ct)).RequireServiceToken();

        app.MapGet("/api/v1/tasks/{taskId:guid}", (Guid taskId, AppDbContext db, CancellationToken ct) =>
            GetTaskAsync(taskId, db, ct)).RequireServiceToken();

        app.MapPost("/internal/commands", (
            InternalCommandRequest request,
            AppDbContext db,
            ConfigurationState configurationState,
            CommandMetrics metrics,
            CancellationToken ct) =>
            InjectCommandAsync(request, db, configurationState, metrics, ct)).RequireServiceToken();
        return app;
    }

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new { code, message, details = new { } }, statusCode: statusCode);

    private static async Task<IResult> GetWorkItemAsync(
        Guid workItemId, AppDbContext db, CancellationToken cancellationToken)
    {
        var workItem = await db.WorkItems.AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == workItemId, cancellationToken);
        if (workItem is null)
            return Error(StatusCodes.Status404NotFound, "not_found", $"work item {workItemId} does not exist");
        var repository = await db.Repositories.AsNoTracking()
            .SingleAsync(r => r.Id == workItem.RepositoryId, cancellationToken);
        var workflowId = await db.Workflows.AsNoTracking()
            .Where(w => w.WorkItemId == workItem.Id)
            .Select(w => (Guid?)w.Id)
            .SingleOrDefaultAsync(cancellationToken);

        return Results.Json(new
        {
            id = workItem.Id,
            repository = repository.FullName,
            number = workItem.Number,
            type = workItem.Type,
            title = workItem.Title,
            state = workItem.State,
            workflowId,
        });
    }

    private static async Task<IResult> GetWorkflowAsync(
        Guid workflowId, AppDbContext db, CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows.AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == workflowId, cancellationToken);
        if (workflow is null)
            return Error(StatusCodes.Status404NotFound, "not_found", $"workflow {workflowId} does not exist");

        var workItem = await db.WorkItems.AsNoTracking()
            .SingleAsync(w => w.Id == workflow.WorkItemId, cancellationToken);
        var repository = await db.Repositories.AsNoTracking()
            .SingleAsync(r => r.Id == workItem.RepositoryId, cancellationToken);
        var tasks = await db.OrchestrationTasks.AsNoTracking()
            .Where(t => t.WorkflowId == workflow.Id)
            .OrderBy(t => t.Sequence)
            .Select(t => new
            {
                id = t.Id,
                type = t.Type,
                status = t.Status,
                sequence = t.Sequence,
                agent = db.AgentProfiles
                    .Where(p => p.Id == db.AgentProfileVersions
                        .Where(v => v.Id == t.AgentProfileVersionId)
                        .Select(v => v.AgentProfileId)
                        .FirstOrDefault())
                    .Select(p => p.Name)
                    .FirstOrDefault(),
                createdAt = t.CreatedAt,
                completedAt = t.CompletedAt,
            })
            .ToListAsync(cancellationToken);

        return Results.Json(new
        {
            id = workflow.Id,
            workItemId = workflow.WorkItemId,
            status = workflow.Status,
            failureReason = workflow.FailureReason,
            createdAt = workflow.CreatedAt,
            rootWorkItem = new
            {
                id = workItem.Id,
                repository = repository.FullName,
                number = workItem.Number,
                type = workItem.Type,
                title = workItem.Title,
                state = workItem.State,
            },
            currentTaskId = workflow.CurrentTaskId,
            tasks,
        });
    }

    private static async Task<IResult> GetTaskAsync(
        Guid taskId, AppDbContext db, CancellationToken cancellationToken)
    {
        var task = await db.OrchestrationTasks.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null)
            return Error(StatusCodes.Status404NotFound, "not_found", $"task {taskId} does not exist");

        var agent = await db.AgentProfileVersions.AsNoTracking()
            .Where(v => v.Id == task.AgentProfileVersionId)
            .Join(db.AgentProfiles, v => v.AgentProfileId, p => p.Id, (v, p) => p.Name)
            .SingleOrDefaultAsync(cancellationToken);
        var runs = await db.Runs.AsNoTracking()
            .Where(r => r.TaskId == task.Id)
            .OrderBy(r => r.AttemptNumber)
            .Select(r => new
            {
                id = r.Id,
                attemptNumber = r.AttemptNumber,
                status = r.Status,
                startedAt = r.StartedAt,
                completedAt = r.CompletedAt,
                errorCode = r.ErrorCode,
                errorMessage = r.ErrorMessage,
                resultSummary = r.ResultSummary,
            })
            .ToListAsync(cancellationToken);

        return Results.Json(new
        {
            id = task.Id,
            workflowId = task.WorkflowId,
            sequence = task.Sequence,
            type = task.Type,
            status = task.Status,
            agent,
            agentProfileVersionId = task.AgentProfileVersionId,
            triggerCommandId = task.TriggerCommandId,
            createdAt = task.CreatedAt,
            claimedAt = task.ClaimedAt,
            startedAt = task.StartedAt,
            completedAt = task.CompletedAt,
            failureReason = task.FailureReason,
            targetHeadSha = task.TargetHeadSha,
            targetBaseSha = task.TargetBaseSha,
            runs,
        });
    }

    private static async Task<IResult> InjectCommandAsync(
        InternalCommandRequest request,
        AppDbContext db,
        ConfigurationState configurationState,
        CommandMetrics metrics,
        CancellationToken cancellationToken)
    {
        if (request.WorkItemId == Guid.Empty)
            return Error(StatusCodes.Status400BadRequest, "validation", "work_item_id must be a valid UUID");
        if (string.IsNullOrWhiteSpace(request.Author))
            return Error(StatusCodes.Status400BadRequest, "validation", "author is required");
        if (request.CommentId <= 0)
            return Error(StatusCodes.Status400BadRequest, "validation", "comment_id must be a positive integer");
        if (string.IsNullOrWhiteSpace(request.Text))
            return Error(StatusCodes.Status400BadRequest, "validation", "text is required");
        if (!configurationState.Synchronized || configurationState.BotLogin is not { } botLogin)
            return Error(StatusCodes.Status503ServiceUnavailable, "not_ready",
                "configuration is not synchronized (cold-start gate)"); // 01 §13

        var workItem = await db.WorkItems.AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == request.WorkItemId, cancellationToken);
        if (workItem is null)
            return Error(StatusCodes.Status404NotFound, "not_found", $"work item {request.WorkItemId} does not exist");
        var repository = await db.Repositories.AsNoTracking()
            .SingleAsync(r => r.Id == workItem.RepositoryId, cancellationToken);
        if (request.RepositoryId is { } requested && requested != repository.Id)
            return Error(StatusCodes.Status400BadRequest, "validation",
                $"work item {workItem.Id} does not belong to repository {requested}");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var existing = await db.AgentCommands.AsNoTracking()
            .SingleOrDefaultAsync(c => c.GithubCommentId == request.CommentId, cancellationToken);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Results.Json(new { commandId = existing.Id, status = existing.Status }); // idempotent replay
        }

        var accepted = await CommandIngestion.TryAcceptAsync(
            db, metrics, botLogin, request.CommentId, request.Author, request.Text,
            editedAlready: false, workItem, repository, cancellationToken);
        AgentCommand command;
        if (accepted is not null)
        {
            command = accepted;
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
            return Results.Json(new { commandId = (Guid?)null, status = "ignored" }); // context-only text
        }

        if (command.Status == AgentCommandStatuses.Received)
        {
            // the hook skips the GitHub collaborator-permission lookup (owner ruling); the config-derived
            // allowlist still applies because it is a repository decision, not a GitHub lookup.
            if (CollaboratorPermissionGate.EvaluateAllowlist(repository.Configuration, request.Author)
                == AllowlistVerdict.Denied)
            {
                command.Status = AgentCommandStatuses.Rejected;
                command.RejectionReason = AgentCommandRejectionReasons.NotInAllowlist;
                command.ProcessedAt = DateTime.UtcNow;
                await new OutboxActionStore(db).EnqueueAsync(
                    new OutboxEnqueueRequest(repository.Id, workItem.Id, null, workItem.Number,
                        AgentCommandRejectionReplies.Text(AgentCommandRejectionReasons.NotInAllowlist,
                            request.Author), Guid.NewGuid()), cancellationToken);
            }
            else
            {
                command.Status = AgentCommandStatuses.Authorized;
                metrics.RecordAuthorized();
            }
        }

        if (command.Status == AgentCommandStatuses.Authorized)
        {
            var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, repository, cancellationToken);
            if (outcome is { } rejection)
            {
                command.Status = AgentCommandStatuses.Rejected;
                command.RejectionReason = rejection.Reason;
                command.ProcessedAt = DateTime.UtcNow;
                await new OutboxActionStore(db).EnqueueAsync(
                    new OutboxEnqueueRequest(repository.Id, workItem.Id, null, workItem.Number,
                        AgentCommandRejectionReplies.Text(rejection.Reason, rejection.Detail), Guid.NewGuid()),
                    cancellationToken);
            }
            else if (command.CommandName is AgentCommandNames.Status or AgentCommandNames.Help)
            {
                await ControlCommandHandler.HandleAsync(db, command, workItem, repository, cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Json(new { commandId = command.Id, status = command.Status });
    }

    public sealed record InternalCommandRequest(
        Guid? RepositoryId,
        Guid WorkItemId,
        string Author,
        long CommentId,
        string Text);
}
