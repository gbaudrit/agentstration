using System.Text.Json;

namespace Agentstration.Extensions.GitHub;

public sealed class GitHubIssueOperations(GitHubConnection connections, GitHubOperationJournal journal)
{
    public async Task<GitHubTaskResponse> InvokeAsync(string mode, string operationKey, string intentDigest, GitHubTaskInput input,
        string apiBaseUrl, string credential, IReadOnlyList<AllowedGitHubRepository> allowedRepositories, string managedLabelPrefix, CancellationToken token)
    {
        GitHubTaskResponse Result(string status, string? code = null, GitHubTaskSnapshot? task = null) => new(operationKey, intentDigest, status, task, code);
        if (!GitHubOperationJournal.Coordinate(operationKey, intentDigest, mode)) return Result("Rejected", "github_operation_invalid");
        var admitted = false;
        try
        {
            if (input is null || input.Operation is not ("Read" or "Update" or "Publish") || !GitHubConnection.RepositoryName(input.Repository)
                || managedLabelPrefix is not { Length: >= 1 and <= 64 } || managedLabelPrefix.Any(char.IsControl)
                || input.Operation != "Publish" && !GitHubConnection.Number(input.TaskId)
                || input.Operation == "Publish" && (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 512 || input.TaskId is not null)
                || input.Body?.Length > 32_600 || input.Title?.Length > 512 || input.Labels?.Count > 32 || input.Relations?.Count > 100
                || input.Labels?.Any(label => label is not { Length: >= 1 and <= 128 } || label.Any(char.IsControl)
                    || !label.StartsWith(managedLabelPrefix, StringComparison.Ordinal)) == true
                || input.Relations?.Any(relation => relation is null || relation.Kind is not ("parent" or "blocked-by")
                    || !GitHubConnection.RepositoryName(relation.Repository) || !GitHubConnection.Number(relation.TaskId)) == true
                || input.Operation == "Update" && string.IsNullOrWhiteSpace(input.ExpectedRevision)) return Result("Rejected", "github_issue_input_invalid");
            if (input.Operation == "Update" && (input.Title is not null || input.Body is not null || input.State is not null))
                return Result("Rejected", "github_conditional_update_unsupported");
            if (input.Operation == "Read" && (input.Title is not null || input.Body is not null || input.State is not null || input.Labels is not null || input.Relations is not null)
                || input.Operation == "Publish" && input.State is not null) return Result("Rejected", "github_issue_input_invalid");
            var session = connections.Open(apiBaseUrl, credential, allowedRepositories);
            await session.RepositoryAsync(input.Repository, token);
            if (input.Operation == "Read") return Result("Succeeded", task: await ReadAsync(session, input.Repository, input.TaskId!, token));
            var marker = "<!-- agentstration-operation:" + operationKey + " -->";
            admitted = true;
            return await journal.RunAsync(operationKey, intentDigest, session.Authority(input.Repository), "issue." + input.Operation,
                JsonSerializer.SerializeToElement(input), mode, async (recovering, cancellation) =>
                {
                    string? number = input.TaskId;
                    if (input.Operation == "Publish")
                    {
                        if (recovering)
                        {
                            var issues = await session.ListAsync($"repos/{input.Repository}/issues?state=all&sort=created&direction=desc", cancellation);
                            var matches = issues.Where(issue => !issue.TryGetProperty("pull_request", out _) && issue.TryGetProperty("body", out var body)
                                && body.ValueKind == JsonValueKind.String && body.GetString()!.Contains(marker, StringComparison.Ordinal)).ToArray();
                            if (matches.Length != 1) return Result("Uncertain", "github_create_outcome_uncertain");
                            number = GitHubConnection.Number(matches[0]);
                        }
                        else
                        {
                            var created = await session.SendAsync(HttpMethod.Post, $"repos/{input.Repository}/issues",
                                new { title = input.Title, body = (input.Body ?? "") + "\n\n" + marker, labels = input.Labels ?? [] }, cancellation);
                            number = GitHubConnection.Number(created);
                        }
                    }
                    var before = await ReadAsync(session, input.Repository, number!, cancellation);
                    if (input.Operation == "Update" && !recovering && before.Revision != input.ExpectedRevision) return Result("Rejected", "github_issue_revision_changed");
                    if (recovering)
                        return Satisfied(before, input) ? Result("Succeeded", task: before) : Result("Uncertain", "github_update_outcome_uncertain", before);
                    if (input.Operation == "Update" && input.Labels is { Count: > 0 })
                        await session.SendAsync(HttpMethod.Post, $"repos/{input.Repository}/issues/{number}/labels", new { labels = input.Labels }, cancellation);
                    foreach (var relation in input.Relations ?? [])
                    {
                        await session.RepositoryAsync(relation.Repository, cancellation);
                        var target = await session.SendAsync(HttpMethod.Get, $"repos/{relation.Repository}/issues/{relation.TaskId}", null, cancellation);
                        if (target.TryGetProperty("pull_request", out _)) throw new GitHubProviderException("github_issue_required");
                        if (relation.Kind == "parent")
                        {
                            if (relation.Repository.Split('/')[0] != input.Repository.Split('/')[0]) throw new GitHubProviderException("github_parent_owner_mismatch");
                            var own = await session.SendAsync(HttpMethod.Get, $"repos/{input.Repository}/issues/{number}", null, cancellation);
                            await session.SendAsync(HttpMethod.Post, $"repos/{relation.Repository}/issues/{relation.TaskId}/sub_issues",
                                new { sub_issue_id = own.GetProperty("id").GetInt64(), replace_parent = false }, cancellation);
                        }
                        else await session.SendAsync(HttpMethod.Post, $"repos/{input.Repository}/issues/{number}/dependencies/blocked_by",
                            new { issue_id = target.GetProperty("id").GetInt64() }, cancellation);
                    }
                    var after = await ReadAsync(session, input.Repository, number!, cancellation);
                    return Satisfied(after, input) ? Result("Succeeded", task: after) : Result("Uncertain", "github_update_outcome_uncertain", after);
                }, async (receipt, cancellation) =>
                {
                    if (receipt.Task is { } task) await session.SendAsync(HttpMethod.Get, $"repos/{input.Repository}/issues/{task.TaskId}", null, cancellation);
                }, status => Result(status), token);
        }
        catch (GitHubProviderException error) { return Result(!admitted && error.Code is "github_credentials_denied" or "github_repository_denied" or "github_connection_invalid" ? "Rejected" : "Uncertain", error.Code); }
        catch (HttpRequestException) { return Result("Uncertain", "github_transport_uncertain"); }
        catch (JsonException) { return Result("Uncertain", "github_response_invalid"); }
        catch (InvalidOperationException) { return Result("Uncertain", "github_response_invalid"); }
        catch (KeyNotFoundException) { return Result("Uncertain", "github_response_invalid"); }
    }

    private static bool Satisfied(GitHubTaskSnapshot task, GitHubTaskInput input) => (input.Labels ?? []).All(task.Labels.Contains)
        && (input.Relations ?? []).All(task.Relations.Contains);

    internal static async Task<GitHubTaskSnapshot> ReadAsync(GitHubConnection.Session session, string repository, string number, CancellationToken token)
    {
        var issue = await session.SendAsync(HttpMethod.Get, $"repos/{repository}/issues/{number}", null, token);
        if (issue.TryGetProperty("pull_request", out _)) throw new GitHubProviderException("github_issue_required");
        var labels = issue.GetProperty("labels").EnumerateArray().Select(label => GitHubConnection.Text(label, "name")).ToArray();
        var relations = new List<GitHubRelation>();
        if (!issue.TryGetProperty("sub_issues_summary", out var subSummary) || !issue.TryGetProperty("issue_dependencies_summary", out var dependencySummary))
            throw new GitHubProviderException("github_relationship_capability_missing");
        var children = await session.ListAsync($"repos/{repository}/issues/{number}/sub_issues", token);
        var dependencies = await session.ListAsync($"repos/{repository}/issues/{number}/dependencies/blocked_by", token);
        if (children.Count != subSummary.GetProperty("total").GetInt32() || dependencies.Count != dependencySummary.GetProperty("blocked_by").GetInt32())
            throw new GitHubProviderException("github_observation_incomplete");
        foreach (var child in children) relations.Add(Relation("child", child));
        foreach (var dependency in dependencies) relations.Add(Relation("blocked-by", dependency));
        var parent = await session.SendAsync(HttpMethod.Get, $"repos/{repository}/issues/{number}/parent", null, token, true);
        if (parent.ValueKind != JsonValueKind.Undefined) relations.Add(Relation("parent", parent));
        if (labels.Length > 32 || relations.Count > 100) throw new GitHubProviderException("github_observation_incomplete");
        // Revision is an observation token, not a claim of GitHub's unsupported unsafe-method CAS.
        var revision = GitHubConnection.Digest(JsonSerializer.SerializeToElement(new { issue, Relations = relations }));
        return new(repository, number, revision, GitHubConnection.Text(issue, "title"), issue.GetProperty("body").GetString(),
            GitHubConnection.Text(issue, "state"), new(GitHubConnection.Text(issue, "html_url")), labels, relations);
    }

    private static GitHubRelation Relation(string kind, JsonElement issue)
    {
        var url = new Uri(GitHubConnection.Text(issue, "repository_url"));
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || segments[^3] != "repos") throw new GitHubProviderException("github_response_invalid");
        return new(kind, segments[^2] + "/" + segments[^1], GitHubConnection.Number(issue));
    }
}
