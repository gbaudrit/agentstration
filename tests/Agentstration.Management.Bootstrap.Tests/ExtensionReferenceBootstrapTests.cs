using System.Text.Json;
using Agentstration.Bootstrap.Contracts;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Management.Tests;

public sealed partial class DeclarativeBootstrapTests
{
    [TestMethod]
    public async Task PublicExtensionSelectorsResolveWithoutMutatingDuringPreviewAndPinTheirIdentity()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "profile.yaml"), Profile(Path.GetFileName(directory.Path), "instance"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "10-consumer.yaml"), Resource("ExtensionConsumer", "consumer", "{\"extension\":{\"id\":\"extension.public\"}}"));
        var resolver = new RecordingExtensionResolver();
        var handler = new ExtensionConsumerHandler();
        var service = ExtensionService(directory.Path, handler, resolver);
        var selection = new BootstrapProfileSelection([Path.GetFileName(directory.Path)]);
        var preview = await service.PreviewAsync(selection, default);
        Assert.IsTrue(preview.CanApply);
        Assert.AreEqual("actual-local-registration", handler.Planned.GetProperty("extension").GetProperty("name").GetString());
        Assert.AreEqual(0, handler.Applied);
        resolver.Revision = "changed";
        Assert.AreNotEqual(preview.Digest, (await service.PreviewAsync(selection, default)).Digest);
        var execution = await service.ExecuteAsync(selection, default);
        Assert.IsNull(execution.Error);
        Assert.AreEqual(1, handler.Applied);
    }

    [TestMethod]
    public async Task ContributionBindingsResolveOnlyUsedRequirementsAndReplacePackTargetsToo()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "profile.yaml"), $$"""
            apiVersion: agentstration.io/v1
            kind: BootstrapProfile
            metadata:
              name: {{Path.GetFileName(directory.Path)}}
            definition:
              targetScope: instance
              bindings:
                - name: search
                  targetKind: tool
                  extensionTarget:
                    extensionId: extension.public
                    kind: Tool
                    contributionId: search
                - name: unused
                  targetKind: tool
                  extensionTarget:
                    extensionId: extension.absent
                    kind: Tool
                    contributionId: unused
            """);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "10-consumer.yaml"), Resource("ExtensionConsumer", "consumer",
            "{\"bindings\":[{\"name\":\"pack-search\",\"target\":{\"binding\":\"search\"}}]}"));
        var resolver = new RecordingExtensionResolver();
        var handler = new ExtensionConsumerHandler();
        var service = ExtensionService(directory.Path, handler, resolver);
        var selection = new BootstrapProfileSelection([Path.GetFileName(directory.Path)]);
        var preview = await service.PreviewAsync(selection, default);
        Assert.IsTrue(preview.CanApply);
        Assert.HasCount(1, resolver.SelectedContributions);
        Assert.AreEqual("search", resolver.SelectedContributions.Single().ContributionId);
        Assert.AreEqual("actual-search", handler.Planned.GetProperty("bindings")[0].GetProperty("target").GetProperty("name").GetString());
        Assert.AreEqual(0, handler.Applied);
    }

    [TestMethod]
    public async Task PublicContributionResolutionUsesProvenanceAndPreservesNativeTypePins()
    {
        using var directory = new TemporaryDirectory();
        await using var factory = Factory(directory.Path, InitialPassword);
        using var client = factory.CreateClient();
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var registration = await store.PutExactAsync(ResourceScopeRef.Instance, new ExtensionRegistrationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ExtensionKinds.ExtensionRegistration,
            Metadata = new() { Name = "connection-with-local-name" }, ScopeRef = ResourceScopeRef.Instance,
            Definition = new() { DisplayName = "Public extension", Endpoint = new("https://public.example/"), ExpectedExtensionId = "extension.public" }
        }, null, true, default);
        var authority = ResourceGovernance.Extension("/instance/ExtensionRegistration/default/connection-with-local-name", "extension.public", "1.0.0");
        StoredResource<ConnectedServiceTypeResource> type;
        using (factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(authority))
        {
        type = await store.PutExactAsync(ResourceScopeRef.Instance, new ConnectedServiceTypeResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
            Metadata = new() { Name = "public-type", Namespace = new("extension.public") }, ScopeRef = ResourceScopeRef.Instance,
            Governance = authority,
            Definition = new()
            {
                DisplayName = "Public type", PreferredVersion = "1.0.0",
                Versions = [new("1.0.0", "schema-digest", JsonSerializer.SerializeToElement(new { type = "object" }), [], [])]
            }
        }, null, true, default);
        }
        var resolver = factory.Services.GetRequiredService<IBootstrapExtensionReferenceResolver>();
        var resolved = await resolver.ResolveContributionAsync(new()
        {
            ExtensionId = "extension.public", Kind = "ConnectedServiceType", ContributionId = "public-type"
        }, ResourceScopeRef.Instance, default);
        Assert.AreEqual(type.Value.Name, resolved.Reference.Name);
        Assert.AreEqual(type.Value.Uid.ToString(), resolved.TargetValue!.Value.GetProperty("typeUid").GetString());
        Assert.AreEqual("schema-digest", resolved.TargetValue.Value.GetProperty("schemaDigest").GetString());
        _ = await store.PutExactAsync(ResourceScopeRef.Instance, registration.Value with
        {
            Metadata = new() { Name = "second-installation" }, Uid = Guid.Empty, ETag = null
        }, null, true, default);
        var ambiguous = await Assert.ThrowsAsync<BootstrapDependencyException>(() => resolver.ResolveExtensionAsync(
            new() { Id = "extension.public" }, ResourceScopeRef.Instance, default));
        Assert.AreEqual("extension_ambiguous", ambiguous.Code);
    }

    private static DeclarativeBootstrapService ExtensionService(string profilePath,
        IBootstrapResourceHandler handler, IBootstrapExtensionReferenceResolver resolver)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:Bootstrap:Path"] = Directory.GetParent(profilePath)!.FullName
        }).Build();
        return new(configuration, new BootstrapProfileCatalog(configuration, new TestHostEnvironment(profilePath)),
            [handler], NullLogger<DeclarativeBootstrapService>.Instance, extensionReferences: resolver);
    }

    private static string Resource(string kind, string name, string definition) =>
        Resource(kind, name).Replace("definition: {}", $"definition: {definition}", StringComparison.Ordinal);

    private sealed class ExtensionConsumerHandler : IBootstrapResourceHandler
    {
        public string Kind => "ExtensionConsumer";
        public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
        public IReadOnlyList<string> ExtensionReferencePaths => ["extension"];
        public JsonElement Planned { get; private set; }
        public int Applied { get; private set; }
        public Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken cancellationToken)
        {
            Planned = resource.Definition;
            return Task.FromResult(new BootstrapResourcePlanResult(BootstrapResourceDisposition.Create));
        }
        public Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation, CancellationToken cancellationToken)
        {
            Applied++;
            return Task.FromResult(BootstrapResourceApplyResult.Created);
        }
    }

    private sealed class RecordingExtensionResolver : IBootstrapExtensionReferenceResolver
    {
        public string Revision { get; set; } = "initial";
        public List<BootstrapContributionSelector> SelectedContributions { get; } = [];
        public Task<BootstrapResolvedReference> ResolveExtensionAsync(BootstrapExtensionSelector selector,
            ResourceScopeRef consumerScope, CancellationToken cancellationToken) =>
            Task.FromResult(new BootstrapResolvedReference(new("actual-local-registration", consumerScope), Revision));
        public Task<BootstrapResolvedReference> ResolveContributionAsync(BootstrapContributionSelector selector,
            ResourceScopeRef consumerScope, CancellationToken cancellationToken)
        {
            SelectedContributions.Add(selector);
            return Task.FromResult(new BootstrapResolvedReference(new("actual-search", consumerScope, new("extension.public")), Revision));
        }
    }
}
