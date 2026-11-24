using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Work;

namespace Agentstration.Coding.Contracts;

public static class CodingProviderProtocol
{
    public const string MetadataKey = "agentstration.coding.repository";
    public const string Version = "1.0";
    public const string ResolveRepositories = "repository.resolve";
    public const string CreatePullRequest = "pull-request.create";
    public const string ReadPullRequest = "pull-request.read";
}

public sealed record CodingProviderCapabilities(string Version, string Provider, IReadOnlyList<string> Operations);
public sealed record CodingRepositoryIdentity(string Provider, string Repository, string NativeId, Uri CloneUrl, Uri WebUrl,
    string DefaultBranch, string Revision, IReadOnlyList<string> Capabilities);
public sealed record CodingRepositorySelection(Guid ProjectId, long ProjectVersion, Guid TaskId, long TaskVersion,
    WorkProjectResourceReference Connection, IReadOnlyList<CodingRepositoryIdentity> Repositories, string Digest, DateTimeOffset ObservedAt);
public sealed record ResolveCodingRepositoriesRequest(Guid TaskId, IReadOnlyList<string> Repositories, long? ExpectedTaskVersion = null);

public sealed record CodingPullRequestInput(string Repository, string? Number = null, string? Title = null, string? Body = null,
    string? Head = null, string? Base = null, string? ExpectedHeadRevision = null, bool Draft = true);
public sealed record CodingPullRequestSnapshot(string Provider, string Repository, string NativeId, string Number, Uri Url,
    string State, bool Draft, bool Merged, string Head, string HeadRevision, string Base, string BaseRevision, string Revision);
public sealed record InvokeCodingPullRequestRequest(Guid Id, Guid TaskId, long ExpectedTaskVersion, string Operation,
    CodingPullRequestInput Input, WorkProjectResourceReference Connection, long ExpectedProjectVersion, bool Recover = false);
[JsonConverter(typeof(JsonStringEnumConverter<CodingProviderStatus>))]
public enum CodingProviderStatus { Succeeded, Rejected, Uncertain, Missing }
public sealed record CodingProviderRequest(string Operation, string Mode, string OperationKey, string IntentDigest, JsonElement Request);
public sealed record CodingProviderResponse(string OperationKey, string IntentDigest, CodingProviderStatus Status,
    IReadOnlyList<CodingRepositoryIdentity>? Repositories = null, CodingPullRequestSnapshot? PullRequest = null, string? ErrorCode = null);
public sealed record CodingPullRequestResponse(Guid Id, Guid ProjectId, Guid TaskId, long TaskVersion, WorkProjectResourceReference Connection,
    CodingProviderResponse Result);
