using Api.Abstractions;
using Api.Data.Repositories;
using Database.Repositories;
using Github.Abstractions;
using Github.Data;
using Github.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Github.Repositories;

public interface IPushWebhookEventRepository : IGenericRepository<PushWebhookEvent, PushWebhookEventId, GithubDbContext>
{
    /// <summary>
    /// Returns the ids of every project whose repository matches <paramref name="gitUrl"/>.
    /// A single repo can back multiple projects (e.g. several services in one monorepo),
    /// so callers must handle zero, one, or many matches.
    /// </summary>
    Task<IReadOnlyList<ProjectId>> GetProjectIdsFromGitUrlAsync(Uri gitUrl);
}

public sealed class PushWebhookEventRepository(
    GithubDbContext context,
    IServiceScopeFactory serviceScopeFactory) : GenericRepository<PushWebhookEvent, PushWebhookEventId, GithubDbContext>(context), IPushWebhookEventRepository
{
    public async Task<IReadOnlyList<ProjectId>> GetProjectIdsFromGitUrlAsync(Uri gitUrl)
    {
        // Create a new scope to properly isolate the ApiDbContext from GithubDbContext
        // This prevents memory leaks when used in long-running background jobs
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var projectRepository = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        // GitHub webhooks always send CloneUrl with .git suffix (e.g. https://github.com/owner/repo.git)
        // but the stored RepoUri may or may not have .git depending on how it was set.
        // Normalize by checking both variants.
        var urlStr = gitUrl.ToString().TrimEnd('/');
        var withoutGit = urlStr.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? new Uri(urlStr[..^4])
            : gitUrl;
        var withGit = urlStr.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? gitUrl
            : new Uri(urlStr + ".git");

        var projects = await projectRepository.GetFilteredAsync(x => x.RepoUri == withoutGit || x.RepoUri == withGit);

        return projects.Select(p => p.Id).ToList();
    }
}
