using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Contracts;

public sealed record CreateVaultRequest(string Name, VaultProperties Properties, ResourceScopeRef? ScopeRef = null);
public sealed record PutVaultRequest(VaultProperties Properties);
public sealed record CreateSecretRequest(string Name, SecretProperties Properties, ResourceScopeRef? ScopeRef = null);
public sealed record PutSecretRequest(SecretProperties Properties);
public sealed record SetSecretValueRequest(string Value);
public sealed record SecretResponse(SecretResource Resource, string ValueStatus, bool ValueConfigured);
public sealed record VaultResponse(VaultResource Resource, string Status);
public sealed record VaultInitializationResponse(string Status, string KeyFilePath);
public sealed record SecretUsageResponse(string ResourceType, string Name, string DisplayName, string Url);
public sealed record SecretUsagesResponse(IReadOnlyList<SecretUsageResponse> Value, int Count);
public sealed record ResourceScopeTargetResponse(ResourceScopeRef ScopeRef, ResourceScopeKind Kind, string DisplayName, bool CanWrite);
