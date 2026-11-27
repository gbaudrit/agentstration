using System.Text.Json;
using Agentstration.Afc.Abstractions;
using Agentstration.Atc.Abstractions;

namespace Agentstration.Extensions.GitHub;

// A new session per native MCP invocation, with fresh backend authorization even for cached receipts.
public sealed class GitHubContractSession(GitHubServiceEnvelope configuration, GitHubConnection connections,
    GitHubIssueOperations issues, GitHubCodingOperations coding)
{
    private GitHubServiceOptions options = null!;
    private GitHubConnection.Session native = null!;
    private string subject = "";
    public AtcTargetRef Target => new(configuration.Inputs["providerId"], configuration.Inputs["targetId"], configuration.Inputs["bindingId"], configuration.Inputs["revision"]);
    public string Subject => subject;
    public GitHubServiceOptions Options => options;
    public async Task AuthorizeAsync(CancellationToken token)
    {
        options = configuration.Options.Deserialize<GitHubServiceOptions>(GitHubConnection.Json) ?? throw new GitHubProviderException("github_service_invalid");
        if (!GitHubConnection.RepositoryName(options.Repository) || options.ManagedLabelPrefix is not { Length: > 0 and <= 64 }
            || options.ManagedLabelPrefix.Any(char.IsControl)) throw new GitHubProviderException("github_service_invalid");
        native = connections.Open(options.ApiBaseUrl, configuration.Inputs["credential"], [new(options.Repository, Target.TargetId)]);
        var user = await native.SendAsync(HttpMethod.Get, "user", null, token);
        subject = GitHubConnection.Id(user);
        if (subject != configuration.Inputs["subjectId"] || Target.ProviderId != GitHubServiceContract.Authority(options.ApiBaseUrl))
            throw new GitHubProviderException("github_service_identity_changed");
        await native.RepositoryAsync(options.Repository, token);
    }
    public string Key(string operationKey) => GitHubServiceContract.Hash(JsonSerializer.Serialize(new
    { Target, subject, options, operationKey }));
    public Task<JsonElement> SendAsync(string path, CancellationToken token) => native.SendAsync(HttpMethod.Get, path, null, token);
    public async Task<GitHubTaskSnapshot> ReadAsync(string number, CancellationToken token) => await GitHubIssueOperations.ReadAsync(native, options.Repository, number, token);
    public Task<GitHubTaskResponse> IssueAsync(AtcMutationRequest request, GitHubTaskInput input, CancellationToken token) =>
        issues.InvokeAsync(request.Mode == AtcMutationMode.Recover ? "recover" : "execute", Key(request.OperationKey), request.IntentDigest[7..], input,
            options.ApiBaseUrl, configuration.Inputs["credential"], [new(options.Repository, Target.TargetId)], options.ManagedLabelPrefix, token);
    public Task<GitHubCodingResponse> PullAsync(AfcCreateRequest request, GitHubPullRequestInput input, CancellationToken token) =>
        coding.InvokeAsync("pull-request.create", request.Mode == AfcMutationMode.Recover ? "recover" : "execute", Key(request.OperationKey), request.IntentDigest[7..],
            JsonSerializer.SerializeToElement(input, GitHubConnection.Json), options.ApiBaseUrl, configuration.Inputs["credential"], [new(options.Repository, Target.TargetId)], token);
}
