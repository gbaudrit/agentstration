using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Secrets.Abstractions;

public enum SecretValueStatus { Configured, Missing, Unavailable, VaultUnavailable }

public sealed class SecretValue : IDisposable
{
    private byte[]? value;
    public SecretValue(ReadOnlySpan<byte> value) => this.value = value.ToArray();
    public ReadOnlyMemory<byte> AccessValue() => value ?? throw new ObjectDisposedException(nameof(SecretValue));
    public override string ToString() => "[REDACTED]";
    public void Dispose()
    {
        if (value is null) return;
        CryptographicOperations.ZeroMemory(value);
        value = null;
    }
}

public sealed class ResolvedSecret(ResourceAddress secret, ResourceAddress vault, SecretValue value) : IDisposable
{
    public ResourceAddress Secret { get; } = secret;
    public ResourceAddress Vault { get; } = vault;
    public SecretValue Value { get; } = value;
    public override string ToString() => "[REDACTED]";
    public void Dispose() => Value.Dispose();
}

public sealed record SecretResolutionContext(Guid TenantId, Guid WorkspaceId, ResourceAddress Consumer);
public sealed record SecretVaultContext(Guid TenantId, Guid WorkspaceId, ResourceAddress Vault, IReadOnlyDictionary<string, JsonElement> Options);

public interface ISecretResolver
{
    Task<ResolvedSecret?> ResolveAsync(ResourceAddress secret, SecretResolutionContext context, CancellationToken cancellationToken = default);
}

public interface ISecretVaultProvider
{
    string ProviderType { get; }
    Task<string> GetHealthAsync(SecretVaultContext context, CancellationToken cancellationToken = default) => Task.FromResult("available");
    Task<SecretValueStatus> GetStatusAsync(SecretVaultContext context, string key, CancellationToken cancellationToken = default);
    Task<SecretValue?> GetAsync(SecretVaultContext context, string key, CancellationToken cancellationToken = default);
    Task SetAsync(SecretVaultContext context, string key, SecretValue value, CancellationToken cancellationToken = default);
    Task DeleteAsync(SecretVaultContext context, string key, CancellationToken cancellationToken = default);
}

public interface IMasterKeyProvider
{
    ValueTask<byte[]> GetKeyAsync(CancellationToken cancellationToken = default);
}

public sealed record SecretVaultInitializationResult(bool Created, string KeyFilePath);

public interface ISecretVaultInitializer
{
    string ProviderType { get; }
    Task<SecretVaultInitializationResult> InitializeAsync(SecretVaultContext context, CancellationToken cancellationToken = default);
}

public interface IMasterKeyInitializer
{
    Task<SecretVaultInitializationResult> InitializeAsync(CancellationToken cancellationToken = default);
}

public class SecretResolutionException(string message) : Exception(message);
public sealed class SecretVaultUnavailableException(string providerType) : SecretResolutionException($"Secret vault provider '{providerType}' is unavailable.");
public sealed class SecretAccessDeniedException(ResourceAddress secret) : SecretResolutionException($"Secret '{secret}' is outside the execution context.");
