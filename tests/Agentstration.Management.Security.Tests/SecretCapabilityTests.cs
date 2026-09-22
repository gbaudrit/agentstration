using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Management.Security.Tests;

[TestClass]
public sealed class SecretCapabilityTests
{
    [TestMethod]
    public async Task IssuanceAuthorizesWithoutResolvingAndRedemptionIsSingleUse()
    {
        var authorizer = new FakeAccessAuthorizer();
        var resolver = new FakeResolver();
        using var capabilities = CreateCapabilities(authorizer, resolver, new ManualTimeProvider());
        var context = Context();

        var handle = await capabilities.IssueAsync(context, Binding(), ["credential"], CancellationToken.None);

        Assert.AreEqual(1, authorizer.Calls);
        Assert.AreEqual(0, resolver.Calls);
        Assert.AreEqual("[REDACTED]", handle.ToString());
        Assert.AreEqual("{}", JsonSerializer.Serialize(handle));
        using var secret = await capabilities.RedeemAsync(handle.RevealForTransport(), context);
        Assert.AreEqual("test-value", Encoding.UTF8.GetString(secret.Value.AccessValue().Span));
        Assert.AreEqual(1, resolver.Calls);
        await AssertCodeAsync("capability_invalid", () => capabilities.RedeemAsync(handle.RevealForTransport(), context));
    }

    [TestMethod]
    public async Task RedemptionUsesABoundedSystemContextForExactResourceResolution()
    {
        var requestContext = new CurrentRequestContext();
        var resolver = new FakeResolver(requestContext);
        using var capabilities = new SecretCapabilityService(
            new FakeAccessAuthorizer(), resolver, requestContext, new ManualTimeProvider());
        var context = Context();
        var handle = await capabilities.IssueAsync(context, Binding(), ["credential"], CancellationToken.None);

        Assert.AreEqual(ControlPlaneAccessMode.Unavailable, requestContext.AccessMode);
        using var secret = await capabilities.RedeemAsync(handle.RevealForTransport(), context);

        Assert.AreEqual(ControlPlaneAccessMode.System, resolver.AccessModeAtResolution);
        Assert.AreEqual(ControlPlaneAccessMode.Unavailable, requestContext.AccessMode);
    }

    [TestMethod]
    public async Task ProtocolRedemptionMatchesExtensionRequirementAndExecution()
    {
        var resolver = new FakeResolver();
        using var capabilities = CreateCapabilities(new FakeAccessAuthorizer(), resolver, new ManualTimeProvider());
        var context = Context();
        var wrong = await capabilities.IssueAsync(context, Binding(), ["credential"], CancellationToken.None);
        await AssertCodeAsync("context_mismatch", () => capabilities.RedeemAsync(
            wrong.RevealForTransport(), "other.extension", context.RequirementId, context.ExecutionId));
        Assert.AreEqual(0, resolver.Calls);

        var correct = await capabilities.IssueAsync(context, Binding(), ["credential"], CancellationToken.None);
        using var secret = await capabilities.RedeemAsync(correct.RevealForTransport(),
            context.ExtensionId, context.RequirementId, context.ExecutionId);
        Assert.AreEqual(1, resolver.Calls);
        Assert.AreEqual("test-value", Encoding.UTF8.GetString(secret.Value.AccessValue().Span));
        await AssertCodeAsync("capability_invalid", () => capabilities.RedeemAsync(correct.RevealForTransport(),
            context.ExtensionId, context.RequirementId, context.ExecutionId));
    }

    [TestMethod]
    public async Task AuditLogsContainOnlyContextAndNeverHandleOrValue()
    {
        var logger = new RecordingLogger();
        using var capabilities = CreateCapabilities(new FakeAccessAuthorizer(), new FakeResolver(),
            new ManualTimeProvider(), logger);
        var context = Context();
        var handle = await capabilities.IssueAsync(context, Binding(), ["credential"], CancellationToken.None);
        using var secret = await capabilities.RedeemAsync(handle.RevealForTransport(), context);

        var messages = string.Join(' ', logger.Messages);
        StringAssert.Contains(messages, context.ExtensionId);
        StringAssert.Contains(messages, context.RequirementId);
        Assert.IsFalse(messages.Contains(handle.RevealForTransport(), StringComparison.Ordinal));
        Assert.IsFalse(messages.Contains("test-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DeletedVaultProducesControlledValueFreeFailures()
    {
        var authorizer = new FakeAccessAuthorizer { VaultMissing = true };
        var resolver = new FakeResolver();
        using var capabilities = CreateCapabilities(authorizer, resolver, new ManualTimeProvider());
        var context = Context();
        await AssertCodeAsync("vault_unavailable", () => capabilities.IssueAsync(
            context, Binding(), ["credential"], CancellationToken.None));

        authorizer.VaultMissing = false;
        var handle = await capabilities.IssueAsync(context, Binding(), ["credential"], CancellationToken.None);
        resolver.VaultMissing = true;
        await AssertCodeAsync("vault_unavailable", () => capabilities.RedeemAsync(handle.RevealForTransport(), context));
        Assert.AreEqual(1, resolver.Calls);
    }

    [TestMethod]
    public async Task CancellationAndCleanupRaceLeaveNoRedeemableCapability()
    {
        var resolver = new FakeResolver();
        using var capabilities = CreateCapabilities(new FakeAccessAuthorizer(), resolver, new ManualTimeProvider());
        using var lifetime = new CancellationTokenSource();
        var context = Context();
        var handles = new ConcurrentBag<SecretCapabilityHandle>();
        var issuance = Enumerable.Range(0, 40).Select(_ => Task.Run(async () =>
        {
            try
            {
                handles.Add(await capabilities.IssueAsync(context, Binding(), ["credential"], lifetime.Token));
            }
            catch (SecretCapabilityException exception) when (exception.Code == "context_terminated") { }
        })).ToArray();
        lifetime.Cancel();
        capabilities.RevokeExecution(context);
        await Task.WhenAll(issuance);
        capabilities.RevokeExecution(context);

        foreach (var handle in handles)
        {
            var failure = await Assert.ThrowsExactlyAsync<SecretCapabilityException>(() =>
                capabilities.RedeemAsync(handle.RevealForTransport(), context));
            Assert.IsTrue(failure.Code is "capability_invalid" or "context_terminated");
        }
        Assert.AreEqual(0, resolver.Calls);
    }

    [TestMethod]
    public async Task ContextMismatchConsumesTheCapabilityWithoutResolving()
    {
        var resolver = new FakeResolver();
        using var capabilities = CreateCapabilities(new FakeAccessAuthorizer(), resolver, new ManualTimeProvider());
        var context = Context();
        var wrongContexts = new[]
        {
            context with { ExtensionId = "other.extension" },
            context with { Consumer = context.Consumer with { Name = "other-profile" } },
            context with { RequirementId = "other" },
            context with { ExecutionId = "other-execution" }
        };
        foreach (var wrong in wrongContexts)
        {
            var handle = await capabilities.IssueAsync(context, Binding(), ["credential"], CancellationToken.None);
            await AssertCodeAsync("context_mismatch", () => capabilities.RedeemAsync(handle.RevealForTransport(), wrong));
            await AssertCodeAsync("capability_invalid", () => capabilities.RedeemAsync(handle.RevealForTransport(), context));
        }
        Assert.AreEqual(0, resolver.Calls);
    }

    [TestMethod]
    public async Task ExpiryCancellationAndRevocationFailClosed()
    {
        var time = new ManualTimeProvider();
        using var capabilities = CreateCapabilities(new FakeAccessAuthorizer(), new FakeResolver(), time);
        var context = Context();
        var expired = await capabilities.IssueAsync(context, Binding(), ["credential"], CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(2));
        await AssertCodeAsync("capability_expired", () => capabilities.RedeemAsync(expired.RevealForTransport(), context));

        using var lifetime = new CancellationTokenSource();
        var cancelled = await capabilities.IssueAsync(context, Binding(), ["credential"], lifetime.Token);
        lifetime.Cancel();
        await AssertCodeAsync("context_terminated", () => capabilities.RedeemAsync(cancelled.RevealForTransport(), context));

        var revoked = await capabilities.IssueAsync(context, Binding(), ["credential"], CancellationToken.None);
        capabilities.Revoke(revoked);
        await AssertCodeAsync("capability_invalid", () => capabilities.RedeemAsync(revoked.RevealForTransport(), context));

        var execution = await capabilities.IssueAsync(context, Binding(), ["credential"], CancellationToken.None);
        capabilities.RevokeExecution(context);
        await AssertCodeAsync("capability_invalid", () => capabilities.RedeemAsync(execution.RevealForTransport(), context));
    }

    [TestMethod]
    public async Task ConcurrentReplayResolvesOnlyOnce()
    {
        var resolver = new FakeResolver();
        using var capabilities = CreateCapabilities(new FakeAccessAuthorizer(), resolver, new ManualTimeProvider());
        var context = Context();
        var handle = await capabilities.IssueAsync(context, Binding(), ["credential"], CancellationToken.None);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            try
            {
                using var value = await capabilities.RedeemAsync(handle.RevealForTransport(), context);
                return true;
            }
            catch (SecretCapabilityException exception) when (exception.Code == "capability_invalid")
            {
                return false;
            }
        }));

        Assert.AreEqual(1, results.Count(value => value));
        Assert.AreEqual(1, resolver.Calls);
    }

    [TestMethod]
    public async Task UndeclaredOrUnavailableSecretsNeverIssueUsableCapabilities()
    {
        var authorizer = new FakeAccessAuthorizer();
        var resolver = new FakeResolver();
        using var capabilities = CreateCapabilities(authorizer, resolver, new ManualTimeProvider());
        var context = Context();
        await AssertCodeAsync("requirement_undeclared", () => capabilities.IssueAsync(
            context, Binding(), ["other"], CancellationToken.None));
        await AssertCodeAsync("binding_invalid", () => capabilities.IssueAsync(
            context, Binding() with { RequirementId = "other" }, ["credential"], CancellationToken.None));
        Assert.AreEqual(0, authorizer.Calls);

        authorizer.Status = SecretValueStatus.Missing;
        await AssertCodeAsync("secret_unavailable", () => capabilities.IssueAsync(
            context, Binding(), ["credential"], CancellationToken.None));
        authorizer.Status = SecretValueStatus.VaultUnavailable;
        await AssertCodeAsync("vault_unavailable", () => capabilities.IssueAsync(
            context, Binding(), ["credential"], CancellationToken.None));
        authorizer.Status = SecretValueStatus.Configured;
        var handle = await capabilities.IssueAsync(context, Binding(), ["credential"], CancellationToken.None);
        resolver.Available = false;
        await AssertCodeAsync("secret_unavailable", () => capabilities.RedeemAsync(handle.RevealForTransport(), context));
        await AssertCodeAsync("capability_invalid", () => capabilities.RedeemAsync(handle.RevealForTransport(), context));
    }

    private static SecretCapabilityService CreateCapabilities(
        ISecretAccessAuthorizer authorizer,
        ISecretResolver resolver,
        TimeProvider timeProvider,
        ILogger<SecretCapabilityService>? logger = null) =>
        new(authorizer, resolver, new CurrentRequestContext(), timeProvider, logger);

    private static async Task AssertCodeAsync(string code, Func<Task> operation)
    {
        var exception = await Assert.ThrowsExactlyAsync<SecretCapabilityException>(operation);
        Assert.AreEqual(code, exception.Code);
        Assert.IsFalse(exception.Message.Contains("test-value", StringComparison.Ordinal));
    }

    private static SecretCapabilityContext Context()
    {
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        return new(
            ScopedResourceAddress.Create(scope, ResourceNamespace.Default, "ExtensionRegistration", "extension"),
            "extension.test",
            ScopedResourceAddress.Create(scope, ResourceNamespace.Default, "ModelProfile", "profile"),
            "credential",
            Guid.NewGuid().ToString("N"));
    }

    private static SecretBinding Binding() =>
        new("credential", new(new(ResourceNamespace.Default, "Secret", "api-key"), ResourceScopeRef.Instance));

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private sealed class FakeAccessAuthorizer : ISecretAccessAuthorizer
    {
        public int Calls { get; private set; }
        public SecretValueStatus Status { get; set; } = SecretValueStatus.Configured;
        public bool VaultMissing { get; set; }
        public Task<SecretValueStatus> GetAuthorizedStatusAsync(SecretReference secret, SecretResolutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (VaultMissing) throw new VaultResourceNotFoundException("vault");
            return Task.FromResult(Status);
        }
    }

    private sealed class FakeResolver(ICurrentRequestContext? requestContext = null) : ISecretResolver
    {
        public int Calls => Volatile.Read(ref calls);
        public bool Available { get; set; } = true;
        public bool VaultMissing { get; set; }
        public ControlPlaneAccessMode? AccessModeAtResolution { get; private set; }
        public Task<ResolvedSecret?> ResolveAsync(SecretReference secret, SecretResolutionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            AccessModeAtResolution = requestContext?.AccessMode;
            if (VaultMissing) throw new VaultResourceNotFoundException("vault");
            return Task.FromResult<ResolvedSecret?>(Available
                ? new(secret.Address, new(ResourceNamespace.Default, "Vault", "local"), new SecretValue("test-value"u8))
                : null);
        }
        private int calls;
    }

    private sealed class RecordingLogger : ILogger<SecretCapabilityService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
