using System.Text.Json;

namespace Agentstration.Extensions.GitHub;

public sealed class GitHubCodingOperations(GitHubConnection connections, GitHubOperationJournal journal)
{
    public async Task<GitHubCodingResponse> InvokeAsync(string operation, string mode, string operationKey, string intentDigest, JsonElement request,
        string apiBaseUrl, string credential, IReadOnlyList<AllowedGitHubRepository> allowedRepositories, CancellationToken token)
    {
        GitHubCodingResponse Result(string status, string? code = null, GitHubPullRequestSnapshot? pr = null) => new(operationKey, intentDigest, status, PullRequest: pr, ErrorCode: code);
        if (!GitHubOperationJournal.Coordinate(operationKey, intentDigest, mode)) return Result("Rejected", "github_operation_invalid");
        var admitted = false;
        try
        {
            var session = connections.Open(apiBaseUrl, credential, allowedRepositories);
            if (operation == "repository.resolve")
            {
                var input = request.Deserialize<RepositoryRequest>(GitHubConnection.Json);
                if (input?.Repositories is not { Count: >= 1 and <= 16 } names || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
                    return Result("Rejected", "github_repository_selection_invalid");
                var results = new List<GitHubRepositoryIdentity>();
                foreach (var name in names)
                {
                    var repository = await session.RepositoryAsync(name, token);
                    var branch = GitHubConnection.Text(repository, "default_branch");
                    var commit = await session.SendAsync(HttpMethod.Get, $"repos/{name}/commits/{Uri.EscapeDataString(branch)}", null, token);
                    results.Add(new("github", name, GitHubConnection.Id(repository), new(GitHubConnection.Text(repository, "clone_url")),
                        new(GitHubConnection.Text(repository, "html_url")), branch, GitHubConnection.Text(commit, "sha"),
                        ["issue.read", "issue.publish", "issue.managed-label-add", "issue.native-parent-add", "issue.native-blocked-by-add",
                            "pull-request.create", "pull-request.read", "operation.recover"]));
                }
                return new(operationKey, intentDigest, "Succeeded", results);
            }
            if (operation is not ("pull-request.create" or "pull-request.read")) return Result("Rejected", "github_operation_unsupported");
            var inputPr = request.Deserialize<GitHubPullRequestInput>(GitHubConnection.Json);
            if (inputPr is null || !GitHubConnection.RepositoryName(inputPr.Repository) || inputPr.Body?.Length > 32_768 || inputPr.Title?.Length > 512
                || inputPr.ExpectedBaseRevision is not null && (inputPr.ExpectedBaseRevision.Length is not (40 or 64) || !inputPr.ExpectedBaseRevision.All(char.IsAsciiHexDigit))
                || operation == "pull-request.read" && !GitHubConnection.Number(inputPr.Number)
                || operation == "pull-request.create" && (string.IsNullOrWhiteSpace(inputPr.Title) || !Branch(inputPr.Head) || !Branch(inputPr.Base)
                    || inputPr.ExpectedHeadRevision is not { Length: 40 or 64 } || !inputPr.ExpectedHeadRevision.All(char.IsAsciiHexDigit) || inputPr.Number is not null))
                return Result("Rejected", "github_pull_request_invalid");
            await session.RepositoryAsync(inputPr.Repository, token);
            if (operation == "pull-request.read") return Result("Succeeded", pr: Snapshot(inputPr.Repository,
                await session.SendAsync(HttpMethod.Get, $"repos/{inputPr.Repository}/pulls/{inputPr.Number}", null, token)));
            var marker = "<!-- agentstration-operation:" + operationKey + " -->";
            admitted = true;
            return await journal.RunAsync(operationKey, intentDigest, session.Authority(inputPr.Repository), operation,
                JsonSerializer.SerializeToElement(inputPr), mode, async (recovering, cancellation) =>
                {
                    JsonElement native;
                    if (recovering)
                    {
                        var pulls = await session.ListAsync($"repos/{inputPr.Repository}/pulls?state=all&sort=created&direction=desc", cancellation);
                        var matches = pulls.Where(pr => pr.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
                            && body.GetString()!.Contains(marker, StringComparison.Ordinal)).ToArray();
                        if (matches.Length != 1) return Result("Uncertain", "github_create_outcome_uncertain");
                        native = await session.SendAsync(HttpMethod.Get, $"repos/{inputPr.Repository}/pulls/{GitHubConnection.Number(matches[0])}", null, cancellation);
                    }
                    else
                    {
                        var head = await session.SendAsync(HttpMethod.Get, $"repos/{inputPr.Repository}/commits/{Uri.EscapeDataString(inputPr.Head!)}", null, cancellation);
                        if (GitHubConnection.Text(head, "sha") != inputPr.ExpectedHeadRevision) return Result("Rejected", "github_head_revision_changed");
                        if (inputPr.ExpectedBaseRevision is not null)
                        {
                            var baseline = await session.SendAsync(HttpMethod.Get, $"repos/{inputPr.Repository}/commits/{Uri.EscapeDataString(inputPr.Base!)}", null, cancellation);
                            if (GitHubConnection.Text(baseline, "sha") != inputPr.ExpectedBaseRevision) return Result("Rejected", "github_base_revision_changed");
                        }
                        native = await session.SendAsync(HttpMethod.Post, $"repos/{inputPr.Repository}/pulls",
                            new { title = inputPr.Title, body = (inputPr.Body ?? "") + "\n\n" + marker, head = inputPr.Head, @base = inputPr.Base, draft = inputPr.Draft }, cancellation);
                    }
                    var snapshot = Snapshot(inputPr.Repository, native);
                    return snapshot.Head == inputPr.Head && snapshot.Base == inputPr.Base && snapshot.HeadRevision == inputPr.ExpectedHeadRevision
                        && (inputPr.ExpectedBaseRevision is null || snapshot.BaseRevision == inputPr.ExpectedBaseRevision)
                        ? Result("Succeeded", pr: snapshot) : Result("Uncertain", inputPr.ExpectedBaseRevision is not null && snapshot.BaseRevision != inputPr.ExpectedBaseRevision
                            ? "github_pull_request_base_changed" : "github_pull_request_head_changed", snapshot);
                }, async (receipt, cancellation) =>
                {
                    if (receipt.PullRequest is { } pr) await session.SendAsync(HttpMethod.Get, $"repos/{inputPr.Repository}/pulls/{pr.Number}", null, cancellation);
                }, status => Result(status), token);
        }
        catch (GitHubProviderException error)
        {
            return Result(!admitted && error.Code is "github_credentials_denied" or "github_repository_denied" or "github_connection_invalid"
            ? "Rejected" : "Uncertain", error.Code);
        }
        catch (HttpRequestException) { return Result("Uncertain", "github_transport_uncertain"); }
        catch (JsonException) { return Result(admitted ? "Uncertain" : "Rejected", "github_contract_invalid"); }
        catch (InvalidOperationException) { return Result("Uncertain", "github_response_invalid"); }
        catch (KeyNotFoundException) { return Result("Uncertain", "github_response_invalid"); }
    }

    private static GitHubPullRequestSnapshot Snapshot(string repository, JsonElement pr)
    {
        var head = pr.GetProperty("head"); var target = pr.GetProperty("base");
        return new("github", repository, GitHubConnection.Id(pr), GitHubConnection.Number(pr), new(GitHubConnection.Text(pr, "html_url")),
            GitHubConnection.Text(pr, "state"), pr.GetProperty("draft").GetBoolean(), pr.TryGetProperty("merged", out var merged) && merged.GetBoolean(),
            GitHubConnection.Text(head, "ref"), GitHubConnection.Text(head, "sha"), GitHubConnection.Text(target, "ref"), GitHubConnection.Text(target, "sha"), GitHubConnection.Text(pr, "updated_at"));
    }
    private static bool Branch(string? value) => value is { Length: >= 1 and <= 256 } && !value.StartsWith('-')
        && value.Split('/').All(GitHubConnection.Segment);
    private sealed record RepositoryRequest(IReadOnlyList<string> Repositories);
}
