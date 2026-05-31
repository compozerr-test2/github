using Api.Abstractions;
using Api.Data.Repositories;
using Github.Abstractions;
using Github.Models;
using Github.Repositories;
using Jobs;
using MediatR;
using Serilog;

namespace Github.Jobs;

public sealed class PushWebhookProcessorJob(
    IPushWebhookEventRepository pushWebhookEventRepository,
    IProjectEnvironmentRepository projectEnvironmentRepository,
    IMediator mediator) : JobBase<PushWebhookProcessorJob, PushWebhookEventId>
{
    public override async Task ExecuteAsync(PushWebhookEventId pushWebhookEventId)
    {
        var @event = await pushWebhookEventRepository.GetByIdAsync(pushWebhookEventId);

        if (@event is null or { HandledAt: not null } or { ErroredAt: not null })
        {
            // If the event is already handled or errored, we skip processing
            return;
        }

        await HandleEventAsync(@event);
    }

    private async Task HandleEventAsync(
        PushWebhookEvent pushWebhookEvent)
    {
        var log = Log.ForContext(nameof(pushWebhookEvent), pushWebhookEvent.Id);

        try
        {
            log.Information("Processing PushWebhookEvent {PushWebhookEventId}", pushWebhookEvent.Id);

            if (Uri.TryCreate(pushWebhookEvent.Event.Repository?.CloneUrl, UriKind.Absolute, out var gitUrl) is false)
            {
                throw new InvalidOperationException("Invalid repository clone URL.");
            }

            // A single repo can back multiple projects (e.g. several services in one
            // monorepo), so fan out: deploy every matching project that has an
            // environment for the pushed branch with auto-deploy enabled.
            var projectIds = await pushWebhookEventRepository.GetProjectIdsFromGitUrlAsync(gitUrl);
            if (projectIds.Count == 0)
            {
                throw new CouldNotFindProjectFromGitUrlException(gitUrl);
            }

            if (pushWebhookEvent.Event.HeadCommit is null)
            {
                log.Information("No head commit in PushWebhookEvent {PushWebhookEventId}, skipping (likely branch deletion)",
                                pushWebhookEvent.Id);
                return;
            }

            if (pushWebhookEvent.Event.HeadCommit is
                {
                    Id: null or "",
                    Message: null or "",
                    Author: { Name: null or "", Email: null or "" }
                })
            {
                throw new InvalidOperationException("Head commit information is incomplete.");
            }

            var commitMessage = pushWebhookEvent.Event.HeadCommit!.Message!;
            var branch = pushWebhookEvent.Event.Ref.Replace("refs/heads/", string.Empty);

            if (commitMessage.Contains("[skip deploy]"))
            {
                log.Information("Skipping deployment for commit message '[skip deploy]' in PushWebhookEvent {PushWebhookEventId}",
                                pushWebhookEvent.Id);
                return;
            }

            foreach (var projectId in projectIds)
            {
                var projectEnvironment = await projectEnvironmentRepository.GetProjectEnvironmentByBranchAsync(
                    projectId, branch);

                if (projectEnvironment is null)
                {
                    log.Information("No environment found for branch {Branch} on project {ProjectId}, skipping deployment in PushWebhookEvent {PushWebhookEventId}",
                                    branch, projectId, pushWebhookEvent.Id);
                    continue;
                }

                if (projectEnvironment.EffectiveAutoDeploy is false)
                {
                    log.Information("Auto-deploy is disabled for project {ProjectId} on branch {Branch} in PushWebhookEvent {PushWebhookEventId}",
                                    projectId, branch, pushWebhookEvent.Id);
                    continue;
                }

                await mediator.Send(new DeployProjectCommand(
                    projectId,
                    CommitHash: pushWebhookEvent.Event.HeadCommit!.Id!,
                    CommitMessage: commitMessage,
                    CommitAuthor: pushWebhookEvent.Event.HeadCommit!.Author!.Name!,
                    CommitBranch: branch,
                    CommitEmail: pushWebhookEvent.Event.HeadCommit!.Author!.Email!,
                    OverrideAuthorization: true,
                    EnvironmentId: projectEnvironment.Id));
            }
        }
        catch (CouldNotFindProjectFromGitUrlException ex)
        {
            log.ForContext(nameof(ex), ex)
               .Information("Could not find project from Git URL for PushWebhookEvent {PushWebhookEventId}", pushWebhookEvent.Id);

            await MarkAsErroredAsync(pushWebhookEvent, ex.Message);
        }
        catch (Exception ex)
        {
            log.ForContext(nameof(ex), ex)
               .Error("Error processing PushWebhookEvent {PushWebhookEventId}", pushWebhookEvent.Id);

            await MarkAsErroredAsync(pushWebhookEvent, ex.Message);
        }
        finally
        {
            await MarkAsHandledAsync(pushWebhookEvent);
        }
    }

    private async Task MarkAsHandledAsync(
        PushWebhookEvent pushWebhookEvent)
    {
        pushWebhookEvent.HandledAt = DateTime.UtcNow;
        await pushWebhookEventRepository.UpdateAsync(pushWebhookEvent);
    }

    private async Task MarkAsErroredAsync(
        PushWebhookEvent pushWebhookEvent, string errorMessage)
    {
        pushWebhookEvent.ErrorMessage = errorMessage;
        pushWebhookEvent.ErroredAt = DateTime.UtcNow;
        await pushWebhookEventRepository.UpdateAsync(pushWebhookEvent);
    }
}