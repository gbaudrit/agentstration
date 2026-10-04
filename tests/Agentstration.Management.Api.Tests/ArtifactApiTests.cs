using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ArtifactApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task StagedArtifactApiUsesDefaultBindingWithoutExposingBackendReference()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        using var client = factory.CreateClient();

        var bindings = await client.GetFromJsonAsync<ArtifactStagingBindingResource[]>("/api/artifacts/staging-bindings");
        Assert.IsNotNull(bindings);
        Assert.IsTrue(bindings.Any(value => value.Name == ArtifactPlatformResourceProvisioner.DefaultBindingName && value.Definition.IsDefault));

        using var createdResponse = await client.PostAsJsonAsync("/api/artifacts/staged", new CreateStagedArtifactRequest(
            "notes.txt", "text/plain", new ArtifactProducer { Kind = ArtifactProducerKind.FlowRun, Id = "producer", FlowRunId = "flow-1", FlowStepId = "compose", CorrelationId = "corr-1" }));
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        var createdJson = await createdResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("backendReference", createdJson, StringComparison.OrdinalIgnoreCase);
        var created = JsonSerializer.Deserialize<StagedArtifactView>(createdJson, JsonOptions());
        Assert.IsNotNull(created);

        var content = Encoding.UTF8.GetBytes("governed artifact");
        using var write = await client.PostAsJsonAsync($"/api/artifacts/staged/{created.ArtifactId}/content",
            new WriteStagedArtifactRequest(0, Convert.ToBase64String(content)));
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);
        using var seal = await client.PostAsync($"/api/artifacts/staged/{created.ArtifactId}/seal", null);
        Assert.AreEqual(HttpStatusCode.OK, seal.StatusCode);
        var sealedArtifact = await seal.Content.ReadFromJsonAsync<StagedArtifactView>(JsonOptions());
        Assert.IsNotNull(sealedArtifact);
        Assert.AreEqual(content.Length, sealedArtifact.Length);
        Assert.IsNotNull(sealedArtifact.Sha256);

        using var leaseResponse = await client.PostAsJsonAsync($"/api/artifacts/staged/{created.ArtifactId}/leases",
            new CreateArtifactLeaseRequest("flow", "consumer-flow", [ArtifactLeaseOperation.Inspect, ArtifactLeaseOperation.Read], DateTimeOffset.UtcNow.AddMinutes(5)));
        var delegated = await leaseResponse.Content.ReadFromJsonAsync<StagedArtifactView>(JsonOptions());
        Assert.IsNotNull(delegated);
        Assert.HasCount(1, delegated.Leases);
        var lease = delegated.Leases[0];
        var read = await client.GetFromJsonAsync<ArtifactContentChunk>(
            $"/api/artifacts/staged/{created.ArtifactId}/content?offset=0&length=64&leaseId={lease.Id}", JsonOptions());
        Assert.IsNotNull(read);
        CollectionAssert.AreEqual(content, Convert.FromBase64String(read.ContentBase64));
    }

    [TestMethod]
    public async Task StorageToolsPersistIdempotentlyAndMaterializeThroughStandardFlows()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var service = factory.Services.GetRequiredService<ArtifactManagementService>();
        var source = await service.CreateStagedAsync(new("report.txt", "text/plain",
            new ArtifactProducer { Kind = ArtifactProducerKind.FlowRun, Id = "producer", FlowRunId = "producer-run", FlowStepId = "render" }), default);
        var bytes = Encoding.UTF8.GetBytes("durable data");
        _ = await service.WriteAsync(source.Value.ArtifactId, 0, bytes, default);
        _ = await service.SealAsync(source.Value.ArtifactId, default);

        var invocation = Invocation(context, JsonSerializer.SerializeToElement(new
        {
            stagedArtifactId = source.Value.ArtifactId.ToString(),
            producerFlowRunId = "producer-run",
            producerFlowStepId = "render"
        }), "storage-run");
        var writer = factory.Services.GetRequiredService<ArtifactStorageWriteMcpTool>();
        var first = await writer.ExecuteAsync(invocation, default);
        var retry = await writer.ExecuteAsync(invocation, default);
        Assert.IsNotNull(first);
        Assert.AreEqual(first.Value.GetProperty("flowRunArtifactId").GetString(), retry!.Value.GetProperty("flowRunArtifactId").GetString());

        var reader = factory.Services.GetRequiredService<ArtifactStorageReadMcpTool>();
        var materialized = await reader.ExecuteAsync(Invocation(context, JsonSerializer.SerializeToElement(new
        {
            flowRunArtifactId = first.Value.GetProperty("flowRunArtifactId").GetString()
        }), "read-run"), default);
        Assert.IsNotNull(materialized);
        var staged = materialized.Value.Deserialize<StagedArtifactView>(JsonOptions());
        Assert.IsNotNull(staged);
        var chunk = await service.ReadAsync(staged.ArtifactId, 0, 64, null, default);
        CollectionAssert.AreEqual(bytes, Convert.FromBase64String(chunk.ContentBase64));

        var flows = factory.Services.GetRequiredService<FlowService>();
        var workspaceId = new WorkspaceId(context.WorkspaceId);
        var writeFlow = await flows.GetAsync(workspaceId, new(ArtifactPlatformResourceProvisioner.StorageWriteFlowName), default);
        var readFlow = await flows.GetAsync(workspaceId, new(ArtifactPlatformResourceProvisioner.StorageReadFlowName), default);
        Assert.AreEqual("1.0.0", writeFlow?.Value.ActiveVersion);
        Assert.AreEqual("1.0.0", readFlow?.Value.ActiveVersion);
        Assert.AreEqual(ArtifactFlowContracts.StorageWrite, writeFlow?.Value.Metadata["artifact.contract"]);
        Assert.AreEqual(ArtifactFlowContracts.StorageRead, readFlow?.Value.Metadata["artifact.contract"]);
        Assert.AreEqual("true", writeFlow?.Value.Metadata[ResourceProvenanceAnnotations.BuiltIn]);
        Assert.AreEqual("true", readFlow?.Value.Metadata[ResourceProvenanceAnnotations.BuiltIn]);
    }

    [TestMethod]
    public async Task NewWorkspaceImmediatelyReceivesCompleteBuiltInArtifactResources()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var workspaceId = Guid.NewGuid();
        var workspace = new Workspace(workspaceId, context.TenantId, $"provision-{workspaceId:N}",
            "Provisioned Workspace", WorkspaceStatus.Initializing, DateTimeOffset.UtcNow);
        var identities = factory.Services.GetRequiredService<IIdentityStore>();
        await identities.AddWorkspaceAsync(workspace, default);

        await factory.Services.GetRequiredService<IWorkspaceProvisioner>().ProvisionAsync(workspace, default);

        var flows = factory.Services.GetRequiredService<FlowService>();
        var write = await flows.GetAsync(new WorkspaceId(workspaceId),
            new(ArtifactPlatformResourceProvisioner.StorageWriteFlowName), default);
        var read = await flows.GetAsync(new WorkspaceId(workspaceId),
            new(ArtifactPlatformResourceProvisioner.StorageReadFlowName), default);
        Assert.AreEqual(ArtifactPlatformResourceProvisioner.DefaultToolSetVersion, write?.Value.ActiveVersion);
        Assert.AreEqual(ArtifactPlatformResourceProvisioner.DefaultToolSetVersion, read?.Value.ActiveVersion);

        var store = factory.Services.GetRequiredService<IResourceStore>();
        using var systemScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var scope = ResourceScopeRef.Workspace(workspaceId);
        var binding = await store.GetExactAsync<ArtifactStagingBindingResource>(ScopedResourceAddress.Create(
            scope, ResourceNamespace.Default, ArtifactResourceKinds.ArtifactStagingBinding,
            ArtifactPlatformResourceProvisioner.DefaultBindingName), default);
        var staging = await store.GetExactAsync<ToolSetResource>(ScopedResourceAddress.Create(
            scope, ResourceNamespace.Default, ToolResourceKinds.ToolSet,
            ArtifactPlatformResourceProvisioner.DefaultToolSetName), default);
        var broker = await store.GetExactAsync<ToolSetResource>(ScopedResourceAddress.Create(
            scope, ResourceNamespace.Default, ToolResourceKinds.ToolSet,
            ArtifactPlatformResourceProvisioner.BrokerToolSetName), default);
        Assert.IsTrue(binding?.Value.Definition.IsDefault);
        Assert.AreEqual("true", staging?.Value.Metadata.Annotations[ResourceProvenanceAnnotations.BuiltIn]);
        Assert.AreEqual("true", broker?.Value.Metadata.Annotations[ResourceProvenanceAnnotations.BuiltIn]);
    }

    [TestMethod]
    public async Task ProvisioningDemotesLegacyBuiltInDefaultBindingWithoutBreakingCompatibility()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var currentAddress = ScopedResourceAddress.Create(scope, ResourceNamespace.Default,
            ArtifactResourceKinds.ArtifactStagingBinding, ArtifactPlatformResourceProvisioner.DefaultBindingName);
        var current = await store.GetExactAsync<ArtifactStagingBindingResource>(currentAddress, default);
        Assert.IsNotNull(current);
        _ = await store.PutExactAsync(scope, current.Value with
        {
            Generation = checked(current.Value.Generation + 1),
            Definition = current.Value.Definition with { IsDefault = false }
        }, current.ETag, false, default);
        var legacy = await store.PutExactAsync(scope, new ArtifactStagingBindingResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ArtifactResourceKinds.ArtifactStagingBinding,
            Metadata = new()
            {
                Name = "filesystem-default",
                Annotations = new Dictionary<string, string> { [ResourceProvenanceAnnotations.BuiltIn] = "true" }
            },
            ScopeRef = scope,
            Generation = 1,
            Status = new() { ProvisioningState = ProvisioningState.Succeeded },
            Definition = current.Value.Definition with { IsDefault = true }
        }, null, true, default);

        await factory.Services.GetRequiredService<ArtifactPlatformResourceProvisioner>().EnsureAsync(scope, default);

        var migratedLegacy = await store.GetExactAsync<ArtifactStagingBindingResource>(
            ScopedResourceAddress.Create(scope, legacy.Value.Namespace, legacy.Value.Kind, legacy.Value.Name), default);
        var restoredCurrent = await store.GetExactAsync<ArtifactStagingBindingResource>(currentAddress, default);
        Assert.IsFalse(migratedLegacy?.Value.Definition.IsDefault);
        Assert.IsTrue(restoredCurrent?.Value.Definition.IsDefault);
    }

    [TestMethod]
    public async Task HandoffCopiesAcrossBindingsAndRecoversByOperationId()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var service = factory.Services.GetRequiredService<ArtifactManagementService>();
        _ = await service.CreateBindingAsync(ResourceNamespace.Default, "filesystem-secondary", new ArtifactStagingBindingProperties
        {
            DisplayName = "Secondary filesystem route",
            ToolSet = new ResourceReference(ArtifactPlatformResourceProvisioner.DefaultToolSetName),
            ToolSetVersion = ArtifactPlatformResourceProvisioner.DefaultToolSetVersion
        }, default);
        var source = await service.CreateStagedAsync(new("handoff.bin", "application/octet-stream",
            new ArtifactProducer { Kind = ArtifactProducerKind.Agent, Id = "agent-a", AgentId = "agent-a" }), default);
        var bytes = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        _ = await service.WriteAsync(source.Value.ArtifactId, 0, bytes, default);
        _ = await service.SealAsync(source.Value.ArtifactId, default);
        var request = new ArtifactHandoffRequest("transfer-1", ArtifactHandoffMode.Copy, "agent", "agent-b",
            [ArtifactLeaseOperation.Inspect, ArtifactLeaseOperation.Read], DateTimeOffset.UtcNow.AddMinutes(5),
            new ResourceReference("filesystem-secondary"));

        var first = await service.HandoffAsync(source.Value.ArtifactId, request, default);
        var retry = await service.HandoffAsync(source.Value.ArtifactId, request, default);

        Assert.AreNotEqual(source.Value.ArtifactId, first.Artifact.ArtifactId);
        Assert.AreEqual(first.Artifact.ArtifactId, retry.Artifact.ArtifactId);
        Assert.IsTrue(retry.Recovered);
        Assert.AreEqual(first.Artifact.Sha256, retry.Artifact.Sha256);
    }

    [TestMethod]
    public async Task InternalBackendToolsRejectDirectAgentInvocation()
    {
        var guard = new ArtifactStagingExecutionGuard();
        var denied = await guard.BeforeInvokeAsync(Context(ToolExecutionOwnerKind.RuntimeRun, ArtifactCapabilities.Delete));
        var allowed = await guard.BeforeInvokeAsync(Context(ToolExecutionOwnerKind.ArtifactService, ArtifactCapabilities.Delete));
        var storageFlow = await guard.BeforeInvokeAsync(Context(ToolExecutionOwnerKind.FlowRun, ArtifactStorageWriteMcpTool.Name));
        Assert.AreEqual(ToolExecutionHookDecisionKind.Deny, denied.Kind);
        Assert.AreEqual(ToolExecutionHookDecisionKind.Allow, allowed.Kind);
        Assert.AreEqual(ToolExecutionHookDecisionKind.Allow, storageFlow.Kind);
    }

    [TestMethod]
    public async Task StagedArtifactsAreWorkspaceIsolatedBoundedAndProtectedByActiveLeases()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var service = factory.Services.GetRequiredService<ArtifactManagementService>();
        var otherWorkspaceId = Guid.NewGuid();
        StagedArtifactId artifactId;
        ArtifactLeaseId leaseId;
        using (scopes.Push(context))
        {
            var identityStore = factory.Services.GetRequiredService<IIdentityStore>();
            var otherWorkspace = new Workspace(otherWorkspaceId, context.TenantId, $"isolation-{otherWorkspaceId:N}",
                "Artifact isolation", WorkspaceStatus.Initializing, DateTimeOffset.UtcNow);
            await identityStore.AddWorkspaceAsync(otherWorkspace, default);
            await factory.Services.GetRequiredService<IWorkspaceProvisioner>().ProvisionAsync(otherWorkspace, default);
            var created = await service.CreateStagedAsync(new("bounded.bin", "application/octet-stream",
                new ArtifactProducer { Kind = ArtifactProducerKind.Tool, Id = "producer" }), default);
            artifactId = created.Value.ArtifactId;
            var tooLarge = new byte[ArtifactManagementService.MaximumChunkBytes + 1];
            var rejected = await Assert.ThrowsAsync<ArtifactValidationException>(async () =>
                await service.WriteAsync(artifactId, 0, tooLarge, default));
            Assert.AreEqual("staged_artifact_chunk_invalid", rejected.Code);
            _ = await service.WriteAsync(artifactId, 0, new byte[] { 1, 2, 3 }, default);
            _ = await service.SealAsync(artifactId, default);
            var leased = await service.CreateLeaseAsync(artifactId,
                new("agent", "consumer", [ArtifactLeaseOperation.Read], DateTimeOffset.UtcNow.AddMinutes(5)), default);
            leaseId = leased.Value.Leases.Single().Id;
            var activeLease = await Assert.ThrowsAsync<ArtifactValidationException>(async () =>
                await service.PurgeAsync(artifactId, default));
            Assert.AreEqual("staged_artifact_active_lease", activeLease.Code);
        }

        using (scopes.Push(context with { WorkspaceId = otherWorkspaceId }))
        {
            Assert.IsNull(await service.GetStagedAsync(artifactId, leaseId, ArtifactLeaseOperation.Read, default));
            var hidden = await Assert.ThrowsAsync<ArtifactValidationException>(async () =>
                await service.ReadAsync(artifactId, 0, 3, leaseId, default));
            Assert.AreEqual("staged_artifact_not_found", hidden.Code);
        }

        using (scopes.Push(context))
        {
            _ = await service.RevokeLeaseAsync(artifactId, leaseId, default);
            await service.PurgeAsync(artifactId, default);
            var purged = await service.GetStagedAsync(artifactId, null, ArtifactLeaseOperation.Inspect, default);
            Assert.AreEqual(StagedArtifactStatus.Purged, purged?.Value.ArtifactStatus);
        }
    }

    [TestMethod]
    public async Task McpExposesBrokerToolsButHidesStorageImplementationTools()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var http = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), Name = "artifact-test" },
            http,
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        await using var mcp = await McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance);
        var tools = await mcp.ListToolsAsync();

        Assert.IsTrue(tools.Any(value => value.Name == "staged-artifact.inspect"));
        Assert.IsTrue(tools.Any(value => value.Name == "staged-artifact.read-content"));
        Assert.IsFalse(tools.Any(value => value.Name.StartsWith("artifact.staging.", StringComparison.Ordinal)));
        Assert.IsFalse(tools.Any(value => value.Name.StartsWith("artifact.storage.local.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ExpiredLeasesStaleBindingsAndIntegrityMismatchesAreRejected()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var service = factory.Services.GetRequiredService<ArtifactManagementService>();
        var bytes = Encoding.UTF8.GetBytes("integrity");
        var created = await service.CreateStagedAsync(new("integrity.txt", "text/plain",
            new ArtifactProducer { Kind = ArtifactProducerKind.FlowRun, Id = "flow", FlowRunId = "flow" }), default);
        _ = await service.WriteAsync(created.Value.ArtifactId, 0, bytes, default);
        var sealedArtifact = await service.SealAsync(created.Value.ArtifactId, default);

        var badReceipt = new ArtifactStorageReceipt
        {
            StorageFlowRunId = "storage",
            OpaqueReference = "opaque",
            MediaType = sealedArtifact.Value.MediaType,
            Length = sealedArtifact.Value.Length,
            Sha256 = new string('0', 64)
        };
        var integrity = await Assert.ThrowsAsync<ArtifactValidationException>(async () =>
            await service.CompleteFlowRunArtifactAsync(created.Value.ArtifactId, new("flow", "step", badReceipt), default));
        Assert.AreEqual("artifact_storage_receipt_mismatch", integrity.Code);

        var lease = (await service.CreateLeaseAsync(created.Value.ArtifactId,
            new("agent", "remote", [ArtifactLeaseOperation.Read], DateTimeOffset.UtcNow.AddMilliseconds(100)), default)).Value.Leases.Single();
        await Task.Delay(150);
        var restricted = context with
        {
            Restriction = new AuthorizationRestriction(Guid.NewGuid(), context.WorkspaceId, new HashSet<string>(StringComparer.Ordinal))
        };
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(restricted))
        {
            _ = await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
                await service.ReadAsync(created.Value.ArtifactId, 0, bytes.Length, lease.Id, default));
        }

        var binding = await service.GetBindingAsync(ResourceNamespace.Default, ArtifactPlatformResourceProvisioner.DefaultBindingName, default);
        Assert.IsNotNull(binding);
        var store = factory.Services.GetRequiredService<IResourceStore>();
        _ = await store.PutExactAsync(binding.Value.ScopeRef!.Value, binding.Value with
        {
            Definition = binding.Value.Definition with { ToolSetVersion = "2.0.0" }
        }, binding.ETag, false, default);
        var stale = await Assert.ThrowsAsync<ArtifactValidationException>(async () =>
            await service.ReadAsync(created.Value.ArtifactId, 0, bytes.Length, null, default));
        Assert.AreEqual("artifact_staging_binding_changed", stale.Code);
    }

    [TestMethod]
    public async Task ExpirationRetriesProviderFailureAndHonorsCancellation()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var backend = new FaultingArtifactStagingExecutor();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<IArtifactStagingToolExecutor>();
            services.AddSingleton<IArtifactStagingToolExecutor>(backend);
        }));
        var context = await GetBootstrapContextAsync(factory);
        var requestScopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var service = factory.Services.GetRequiredService<ArtifactManagementService>();
        StoredResource<StagedArtifactResource> created;
        using (requestScopes.Push(context))
        {
            created = await service.CreateStagedAsync(new("expiry.bin", "application/octet-stream",
                new ArtifactProducer { Kind = ArtifactProducerKind.Tool, Id = "tool" }, ExpiresAt: clock.GetUtcNow().AddMinutes(1)), default);
            _ = await service.WriteAsync(created.Value.ArtifactId, 0, new byte[] { 4, 5, 6 }, default);
            _ = await service.SealAsync(created.Value.ArtifactId, default);
        }
        clock.Advance(TimeSpan.FromMinutes(2));
        backend.FailNextDelete = true;

        using (requestScopes.PushSystem())
            Assert.AreEqual(0, await service.ExpireAndPurgeAsync(10, default));
        using (requestScopes.Push(context))
        {
            var failed = await service.GetStagedAsync(created.Value.ArtifactId, null, ArtifactLeaseOperation.Inspect, default);
            Assert.AreEqual(StagedArtifactStatus.Expired, failed?.Value.ArtifactStatus);
            Assert.AreEqual("staged_artifact_backend_delete_failed", failed?.Value.FailureCode);
        }
        using (requestScopes.PushSystem())
            Assert.AreEqual(1, await service.ExpireAndPurgeAsync(10, default));
        using (requestScopes.Push(context))
        {
            var purged = await service.GetStagedAsync(created.Value.ArtifactId, null, ArtifactLeaseOperation.Inspect, default);
            Assert.AreEqual(StagedArtifactStatus.Purged, purged?.Value.ArtifactStatus);
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using (requestScopes.PushSystem())
            _ = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await service.ExpireAndPurgeAsync(10, cancellation.Token));
    }

    private static InternalMcpToolInvocation Invocation(RequestContext context, JsonElement arguments, string runId) => new(
        context.TenantId, new WorkspaceId(context.WorkspaceId), context.PrincipalId, $"call-{runId}", "correlation",
        arguments, ToolDefinitionCallerKind.Flow, $"flow:{runId}", runId, "storage");

    private static ToolExecutionContext Context(ToolExecutionOwnerKind owner, string externalId) => new()
    {
        OwnerKind = owner,
        ToolCallId = "call",
        InvocationId = "invocation",
        ToolId = "tool",
        ToolName = externalId,
        ExternalToolId = externalId
    };

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class FaultingArtifactStagingExecutor : IArtifactStagingToolExecutor
    {
        private readonly Dictionary<string, List<byte>> content = new(StringComparer.Ordinal);
        public bool FailNextDelete { get; set; }

        public Task ValidateAsync(ArtifactStagingBindingResource binding, ArtifactBackendCommandContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<ArtifactBackendCreateResult> CreateAsync(ArtifactStagingBindingResource binding, StagedArtifactId artifactId,
            string mediaType, ArtifactBackendCommandContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = artifactId.ToString();
            content.Add(reference, []);
            return Task.FromResult(new ArtifactBackendCreateResult(reference));
        }

        public Task<long> WriteAsync(ArtifactStagingBindingResource binding, ArtifactBackendResolution backend, long offset,
            ReadOnlyMemory<byte> value, ArtifactBackendCommandContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = content[backend.BackendReference];
            if (target.Count != offset) throw new InvalidOperationException("Unexpected offset.");
            target.AddRange(value.ToArray());
            return Task.FromResult((long)target.Count);
        }

        public Task<ArtifactContentChunk> ReadAsync(ArtifactStagingBindingResource binding, ArtifactBackendResolution backend,
            long offset, int length, ArtifactBackendCommandContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = content[backend.BackendReference];
            var bytes = source.Skip(checked((int)offset)).Take(length).ToArray();
            return Task.FromResult(new ArtifactContentChunk(offset, Convert.ToBase64String(bytes), offset + bytes.Length >= source.Count));
        }

        public Task<ArtifactBackendStat> StatAsync(ArtifactStagingBindingResource binding, ArtifactBackendResolution backend,
            ArtifactBackendCommandContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = content[backend.BackendReference].ToArray();
            return Task.FromResult(new ArtifactBackendStat(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }

        public Task DeleteAsync(ArtifactStagingBindingResource binding, ArtifactBackendResolution backend,
            ArtifactBackendCommandContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextDelete)
            {
                FailNextDelete = false;
                throw new IOException("Transient provider failure.");
            }
            content.Remove(backend.BackendReference);
            return Task.CompletedTask;
        }
    }
}
