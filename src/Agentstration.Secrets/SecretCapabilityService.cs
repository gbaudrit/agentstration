using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Agentstration.Secrets.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Secrets;

public sealed class SecretCapabilityService : ISecretCapabilityService, IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);
    private const int MaximumActiveCapabilities = 10_000;

    private readonly ISecretAccessAuthorizer authorizer;
    private readonly ISecretResolver resolver;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SecretCapabilityService> logger;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly object issuanceGate = new();
    private readonly ITimer cleanupTimer;

    public SecretCapabilityService(ISecretAccessAuthorizer authorizer, ISecretResolver resolver, TimeProvider timeProvider,
        ILogger<SecretCapabilityService>? logger = null)
    {
        this.authorizer = authorizer;
        this.resolver = resolver;
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<SecretCapabilityService>.Instance;
        cleanupTimer = timeProvider.CreateTimer(static state => ((SecretCapabilityService)state!).PruneExpired(),
            this, CleanupInterval, CleanupInterval);
    }

    public async Task<SecretCapabilityHandle> IssueAsync(
        SecretCapabilityContext context,
        SecretBinding binding,
        IReadOnlyCollection<string> declaredRequirements,
        CancellationToken lifetimeToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(declaredRequirements);
        cancellationToken.ThrowIfCancellationRequested();
        if (lifetimeToken.IsCancellationRequested)
            throw Failure("context_terminated", "The execution context has ended.");
        if (context.ExtensionRegistration.ScopeRef == default
            || context.Consumer.ScopeRef == default
            || string.IsNullOrWhiteSpace(context.ExtensionRegistration.Kind)
            || string.IsNullOrWhiteSpace(context.ExtensionRegistration.Name)
            || string.IsNullOrWhiteSpace(context.ExtensionId)
            || context.ExtensionId.Length > 128
            || string.IsNullOrWhiteSpace(context.Consumer.Kind)
            || string.IsNullOrWhiteSpace(context.Consumer.Name)
            || string.IsNullOrWhiteSpace(context.RequirementId)
            || context.RequirementId.Length > 64
            || string.IsNullOrWhiteSpace(context.ExecutionId)
            || context.ExecutionId.Length > 128)
            throw Failure("context_invalid", "The Secret capability context is invalid.");
        if (!declaredRequirements.Contains(context.RequirementId, StringComparer.Ordinal))
            throw Failure("requirement_undeclared", "The extension has not declared this Secret requirement.");
        if (!string.Equals(binding.RequirementId, context.RequirementId, StringComparison.Ordinal)
            || binding.Secret is null
            || !string.Equals(binding.Secret.Address.Kind, SecretResourceKinds.Secret, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(binding.Secret.Address.Name)
            || binding.Secret.ScopeRef is not { } secretScope
            || secretScope == default)
            throw Failure("binding_invalid", "The Secret requirement has no valid scoped binding.");

        var resolution = new SecretResolutionContext(context.Consumer.ScopeRef, context.Consumer.Address);
        try
        {
            var status = await authorizer.GetAuthorizedStatusAsync(binding.Secret, resolution, cancellationToken);
            if (status == SecretValueStatus.VaultUnavailable)
                throw Failure("vault_unavailable", "The bound Secret's Vault is unavailable.");
            if (status != SecretValueStatus.Configured)
                throw Failure("secret_unavailable", "The bound Secret is unavailable.");
        }
        catch (SecretAccessDeniedException)
        {
            throw Failure("access_denied", "Access to the bound Secret was denied.");
        }
        catch (SecretVaultUnavailableException)
        {
            throw Failure("vault_unavailable", "The bound Secret's Vault is unavailable.");
        }
        catch (VaultResourceNotFoundException)
        {
            throw Failure("vault_unavailable", "The bound Secret's Vault is unavailable.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (lifetimeToken.IsCancellationRequested)
            throw Failure("context_terminated", "The execution context has ended.");
        PruneExpired();
        Span<byte> random = stackalloc byte[32];
        string token;
        try
        {
            RandomNumberGenerator.Fill(random);
            token = Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
        }
        var entry = new Entry(context, binding.Secret, timeProvider.GetUtcNow() + Lifetime, lifetimeToken);
        lock (issuanceGate)
        {
            if (entries.Count >= MaximumActiveCapabilities)
                throw Failure("capability_capacity", "The Secret capability limit has been reached.");
            if (!entries.TryAdd(Hash(token), entry))
                throw Failure("capability_collision", "A Secret capability could not be issued.");
        }
        if (lifetimeToken.IsCancellationRequested)
        {
            entries.TryRemove(Hash(token), out _);
            throw Failure("context_terminated", "The execution context has ended.");
        }
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("AEP Secret capability issued for extension {ExtensionId}, consumer {Consumer}, requirement {RequirementId}, execution {ExecutionId}",
                    context.ExtensionId, context.Consumer, context.RequirementId, context.ExecutionId);
        }
        catch
        {
            entries.TryRemove(Hash(token), out _);
            throw;
        }
        return new SecretCapabilityHandle(token);
    }

    public async Task<ResolvedSecret> RedeemAsync(
        string token,
        SecretCapabilityContext context,
        CancellationToken cancellationToken = default)
        => await RedeemCoreAsync(token, candidate => candidate == context, cancellationToken);

    public async Task<ResolvedSecret> RedeemAsync(
        string token,
        string extensionId,
        string requirementId,
        string executionId,
        CancellationToken cancellationToken = default)
        => await RedeemCoreAsync(token,
            candidate => string.Equals(candidate.ExtensionId, extensionId, StringComparison.Ordinal)
                && string.Equals(candidate.RequirementId, requirementId, StringComparison.Ordinal)
                && string.Equals(candidate.ExecutionId, executionId, StringComparison.Ordinal),
            cancellationToken);

    private async Task<ResolvedSecret> RedeemCoreAsync(
        string token,
        Func<SecretCapabilityContext, bool> matches,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256 || !entries.TryRemove(Hash(token), out var entry))
            throw Failure("capability_invalid", "The Secret capability is invalid.");
        if (entry.ExpiresAt <= timeProvider.GetUtcNow())
            throw Failure("capability_expired", "The Secret capability has expired.");
        if (entry.LifetimeToken.IsCancellationRequested)
            throw Failure("context_terminated", "The execution context has ended.");
        if (!matches(entry.Context))
            throw Failure("context_mismatch", "The Secret capability does not belong to this context.");

        try
        {
            var resolved = await resolver.ResolveAsync(entry.Secret,
                new SecretResolutionContext(entry.Context.Consumer.ScopeRef, entry.Context.Consumer.Address),
                cancellationToken)
                ?? throw Failure("secret_unavailable", "The bound Secret is unavailable.");
            try
            {
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("AEP Secret capability redeemed for extension {ExtensionId}, consumer {Consumer}, requirement {RequirementId}, execution {ExecutionId}",
                        entry.Context.ExtensionId, entry.Context.Consumer, entry.Context.RequirementId, entry.Context.ExecutionId);
            }
            catch
            {
                resolved.Dispose();
                throw;
            }
            return resolved;
        }
        catch (SecretAccessDeniedException)
        {
            throw Failure("access_denied", "Access to the bound Secret was denied.");
        }
        catch (SecretVaultUnavailableException)
        {
            throw Failure("vault_unavailable", "The bound Secret's Vault is unavailable.");
        }
        catch (VaultResourceNotFoundException)
        {
            throw Failure("vault_unavailable", "The bound Secret's Vault is unavailable.");
        }
    }

    public void Revoke(SecretCapabilityHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        entries.TryRemove(Hash(handle.RevealForTransport()), out _);
    }

    public void RevokeExecution(SecretCapabilityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var entry in entries)
            if (entry.Value.Context.ExtensionRegistration == context.ExtensionRegistration
                && string.Equals(entry.Value.Context.ExtensionId, context.ExtensionId, StringComparison.Ordinal)
                && entry.Value.Context.Consumer == context.Consumer
                && string.Equals(entry.Value.Context.ExecutionId, context.ExecutionId, StringComparison.Ordinal))
                entries.TryRemove(entry.Key, out _);
    }

    public int PruneExpired()
    {
        var now = timeProvider.GetUtcNow();
        var removed = 0;
        foreach (var entry in entries)
            if ((entry.Value.ExpiresAt <= now || entry.Value.LifetimeToken.IsCancellationRequested)
                && entries.TryRemove(entry.Key, out _))
                removed++;
        return removed;
    }

    public void Dispose()
    {
        cleanupTimer.Dispose();
        entries.Clear();
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static SecretCapabilityException Failure(string code, string message) => new(code, message);

    private sealed record Entry(
        SecretCapabilityContext Context,
        SecretReference Secret,
        DateTimeOffset ExpiresAt,
        CancellationToken LifetimeToken);
}
