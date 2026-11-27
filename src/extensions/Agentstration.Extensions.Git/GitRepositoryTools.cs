using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Agentstration.Extensions.Git;

[McpServerToolType]
public sealed class GitRepositoryTools(GitRepositoryOperations operations, IHttpContextAccessor http)
{
    [McpServerTool(Name = "git_repository_operation", UseStructuredContent = true),
     Description("Bounded generic Git reads, diffs and compare-and-swap publication. Use an authorized Git Connected Service or bind repository, authentication, credential and ref/path permissions in a protected published relay.")]
    public Task<GitRepositoryResponse> InvokeAsync(string operation, string mode, string operationKey, string intentDigest, GitRepositoryInput request,
        string repository = "", string authentication = "", string username = "", string credential = "", IReadOnlyList<string>? allowedRefPrefixes = null,
        IReadOnlyList<string>? allowedPathPrefixes = null, CancellationToken cancellationToken = default)
    {
        if (http.HttpContext?.Request is { } transport && transport.Headers.ContainsKey(GitServiceContract.Header))
        {
            var service = GitServiceContract.Read(transport);
            if (repository.Length != 0 || authentication.Length != 0 || username.Length != 0 || credential.Length != 0
                || allowedRefPrefixes is not null || allowedPathPrefixes is not null) throw new GitOperationException("git_service_arguments_conflict");
            var options = service.Options;
            if (options.RefName is { } reference && reference != request.RefName) throw new GitOperationException("git_service_reference_mismatch");
            return operations.InvokeAsync(operation, mode, operationKey, intentDigest, request, options.Repository, options.Authentication,
                options.Username, service.Credential, options.AllowedRefPrefixes, options.AllowedPathPrefixes, cancellationToken);
        }
        return operations.InvokeAsync(operation, mode, operationKey, intentDigest, request, repository, authentication, username, credential,
            allowedRefPrefixes ?? [], allowedPathPrefixes ?? [], cancellationToken);
    }
}
