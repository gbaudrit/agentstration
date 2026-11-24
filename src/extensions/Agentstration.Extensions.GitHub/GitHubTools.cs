using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Agentstration.Extensions.GitHub;

[McpServerToolType]
public sealed class GitHubTools(GitHubIssueOperations issues, GitHubCodingOperations coding)
{
    [McpServerTool(Name = "github_issue_operation", UseStructuredContent = true), Description("Bounded native GitHub issue operations. Bind credential, API origin, repositories and managed label prefix in a protected published relay.")]
    public Task<GitHubTaskResponse> IssueAsync(string mode, string operationKey, string intentDigest, GitHubTaskInput input,
        string apiBaseUrl, string credential, IReadOnlyList<AllowedGitHubRepository> allowedRepositories, string managedLabelPrefix, CancellationToken cancellationToken) =>
        issues.InvokeAsync(mode, operationKey, intentDigest, input, apiBaseUrl, credential, allowedRepositories, managedLabelPrefix, cancellationToken);

    [McpServerTool(Name = "github_coding_operation", UseStructuredContent = true), Description("Resolve pinned native GitHub repository identity and create, read or recover a pull request through a protected published relay.")]
    public Task<GitHubCodingResponse> CodingAsync(string operation, string mode, string operationKey, string intentDigest, JsonElement request,
        string apiBaseUrl, string credential, IReadOnlyList<AllowedGitHubRepository> allowedRepositories, CancellationToken cancellationToken) =>
        coding.InvokeAsync(operation, mode, operationKey, intentDigest, request, apiBaseUrl, credential, allowedRepositories, cancellationToken);
}
