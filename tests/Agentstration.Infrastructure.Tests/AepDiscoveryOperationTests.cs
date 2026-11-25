using System.Net;
using System.Net.Http.Json;
using System.Text;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class AepDiscoveryOperationTests
{
    [TestMethod]
    public async Task SevenFormatResolutionsDiscoverThreeExtensionsOncePerOperation()
    {
        var http = new ManifestHttpFactory();
        var transport = new AepMcpTransport(http, NullLoggerFactory.Instance);
        var extensions = Enumerable.Range(0, 3).Select(index => Extension("extension-" + index)).ToArray();
        var registry = new SchemaFormatRegistry([new BuiltinJsonSchemaFormatHandler()], [new FormatSource(transport, extensions)]);
        for (var operation = 1; operation <= 2; operation++)
        {
            using var scope = transport.BeginDiscoveryOperation(default);
            for (var resolution = 0; resolution < 7; resolution++)
                _ = await registry.ResolveAsync(SchemaFormatIds.JsonSchema, SchemaFormatCapabilities.ValidateDefinition);
            Assert.AreEqual(operation * 3, http.Calls);
        }
    }

    [TestMethod]
    public async Task ConcurrentWaitersShareOneDiscoveryAndCancellationOnlyCancelsTheirWait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new ManifestHttpFactory { BeforeReply = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        var transport = new AepMcpTransport(http, NullLoggerFactory.Instance);
        using var scope = transport.BeginDiscoveryOperation(default);
        using var cancelled = new CancellationTokenSource();
        var extension = Extension("shared");
        var first = transport.DiscoverAsync(extension, "example.extension", cancelled.Token);
        await entered.Task;
        var others = Enumerable.Range(0, 8).Select(_ => transport.DiscoverAsync(extension, "example.extension", default)).ToArray();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        release.SetResult();
        await Task.WhenAll(others);
        Assert.AreEqual(1, http.Calls);
    }

    [TestMethod]
    public async Task OperationCancellationCancelsTheSharedRequestAndTheNextOperationRetries()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new ManifestHttpFactory { BeforeReply = async token => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); } };
        var transport = new AepMcpTransport(http, NullLoggerFactory.Instance);
        var extension = Extension("cancelled");
        using (var cancelled = new CancellationTokenSource())
        using (transport.BeginDiscoveryOperation(cancelled.Token))
        {
            var pending = transport.DiscoverAsync(extension, "example.extension", default);
            await entered.Task;
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        }
        http.BeforeReply = null;
        using (transport.BeginDiscoveryOperation(default))
            _ = await transport.DiscoverAsync(extension, "example.extension", default);
        Assert.AreEqual(2, http.Calls);
    }

    [TestMethod]
    public async Task IndependentConcurrentOperationsAndCallsOutsideAnOperationStayFresh()
    {
        var http = new ManifestHttpFactory();
        var transport = new AepMcpTransport(http, NullLoggerFactory.Instance);
        var extension = Extension("fresh");
        await Task.WhenAll(Enumerable.Range(0, 2).Select(operationIndex => Task.Run(async () =>
        {
            using var scope = transport.BeginDiscoveryOperation(default);
            _ = await transport.DiscoverAsync(extension, "example.extension", default);
            _ = await transport.DiscoverAsync(extension, "example.extension", default);
        })));
        Assert.AreEqual(2, http.Calls);
        _ = await transport.DiscoverAsync(extension, "example.extension", default);
        _ = await transport.DiscoverAsync(extension, "example.extension", default);
        Assert.AreEqual(4, http.Calls);
    }

    [TestMethod]
    public async Task NestedOperationsRestoreTheirParentAndJoiningKeepsItsDiscovery()
    {
        var http = new ManifestHttpFactory();
        var transport = new AepMcpTransport(http, NullLoggerFactory.Instance);
        var extension = Extension("nested");
        using var outer = transport.BeginDiscoveryOperation(default);
        var first = await transport.DiscoverAsync(extension, "example.extension", default);
        http.Version = "2.0.0";
        using (transport.BeginDiscoveryOperation(default, joinExisting: true))
            Assert.AreSame(first, await transport.DiscoverAsync(extension, "example.extension", default));
        using (transport.BeginDiscoveryOperation(default))
            Assert.AreEqual("2.0.0", (await transport.DiscoverAsync(extension, "example.extension", default)).Extension.Version);
        Assert.AreSame(first, await transport.DiscoverAsync(extension, "example.extension", default));
        Assert.AreEqual(2, http.Calls);
    }

    [TestMethod]
    public async Task CallerWorkspaceAndRegistrationChangesKeepDiscoveriesSeparate()
    {
        var current = new CurrentRequestContext();
        var http = new ManifestHttpFactory();
        var transport = new AepMcpTransport(http, NullLoggerFactory.Instance, current);
        var extension = Extension("isolated");
        var context = new RequestContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var operation = transport.BeginDiscoveryOperation(default);
        using (current.Push(context))
        {
            _ = await transport.DiscoverAsync(extension, "example.extension", default);
            _ = await transport.DiscoverAsync(extension, "example.extension", default);
            _ = await transport.DiscoverAsync(extension with { Registration = extension.Registration with { Generation = 2 } }, "example.extension", default);
            _ = await transport.DiscoverAsync(extension with { Registration = extension.Registration with { ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) } }, "example.extension", default);
        }
        using (current.Push(context with { WorkspaceId = Guid.NewGuid() }))
            _ = await transport.DiscoverAsync(extension, "example.extension", default);
        using (current.Push(context with { PrincipalId = Guid.NewGuid() }))
            _ = await transport.DiscoverAsync(extension, "example.extension", default);
        Assert.AreEqual(5, http.Calls);
    }

    [TestMethod]
    public async Task RecreatedGovernedCredentialsShareDiscoveryButOtherCredentialSourcesDoNot()
    {
        var http = new ManifestHttpFactory();
        var transport = new AepMcpTransport(http, NullLoggerFactory.Instance);
        var registration = Extension("authenticated").Registration;
        registration = registration with { Definition = registration.Definition with
        {
            AuthenticationMode = AepTransportAuthenticationMode.StaticBearer,
            Credential = new ResourceReference("fixture-token", registration.ScopeRef)
        } };
        var firstSecrets = new FixtureSecrets();
        var otherSecrets = new FixtureSecrets();
        using var operation = transport.BeginDiscoveryOperation(default);
        for (var call = 0; call < 7; call++)
            _ = await transport.DiscoverAsync(new(registration, AepExtensionCredentials.Create(registration, firstSecrets)), "example.extension", default);
        Assert.AreEqual(1, http.Calls);
        _ = await transport.DiscoverAsync(new(registration, AepExtensionCredentials.Create(registration, otherSecrets)), "example.extension", default);
        Assert.AreEqual(2, http.Calls);
        Assert.IsTrue(firstSecrets.Calls > 0 && otherSecrets.Calls > 0);
    }

    [TestMethod]
    public async Task CancelledOperationsDoNotReturnCompletedDescriptors()
    {
        var http = new ManifestHttpFactory();
        var transport = new AepMcpTransport(http, NullLoggerFactory.Instance);
        var extension = Extension("completed");
        using var cancelled = new CancellationTokenSource();
        using var scope = transport.BeginDiscoveryOperation(cancelled.Token);
        _ = await transport.DiscoverAsync(extension, "example.extension", default);
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => transport.DiscoverAsync(extension, "example.extension", default));
        Assert.AreEqual(1, http.Calls);
    }

    [TestMethod]
    public async Task AChildThatOutlivesTheOperationCannotReuseItsDescriptor()
    {
        var http = new ManifestHttpFactory();
        var transport = new AepMcpTransport(http, NullLoggerFactory.Instance);
        var extension = Extension("escaped");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AepManifest> child;
        using (transport.BeginDiscoveryOperation(default))
        {
            _ = await transport.DiscoverAsync(extension, "example.extension", default);
            child = Task.Run(async () => { await release.Task; return await transport.DiscoverAsync(extension, "example.extension", default); });
        }
        http.Version = "2.0.0";
        release.SetResult();
        Assert.AreEqual("2.0.0", (await child).Extension.Version);
        Assert.AreEqual(2, http.Calls);
    }

    [TestMethod]
    public async Task IdentityFailuresAreSharedOnlyWithinTheirOperationAndFreshDiscoveryCanRecover()
    {
        var http = new ManifestHttpFactory { ExtensionId = "unexpected.extension" };
        var transport = new AepMcpTransport(http, NullLoggerFactory.Instance);
        var extension = Extension("failure");
        using (transport.BeginDiscoveryOperation(default))
        {
            for (var attempt = 0; attempt < 2; attempt++)
                await Assert.ThrowsAsync<AepProtocolException>(() => transport.DiscoverAsync(extension, "example.extension", default));
            Assert.AreEqual(1, http.Calls);
        }
        http.ExtensionId = "example.extension";
        using (transport.BeginDiscoveryOperation(default))
            _ = await transport.DiscoverAsync(extension, "example.extension", default);
        Assert.AreEqual(2, http.Calls);
    }

    private static ResolvedAepExtension Extension(string name) => new(new ExtensionRegistrationResource
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ExtensionKinds.ExtensionRegistration,
        ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()),
        Metadata = new() { Name = name },
        Generation = 1,
        Definition = new() { DisplayName = name, Endpoint = new Uri("http://extension.test/"), ExpectedExtensionId = "example.extension" }
    }, null);

    private sealed class FormatSource(AepMcpTransport transport, IReadOnlyList<ResolvedAepExtension> extensions) : ISchemaFormatHandlerSource
    {
        public async ValueTask<IReadOnlyList<ISchemaFormatHandler>> ListAsync(CancellationToken cancellationToken = default)
        {
            foreach (var extension in extensions)
                _ = await transport.DiscoverAsync(extension, "example.extension", cancellationToken);
            return [];
        }
    }

    private sealed class FixtureSecrets : ISecretResolver
    {
        public int Calls { get; private set; }
        public Task<ResolvedSecret?> ResolveAsync(SecretReference secret, SecretResolutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<ResolvedSecret?>(new(secret.Address, new(ResourceNamespace.Default, "Vault", "fixture"),
                new SecretValue(Encoding.UTF8.GetBytes("public-disposable-fixture-token"))));
        }
    }

    private sealed class ManifestHttpFactory : IHttpMessageHandlerFactory
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public string ExtensionId { get; set; } = "example.extension";
        public string Version { get; set; } = "1.0.0";
        public Func<CancellationToken, Task>? BeforeReply { get; set; }
        public HttpMessageHandler CreateHandler(string name) => new Handler(this);

        private sealed class Handler(ManifestHttpFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.AreEqual(AepProtocol.DiscoveryPath, request.RequestUri!.AbsolutePath);
                Interlocked.Increment(ref owner.calls);
                if (owner.BeforeReply is { } wait) await wait(cancellationToken);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new AepManifest(AepProtocol.Version,
                    new(owner.ExtensionId, "Fixture extension", owner.Version), new Dictionary<string, AepCapabilityDescriptor>(), new([])), options: AepProtocol.JsonOptions) };
            }
        }
    }
}
