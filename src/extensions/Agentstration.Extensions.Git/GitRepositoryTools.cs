using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Agentstration.Extensions.Git;

[McpServerToolType]
public sealed class GitRepositoryTools(GitRepositoryOperations operations)
{
    [McpServerTool(Name = "git_repository_operation", UseStructuredContent = true),
     Description("Bounded generic Git reads, diffs and compare-and-swap publication. Bind repository, authentication, credential and ref/path permissions in a protected published relay.")]
    public Task<GitRepositoryResponse> InvokeAsync(string operation, string mode, string operationKey, string intentDigest, GitRepositoryInput request,
        string repository, string authentication, string username, string credential, IReadOnlyList<string> allowedRefPrefixes,
        IReadOnlyList<string> allowedPathPrefixes, CancellationToken cancellationToken) =>
        operations.InvokeAsync(operation, mode, operationKey, intentDigest, request, repository, authentication, username, credential,
            allowedRefPrefixes, allowedPathPrefixes, cancellationToken);
}
