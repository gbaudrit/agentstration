using System.Runtime.CompilerServices;
using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;

namespace Agentstration.Tools.Mcp;

public sealed partial class AepMcpTransport
{
    private readonly AsyncLocal<DiscoveryOperation?> discoveryOperation = new();

    /// <summary>Shares descriptor discovery for one bounded operation, never across requests.</summary>
    public IDisposable BeginDiscoveryOperation(CancellationToken cancellationToken)
    {
        var previous = discoveryOperation.Value;
        var operation = new DiscoveryOperation(cancellationToken);
        discoveryOperation.Value = operation;
        return new DiscoveryLease(() =>
        {
            discoveryOperation.Value = previous;
            operation.Dispose();
        });
    }

    public Task<AepManifest> DiscoverAsync(ResolvedAepExtension extension, string? expectedExtensionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = discoveryOperation.Value;
        if (operation is null || operation.IsDisposed)
            return DiscoverCoreAsync(extension, expectedExtensionId, cancellationToken);
        operation.Token.ThrowIfCancellationRequested();

        var registration = extension.Registration;
        var credentials = extension.AccessTokenProvider is ToolAepAccessTokenProvider token
            ? token.DiscoverySecretResolver : (object?)extension.AccessTokenProvider;
        var key = new DiscoveryKey(registration.Uid, registration.Generation, registration.ScopeRef,
            registration.Namespace, registration.Name, registration.Definition, expectedExtensionId,
            requestContext?.AccessMode ?? ControlPlaneAccessMode.Unavailable,
            requestContext?.ActorPrincipalId, CurrentDiscoveryContext(),
            credentials is null ? null : new ReferenceIdentity(credentials));
        var discovery = operation.GetOrAdd(key, () => DiscoverCoreAsync(extension, expectedExtensionId, operation.Token));
        return discovery is null ? DiscoverCoreAsync(extension, expectedExtensionId, cancellationToken)
            : discovery.Value.WaitAsync(cancellationToken);
    }

    private RequestContext? CurrentDiscoveryContext()
    {
        if (requestContext is null || requestContext.AccessMode is ControlPlaneAccessMode.Unavailable or ControlPlaneAccessMode.System)
            return null;
        // Principal-only administrative contexts intentionally have no operational Workspace.
        try { return requestContext.Current; }
        catch (InvalidOperationException) { return null; }
    }

    private sealed record DiscoveryKey(Guid Uid, long Generation, ResourceScopeRef? ScopeRef,
        ResourceNamespace Namespace, string Name, ExtensionRegistrationProperties Definition,
        string? ExpectedExtensionId, ControlPlaneAccessMode AccessMode, Guid? PrincipalId,
        RequestContext? Context, ReferenceIdentity? Credentials);

    private sealed class ReferenceIdentity(object value)
    {
        public override bool Equals(object? obj) => obj is ReferenceIdentity other && ReferenceEquals(value, other.Value);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(value);
        private object Value => value;
    }

    private sealed class DiscoveryOperation : IDisposable
    {
        private const int MaximumEntries = 256;
        private readonly Dictionary<DiscoveryKey, Lazy<Task<AepManifest>>> discoveries = [];
        private readonly CancellationTokenSource lifetime;
        private readonly object gate = new();
        private int disposed;

        public DiscoveryOperation(CancellationToken cancellationToken)
        {
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Token = lifetime.Token;
        }

        public CancellationToken Token { get; }
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;

        public Lazy<Task<AepManifest>>? GetOrAdd(DiscoveryKey key, Func<Task<AepManifest>> discover)
        {
            lock (gate)
            {
                if (IsDisposed) return null;
                if (discoveries.TryGetValue(key, out var existing)) return existing;
                // Preserve discovery behavior when the operation touches more identities than we retain.
                if (discoveries.Count >= MaximumEntries) return null;
                var pending = new Lazy<Task<AepManifest>>(discover, LazyThreadSafetyMode.ExecutionAndPublication);
                discoveries.Add(key, pending);
                return pending;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (gate) discoveries.Clear();
            try { lifetime.Cancel(); }
            finally { lifetime.Dispose(); }
        }
    }

    private sealed class DiscoveryLease(Action close) : IDisposable
    {
        private Action? close = close;
        public void Dispose() => Interlocked.Exchange(ref close, null)?.Invoke();
    }
}
