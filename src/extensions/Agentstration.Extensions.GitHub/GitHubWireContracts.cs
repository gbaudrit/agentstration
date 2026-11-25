using System.Text.Json;

namespace Agentstration.Extensions.GitHub;

// Wire DTOs deliberately do not reference the host's Work, Coding or application assemblies.
public sealed record AllowedGitHubRepository(string Repository, string NativeId);
public sealed record GitHubTaskInput(string Operation, string Repository, string? TaskId = null, string? Title = null,
    string? Body = null, string? State = null, IReadOnlyList<string>? Labels = null, IReadOnlyList<GitHubRelation>? Relations = null, string? ExpectedRevision = null);
public sealed record GitHubRelation(string Kind, string Repository, string TaskId);
public sealed record GitHubTaskSnapshot(string Repository, string TaskId, string Revision, string Title, string? Body, string State,
    Uri Url, IReadOnlyList<string> Labels, IReadOnlyList<GitHubRelation> Relations)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CompletionReason { get; init; }
}
public sealed record GitHubTaskResponse(string OperationKey, string IntentDigest, string Status, GitHubTaskSnapshot? Task = null, string? ErrorCode = null);
public sealed record GitHubRepositoryIdentity(string Provider, string Repository, string NativeId, Uri CloneUrl, Uri WebUrl,
    string DefaultBranch, string Revision, IReadOnlyList<string> Capabilities);
public sealed record GitHubPullRequestInput(string Repository, string? Number = null, string? Title = null, string? Body = null,
    string? Head = null, string? Base = null, string? ExpectedHeadRevision = null, bool Draft = true)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpectedBaseRevision { get; init; }
}
public sealed record GitHubPullRequestSnapshot(string Provider, string Repository, string NativeId, string Number, Uri Url,
    string State, bool Draft, bool Merged, string Head, string HeadRevision, string Base, string BaseRevision, string Revision);
public sealed record GitHubCodingResponse(string OperationKey, string IntentDigest, string Status,
    IReadOnlyList<GitHubRepositoryIdentity>? Repositories = null, GitHubPullRequestSnapshot? PullRequest = null, string? ErrorCode = null);
internal sealed record GitHubJournal(string IntentDigest, string Authority, string Operation, JsonElement Input, string Phase,
    JsonElement? Result = null);

public sealed class GitHubProviderException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
