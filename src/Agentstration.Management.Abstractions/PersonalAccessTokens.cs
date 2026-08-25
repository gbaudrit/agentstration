namespace Agentstration.Management.Abstractions;

public sealed record PersonalAccessToken(
    Guid Id,
    Guid PrincipalId,
    Guid WorkspaceId,
    string Name,
    string TokenPrefix,
    IReadOnlyCollection<string> Permissions,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

public sealed record PersonalAccessTokenCredential(
    PersonalAccessToken Token,
    byte[] SecretHash);

public sealed record CreatedPersonalAccessToken(
    PersonalAccessToken Metadata,
    string Token);

public interface IPersonalAccessTokenStore
{
    Task<PersonalAccessTokenCredential?> GetCredentialAsync(Guid tokenId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PersonalAccessToken>> ListAsync(Guid principalId, CancellationToken cancellationToken);
    Task AddAsync(PersonalAccessTokenCredential credential, CancellationToken cancellationToken);
    Task<bool> RevokeAsync(Guid tokenId, Guid principalId, DateTimeOffset revokedAt, CancellationToken cancellationToken);
    Task<int> RevokeAllAsync(Guid principalId, DateTimeOffset revokedAt, CancellationToken cancellationToken);
    Task RecordUseAsync(Guid tokenId, DateTimeOffset usedAt, TimeSpan minimumInterval, CancellationToken cancellationToken);
}

public sealed record AuthorizationRestriction(
    Guid PersonalAccessTokenId,
    Guid WorkspaceId,
    IReadOnlySet<string> Permissions);
