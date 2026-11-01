using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Extensions;
using Agentstration.Extensions.Aep;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.Packs;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Storage.Sqlite;
using Agentstration.Resources;
using Agentstration.Runtime.Core;
using Agentstration.Schemas.Contracts;
using Agentstration.Security.Contracts;
using Agentstration.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ManagementPlaneTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void AgentResourceSerializesWithCanonicalEnvelopeAndStructuredReferences()
    {
        var resource = Agent("assistant") with
        {
            Metadata = new ResourceMetadata
            {
                Name = "assistant",
                Tags = new Dictionary<string, string> { ["team"] = "platform" },
                Annotations = new Dictionary<string, string> { ["owner"] = "engineering" }
            }
        };

        var json = JsonSerializer.Serialize(resource, JsonOptions);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.AreEqual(ResourceApiVersions.CoreV1, root.GetProperty("apiVersion").GetString());
        Assert.AreEqual(AgentResourceKinds.Agent, root.GetProperty("kind").GetString());
        Assert.AreEqual("assistant", root.GetProperty("metadata").GetProperty("name").GetString());
        Assert.AreEqual("platform", root.GetProperty("metadata").GetProperty("tags").GetProperty("team").GetString());
        Assert.AreEqual("engineering", root.GetProperty("metadata").GetProperty("annotations").GetProperty("owner").GetString());
        Assert.AreEqual("reasoning-default", root.GetProperty("definition").GetProperty("modelProfile").GetProperty("name").GetString());
        Assert.AreEqual("maf-builtin", root.GetProperty("definition").GetProperty("runtimeProfile").GetProperty("name").GetString());
        Assert.AreEqual("default", root.GetProperty("definition").GetProperty("runtimeProfile").GetProperty("namespace").GetString());
        Assert.IsFalse(root.TryGetProperty("id", out _));
        Assert.IsFalse(root.TryGetProperty("type", out _));
        Assert.IsFalse(root.TryGetProperty("resourceGroup", out _));
        Assert.IsFalse(root.TryGetProperty("location", out _));
        Assert.IsFalse(root.TryGetProperty("properties", out _));
    }

    [TestMethod]
    public void ResourceReferenceRoundTripsNameAndOptionalScope()
    {
        var local = JsonSerializer.Deserialize<ResourceReference>("{\"name\":\"profile-a\"}", JsonOptions);
        var remote = JsonSerializer.Deserialize<ResourceReference>("{\"name\":\"profile-b\",\"scopeRef\":\"/tenants/11111111-1111-1111-1111-111111111111\"}", JsonOptions);

        Assert.IsNotNull(local);
        Assert.AreEqual("profile-a", local.Name);
        Assert.IsNull(local.ScopeRef);
        Assert.IsNotNull(remote);
        Assert.AreEqual("profile-b", remote.Name);
        Assert.AreEqual(ResourceScopeRef.Tenant(Guid.Parse("11111111-1111-1111-1111-111111111111")), remote.ScopeRef);
    }

    [TestMethod]
    public void GovernanceClassifiesAllSupportedOriginsWithoutBootstrap()
    {
        var values = new[]
        {
            ResourceGovernance.User,
            ResourceGovernance.Core("agentstration.core", "1.0.0"),
            ResourceGovernance.Pack("/instances/local/InstalledPack/sample", "publisher/sample", "1.0.0"),
            ResourceGovernance.Extension("/instances/local/ExtensionInstallation/sample", "publisher/sample", "1.0.0")
        };

        CollectionAssert.AreEqual(
            new[] { ResourceOriginKind.User, ResourceOriginKind.BuiltIn, ResourceOriginKind.Pack, ResourceOriginKind.Extension },
            values.Select(value => value.Origin.Kind).ToArray());
        Assert.IsFalse(Enum.GetNames<ResourceOriginKind>().Contains("Bootstrap", StringComparer.Ordinal));
    }

    [TestMethod]
    public void AgentResourceRoundTripsThroughCanonicalYaml()
    {
        var expected = Agent("assistant") with
        {
            Uid = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Metadata = new ResourceMetadata
            {
                Name = "assistant",
                Tags = new Dictionary<string, string> { ["tier"] = "internal" },
                Annotations = new Dictionary<string, string> { ["description"] = "YAML test" }
            }
        };

        var yaml = ResourceManifestSerializer.ToYaml(expected);
        var actual = ResourceManifestSerializer.FromYaml<AgentResource>(yaml);

        StringAssert.Contains(yaml, "apiVersion: agentstration.io/v1");
        StringAssert.Contains(yaml, "definition:");
        Assert.AreEqual(expected.Uid, actual.Uid);
        Assert.AreEqual(expected.Metadata.Name, actual.Metadata.Name);
        Assert.AreEqual("reasoning-default", actual.Definition.ModelProfile.Name);
        Assert.AreEqual("maf-builtin", actual.Definition.RuntimeProfile.Name);
        Assert.AreEqual(ResourceNamespace.Default, actual.Definition.RuntimeProfile.Namespace);
        Assert.AreEqual("internal", actual.Metadata.Tags["tier"]);
    }

    [TestMethod]
    public void CompilerIsDeterministicAndOrdersLogicalToolNames()
    {
        var compiler = new AgentDefinitionCompiler();
        var agent = Agent("assistant") with
        {
            Uid = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Generation = 3,
            Metadata = new ResourceMetadata
            {
                Name = "assistant",
                Namespace = new ResourceNamespace("agentstration.sample-pack")
            },
            Definition = Agent("assistant").Definition with
            {
                Instructions = "  Answer carefully.\r\nUse sources.  ",
                Tools = [new("zeta"), new("alpha"), new("alpha")]
            }
        };
        var spec = LocalSpec();

        var first = compiler.Compile(agent, spec);
        var second = compiler.Compile(agent, spec);

        Assert.AreEqual(first.DefinitionHash, second.DefinitionHash);
        Assert.AreEqual("Answer carefully.\nUse sources.", first.EffectiveInstructions);
        CollectionAssert.AreEqual(new[] { "agentstration.sample-pack/alpha", "agentstration.sample-pack/zeta" }, first.EffectiveToolNames.ToArray());
        Assert.AreEqual("reasoning-default", first.ModelProfileName);
        Assert.AreEqual(new ResourceNamespace("agentstration.sample-pack"), first.ModelProfileNamespace);
        Assert.AreEqual("maf-builtin", first.RuntimeProfileName);
        Assert.AreEqual(ResourceNamespace.Default, first.RuntimeProfileNamespace);
    }

    [TestMethod]
    public void CompilerIncludesEffectiveInvocationContractsInImmutableRevision()
    {
        var compiler = new AgentDefinitionCompiler();
        var input = JsonSerializer.SerializeToElement(new { type = "object", properties = new { prompt = new { type = "string" } } });
        var output = JsonSerializer.SerializeToElement(new { type = "object", properties = new { answer = new { type = "string" } } });
        var agent = Agent("contract-agent") with
        {
            Uid = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Generation = 2,
            Definition = Agent("contract-agent").Definition with
            {
                InputSchema = input,
                InputSchemaBinding = new SchemaBinding
                {
                    Reference = new() { Name = "agent-input", Version = "1.0.0" }
                },
                OutputSchema = output
            }
        };

        var resolved = compiler.Compile(agent, LocalSpec());
        var withoutContract = compiler.Compile(agent with
        {
            Definition = agent.Definition with
            {
                InputSchema = null,
                InputSchemaBinding = null,
                OutputSchema = null
            }
        }, LocalSpec());

        Assert.AreEqual("string", resolved.InputSchema?.GetProperty("properties").GetProperty("prompt").GetProperty("type").GetString());
        Assert.AreEqual("string", resolved.OutputSchema?.GetProperty("properties").GetProperty("answer").GetProperty("type").GetString());
        Assert.AreNotEqual(withoutContract.DefinitionHash, resolved.DefinitionHash);
    }

    [TestMethod]
    public async Task SqliteStoreGeneratesAndPreservesImmutableUid()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var created = await fixture.Store.PutAsync(Agent("assistant"), null, true, default);
        var updated = await fixture.Store.PutAsync(
            created.Value with { Definition = created.Value.Definition with { Description = "updated" } },
            created.ETag,
            false,
            default);

        Assert.AreNotEqual(Guid.Empty, created.Value.Uid);
        Assert.AreEqual(created.Value.Uid, updated.Value.Uid);
        var error = await Assert.ThrowsAsync<ResourceConcurrencyException>(() => fixture.Store.PutAsync(
            updated.Value with { Uid = Guid.NewGuid() }, updated.ETag, false, default));
        StringAssert.Contains(error.Message, "immutable");
    }

    [TestMethod]
    public async Task SqliteStoreEnforcesLogicalIdentityAndRoundTripsMetadata()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var desired = Agent("assistant") with
        {
            Metadata = new ResourceMetadata
            {
                Name = "assistant",
                Tags = new Dictionary<string, string> { ["environment"] = "test" },
                Annotations = new Dictionary<string, string> { ["note"] = "preserve-me" }
            }
        };

        var created = await fixture.Store.PutAsync(desired, null, true, default);
        await Assert.ThrowsAsync<ResourceConcurrencyException>(() => fixture.Store.PutAsync(Agent("assistant"), null, true, default));
        var key = new ResourceKey(AgentResourceKinds.Agent, "assistant");
        var loaded = await fixture.Store.GetAsync<AgentResource>(key, default);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(created.Value.Uid, loaded.Value.Uid);
        Assert.AreEqual("test", loaded.Value.Metadata.Tags["environment"]);
        Assert.AreEqual("preserve-me", loaded.Value.Metadata.Annotations["note"]);
    }

    [TestMethod]
    public async Task SqliteStoreIsolatesHomonymousResourcesByNamespace()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var firstNamespace = new ResourceNamespace("team-a");
        var secondNamespace = new ResourceNamespace("team-b");
        var first = Agent("assistant") with { Metadata = new ResourceMetadata { Namespace = firstNamespace, Name = "assistant" } };
        var second = Agent("assistant") with { Metadata = new ResourceMetadata { Namespace = secondNamespace, Name = "assistant" } };

        await fixture.Store.PutAsync(first, null, true, default);
        await fixture.Store.PutAsync(second, null, true, default);

        var loadedFirst = await fixture.Store.GetAsync<AgentResource>(new ResourceKey(AgentResourceKinds.Agent, "assistant", firstNamespace), default);
        var loadedSecond = await fixture.Store.GetAsync<AgentResource>(new ResourceKey(AgentResourceKinds.Agent, "assistant", secondNamespace), default);
        Assert.AreEqual(firstNamespace, loadedFirst?.Value.Namespace);
        Assert.AreEqual(secondNamespace, loadedSecond?.Value.Namespace);
        Assert.IsNull(await fixture.Store.GetAsync<AgentResource>(new ResourceKey(AgentResourceKinds.Agent, "assistant"), default));

        await fixture.Store.DeleteAsync(new ResourceKey(AgentResourceKinds.Agent, "assistant", firstNamespace), loadedFirst!.ETag, default);
        Assert.IsNull(await fixture.Store.GetAsync<AgentResource>(new ResourceKey(AgentResourceKinds.Agent, "assistant", firstNamespace), default));
        Assert.IsNotNull(await fixture.Store.GetAsync<AgentResource>(new ResourceKey(AgentResourceKinds.Agent, "assistant", secondNamespace), default));
    }

    [TestMethod]
    public async Task SqliteStoreRejectsCallerSuppliedManagedGovernance()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var exception = await Assert.ThrowsExactlyAsync<ResourceConcurrencyException>(() => fixture.Store.PutAsync(
            Agent("forged") with { Governance = ResourceGovernance.Core("agentstration.core") },
            null,
            true,
            default));

        StringAssert.Contains(exception.Message, "resource_governance_reserved");
    }

    [TestMethod]
    public async Task SqliteStoreAllowsOnlyExactManagerToMutateManagedResource()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var v1 = ResourceGovernance.Pack("/instances/local/InstalledPack/sample", "publisher/sample", "1.0.0");
        StoredResource<AgentResource> created;
        using (fixture.Governance.Push(v1))
            created = await fixture.Store.PutAsync(Agent("managed"), null, true, default);

        Assert.AreEqual(v1, created.Value.Governance);
        await Assert.ThrowsExactlyAsync<ResourceConcurrencyException>(() => fixture.Store.PutAsync(
            created.Value with { Definition = created.Value.Definition with { Description = "forbidden" } },
            created.ETag,
            false,
            default));
        await Assert.ThrowsExactlyAsync<ResourceConcurrencyException>(() => fixture.Store.DeleteAsync(
            new(AgentResourceKinds.Agent, "managed"), created.ETag, default));

        var wrong = ResourceGovernance.Pack("/instances/local/InstalledPack/other", "publisher/other", "1.0.0");
        using (fixture.Governance.Push(wrong))
            await Assert.ThrowsExactlyAsync<ResourceConcurrencyException>(() => fixture.Store.PutAsync(created.Value, created.ETag, false, default));

        var v2 = ResourceGovernance.Pack("/instances/local/InstalledPack/sample", "publisher/sample", "2.0.0");
        StoredResource<AgentResource> updated;
        using (fixture.Governance.Push(v2))
            updated = await fixture.Store.PutAsync(
                created.Value with { Definition = created.Value.Definition with { Description = "reconciled" } },
                created.ETag,
                false,
                default);

        Assert.AreEqual("2.0.0", updated.Value.Governance.Origin.Version);
        using (fixture.Governance.Push(v2))
            await fixture.Store.DeleteAsync(new(AgentResourceKinds.Agent, "managed"), updated.ETag, default);

        Assert.IsTrue(fixture.Audit.Events.Any(value => value.Action == SecurityAuditActions.ManagedResourceMutationDenied
            && value.Outcome == SecurityAuditOutcome.Failed
            && value.ReasonCode == "resource_managed"));
        Assert.IsTrue(fixture.Audit.Events.Count(value => value.Action == SecurityAuditActions.ManagedResourceReconciled) >= 3);
    }

    private static AgentResource Agent(string name) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = AgentResourceKinds.Agent,
        Metadata = new ResourceMetadata { Name = name },
        Definition = new AgentProperties
        {
            DisplayName = name,
            Description = "A test agent",
            Instructions = "Answer carefully.",
            ModelProfile = new ResourceReference("reasoning-default")
        }
    };

    private static AgentDeploymentSpec LocalSpec() => new()
    {
        Environment = "local",
        RuntimeProfileName = "maf-builtin",
        HostingMode = AgentHostingMode.InProcess
    };

    private sealed class StoreFixture : IAsyncDisposable
    {
        private readonly string directory;
        private readonly ServiceProvider services;
        public IResourceStore Store { get; }
        public IResourceGovernanceContext Governance { get; }
        public RecordingAuditWriter Audit { get; }

        private StoreFixture(string directory, ServiceProvider services, IResourceStore store, IResourceGovernanceContext governance, RecordingAuditWriter audit)
        {
            this.directory = directory;
            this.services = services;
            Store = store;
            Governance = governance;
            Audit = audit;
        }

        public static async Task<StoreFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"agentstration-resource-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var audit = new RecordingAuditWriter();
            var services = new ServiceCollection()
                .AddSingleton(TimeProvider.System)
                .AddSingleton<ICurrentRequestContext, SystemOperationRequestContext>()
                .AddSingleton<ISecurityAuditWriter>(audit)
                .AddSqliteControlPlane($"Data Source={Path.Combine(directory, "control-plane.db")};Pooling=False")
                .BuildServiceProvider();
            var store = services.GetRequiredService<IResourceStore>();
            await store.InitializeAsync(default);
            return new StoreFixture(directory, services, store, services.GetRequiredService<IResourceGovernanceContext>(), audit);
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingAuditWriter : ISecurityAuditWriter
    {
        public List<SecurityAuditWrite> Events { get; } = [];

        public Task WriteAsync(SecurityAuditWrite entry, CancellationToken cancellationToken)
        {
            Events.Add(entry);
            return Task.CompletedTask;
        }
    }
}
