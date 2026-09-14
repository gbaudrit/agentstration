using Agentstration.Security.Contracts;
using Microsoft.Extensions.Options;

namespace Agentstration.Identity.Api.Security;

public sealed class BffWorkloadTrustAuditService(
    IOptionsMonitor<BffWorkloadTrustOptions> options,
    ISecurityAuditWriter audit,
    ILogger<BffWorkloadTrustAuditService> logger) : IHostedService, IDisposable
{
    private readonly object sync = new();
    private IDisposable? registration;
    private IReadOnlyDictionary<string, bool> previous = new Dictionary<string, bool>();
    private Task pending = Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        previous = Snapshot(options.CurrentValue);
        await AuditInitialAsync(options.CurrentValue, cancellationToken);
        registration = options.OnChange((value, _) => Queue(value));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task current;
        lock (sync) current = pending;
        await current.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task AuditInitialAsync(BffWorkloadTrustOptions options, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return;
        foreach (var group in options.Credentials.GroupBy(value => value.WorkloadId, StringComparer.Ordinal))
        {
            var ordered = group.ToArray();
            if (ordered.Length > 0)
                await WriteAsync(SecurityAuditActions.BffWorkloadEnrolled, ordered[0], cancellationToken);
            foreach (var credential in ordered.Skip(1).Where(value => !value.Revoked))
                await WriteAsync(SecurityAuditActions.BffWorkloadCredentialRotated, credential, cancellationToken);
            foreach (var credential in ordered.Where(value => value.Revoked))
                await WriteAsync(SecurityAuditActions.BffWorkloadCredentialRevoked, credential, cancellationToken);
        }
    }

    private async Task AuditChangesAsync(BffWorkloadTrustOptions current)
    {
        var snapshot = Snapshot(current);
        foreach (var credential in current.Credentials)
        {
            var key = Key(credential);
            if (!previous.TryGetValue(key, out var wasRevoked))
                await WriteAsync(SecurityAuditActions.BffWorkloadCredentialRotated, credential, CancellationToken.None);
            else if (!wasRevoked && credential.Revoked)
                await WriteAsync(SecurityAuditActions.BffWorkloadCredentialRevoked, credential, CancellationToken.None);
        }
        previous = snapshot;
    }

    private void Queue(BffWorkloadTrustOptions current)
    {
        lock (sync) pending = ObserveAsync(pending, current);
    }

    private async Task ObserveAsync(Task preceding, BffWorkloadTrustOptions current)
    {
        await preceding.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        try { await AuditChangesAsync(current); }
        catch (Exception) { logger.LogError("BFF workload trust configuration changes could not be audited."); }
    }

    private Task WriteAsync(string action, BffWorkloadCredentialOptions credential, CancellationToken cancellationToken) =>
        audit.WriteAsync(new(
            action,
            ReasonCode: BffWorkloadAuthenticationHandler.CredentialReference(credential.WorkloadId, credential.CredentialId)), cancellationToken);

    private static IReadOnlyDictionary<string, bool> Snapshot(BffWorkloadTrustOptions options) =>
        options.Credentials.ToDictionary(Key, credential => credential.Revoked, StringComparer.Ordinal);

    private static string Key(BffWorkloadCredentialOptions credential) => $"{credential.WorkloadId}\n{credential.CredentialId}";

    public void Dispose() => registration?.Dispose();
}
