using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentstration.Application.Work;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Infrastructure.Packs;

public sealed class WorkspacePackResourceCatalog(
    IControlPlaneStore store,
    FlowService flows,
    IWorkplaceRepository workplace,
    IWorkplaceContext workplaceContext) : IPackWorkspaceResourceCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<IReadOnlyList<PackCompositionCatalogItem>> ListAsync(CancellationToken cancellationToken)
    {
        var resources = new List<PackCompositionCatalogItem>();
        resources.AddRange((await store.ListAsync<AgentResource>(ResourceNamespace.Default, ResourceKinds.Agent, 0, 1000, cancellationToken))
            .Select(value => AgentItem(value.Value)));
        resources.AddRange((await flows.ListAsync(workplaceContext.WorkspaceId, ResourceNamespace.Default, 0, 1000, cancellationToken)).Items
            .Select(value => FlowItem(value.Value)));
        resources.AddRange((await workplace.ListEntryDraftsAsync(workplaceContext.WorkspaceId, cancellationToken))
            .Where(value => value.Id.Namespace.IsDefault)
            .Select(EntryItem));
        resources.AddRange((await store.ListAsync<ModelProfileResource>(ResourceNamespace.Default, ResourceKinds.ModelProfile, 0, 1000, cancellationToken))
            .Select(value => ModelProfileItem(value.Value)));
        resources.AddRange((await store.ListAsync<ModelProviderResource>(ResourceNamespace.Default, ResourceKinds.ModelProvider, 0, 1000, cancellationToken))
            .Select(value => ModelProviderItem(value.Value)));
        resources.AddRange((await store.ListAsync<RuntimeProfileResource>(ResourceNamespace.Default, ResourceKinds.RuntimeProfile, 0, 1000, cancellationToken))
            .Select(value => RuntimeProfileItem(value.Value)));
        resources.AddRange((await store.ListAsync<SecretResource>(ResourceNamespace.Default, ResourceKinds.Secret, 0, 1000, cancellationToken))
            .Select(value => BindingItem(value.Value, value.Value.Definition.DisplayName, "Secrets are converted to installation bindings; their values are never exported.")));
        await AddUnsupportedAsync<VaultResource>(resources, ResourceKinds.Vault, "Vaults and their configuration are never copied into a Pack.", cancellationToken);
        await AddUnsupportedAsync<ToolProviderResource>(resources, ResourceKinds.ToolProvider, "Tool Providers are not yet exportable by the Composer.", cancellationToken);
        await AddUnsupportedAsync<ToolResource>(resources, ResourceKinds.Tool, "Tools are not yet exportable by the Composer.", cancellationToken);
        return resources
            .OrderBy(value => KindOrder(value.Resource.Kind))
            .ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<PackCompositionResourceSnapshot?> GetAsync(
        PackCompositionResourceKey resource,
        CancellationToken cancellationToken)
    {
        if (!resource.NamespaceValue.IsDefault) return null;
        return resource.Kind switch
        {
            ResourceKinds.Agent => await GetAgentAsync(resource, cancellationToken),
            ResourceKinds.Flow => await GetFlowAsync(resource, cancellationToken),
            ResourceKinds.Entry => await GetEntryAsync(resource, cancellationToken),
            ResourceKinds.ModelProfile => await GetModelProfileAsync(resource, cancellationToken),
            ResourceKinds.ModelProvider => await GetModelProviderAsync(resource, cancellationToken),
            ResourceKinds.RuntimeProfile => await GetRuntimeProfileAsync(resource, cancellationToken),
            ResourceKinds.Secret => await GetBindingAsync<SecretResource>(resource, PackBindingTargetKind.Secret, cancellationToken),
            _ => (await ListAsync(cancellationToken)).Where(value => value.Resource.Address == resource.Address).Select(value => new PackCompositionResourceSnapshot(value, [])).SingleOrDefault()
        };
    }

    public async Task<JsonElement> ExportAsync(
        PackCompositionResourceKey resource,
        IReadOnlyDictionary<ResourceAddress, string> bindings,
        CancellationToken cancellationToken) => resource.Kind switch
        {
            ResourceKinds.Agent => await ExportAgentAsync(resource, bindings, cancellationToken),
            ResourceKinds.Flow => await ExportFlowAsync(resource, bindings, cancellationToken),
            ResourceKinds.Entry => await ExportEntryAsync(resource, cancellationToken),
            ResourceKinds.ModelProfile => await ExportModelProfileAsync(resource, bindings, cancellationToken),
            ResourceKinds.ModelProvider => await ExportModelProviderAsync(resource, bindings, cancellationToken),
            ResourceKinds.RuntimeProfile => await ExportRuntimeProfileAsync(resource, cancellationToken),
            _ => throw new InvalidOperationException($"Resource kind '{resource.Kind}' is not exportable by the Pack Composer.")
        };

    private async Task<PackCompositionResourceSnapshot?> GetAgentAsync(PackCompositionResourceKey key, CancellationToken token)
    {
        var stored = await store.GetAsync<AgentResource>(ResourceKey.Create(ResourceKinds.Agent, key.Name, key.NamespaceValue), token);
        if (stored is null) return null;
        var agent = stored.Value;
        var dependencies = new List<PackCompositionDependency>
        {
            BindingDependency(agent.Definition.ModelProfile, agent.Namespace, ResourceKinds.ModelProfile, PackBindingTargetKind.ModelProfile, "modelProfile"),
            BindingDependency(agent.Definition.RuntimeProfile, agent.Namespace, ResourceKinds.RuntimeProfile, PackBindingTargetKind.RuntimeProfile, "runtimeProfile")
        };
        dependencies.AddRange(agent.Definition.Tools.Select(tool => UnsupportedDependency(tool, agent.Namespace, ResourceKinds.Tool, "tool")));
        return new(AgentItem(agent) with { DependencyCount = dependencies.Count }, dependencies);
    }

    private async Task<PackCompositionResourceSnapshot?> GetFlowAsync(PackCompositionResourceKey key, CancellationToken token)
    {
        var flow = (await flows.GetAsync(workplaceContext.WorkspaceId, new(key.Name, key.NamespaceValue), token))?.Value;
        if (flow is null) return null;
        var dependencies = FlowDependencies(flow).DistinctBy(value => (value.Target.Address, value.Relationship)).ToArray();
        return new(FlowItem(flow) with { DependencyCount = dependencies.Length }, dependencies);
    }

    private async Task<PackCompositionResourceSnapshot?> GetEntryAsync(PackCompositionResourceKey key, CancellationToken token)
    {
        var entry = await workplace.GetEntryDraftAsync(workplaceContext.WorkspaceId, new(key.Name, key.NamespaceValue), token);
        if (entry is null) return null;
        var dependencies = new List<PackCompositionDependency>
        {
            IncludeDependency(entry.Binding.ResourceId, entry.Binding.Namespace ?? entry.Id.Namespace, ResourceKinds.Flow, "flow")
        };
        if (entry.Behavior.Conversation?.ContinuationTarget is { } continuation)
            dependencies.Add(IncludeDependency(continuation.FlowResourceId, continuation.Namespace, ResourceKinds.Flow, "continuationFlow"));
        return new(EntryItem(entry) with { DependencyCount = dependencies.Count }, dependencies);
    }

    private async Task<PackCompositionResourceSnapshot?> GetModelProfileAsync(PackCompositionResourceKey key, CancellationToken token)
    {
        var stored = await store.GetAsync<ModelProfileResource>(ResourceKey.Create(ResourceKinds.ModelProfile, key.Name, key.NamespaceValue), token);
        if (stored is null) return null;
        var profile = stored.Value;
        var dependencies = new[]
        {
            BindingDependency(profile.Definition.Provider, profile.Namespace, ResourceKinds.ModelProvider, PackBindingTargetKind.ModelProvider, "provider")
        };
        return new(ModelProfileItem(profile) with { DependencyCount = dependencies.Length }, dependencies);
    }

    private async Task<PackCompositionResourceSnapshot?> GetModelProviderAsync(PackCompositionResourceKey key, CancellationToken token)
    {
        var stored = await store.GetAsync<ModelProviderResource>(ResourceKey.Create(ResourceKinds.ModelProvider, key.Name, key.NamespaceValue), token);
        if (stored is null) return null;
        var provider = stored.Value;
        var dependencies = provider.Definition.Credential is { } credential
            ? new[] { BindingDependency(credential, provider.Namespace, ResourceKinds.Secret, PackBindingTargetKind.Secret, "credential") }
            : [];
        return new(ModelProviderItem(provider) with { DependencyCount = dependencies.Length }, dependencies);
    }

    private async Task<PackCompositionResourceSnapshot?> GetRuntimeProfileAsync(PackCompositionResourceKey key, CancellationToken token)
    {
        var stored = await store.GetAsync<RuntimeProfileResource>(ResourceKey.Create(ResourceKinds.RuntimeProfile, key.Name, key.NamespaceValue), token);
        return stored is null ? null : new(RuntimeProfileItem(stored.Value), []);
    }

    private async Task<PackCompositionResourceSnapshot?> GetBindingAsync<T>(
        PackCompositionResourceKey key,
        PackBindingTargetKind targetKind,
        CancellationToken token) where T : Resource
    {
        var stored = await store.GetAsync<T>(ResourceKey.Create(key.Kind, key.Name, key.NamespaceValue), token);
        if (stored is null) return null;
        var displayName = stored.Value switch
        {
            ModelProfileResource profile => profile.Definition.DisplayName,
            SecretResource secret => secret.Definition.DisplayName,
            _ => stored.Value.Name
        };
        return new(BindingItem(stored.Value, displayName, $"This resource is emitted as a {BindingLabel(targetKind)} binding."), []);
    }

    private async Task<JsonElement> ExportAgentAsync(
        PackCompositionResourceKey key,
        IReadOnlyDictionary<ResourceAddress, string> bindings,
        CancellationToken token)
    {
        var agent = (await store.GetAsync<AgentResource>(ResourceKey.Create(ResourceKinds.Agent, key.Name, key.NamespaceValue), token))?.Value
            ?? throw new KeyNotFoundException($"Agent '{key.Name}' was not found.");
        var clean = agent with
        {
            Uid = Guid.Empty,
            TenantId = Guid.Empty,
            WorkspaceId = Guid.Empty,
            Generation = 1,
            ETag = null,
            Metadata = CleanMetadata(agent.Metadata),
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Accepted }
        };
        var node = JsonSerializer.SerializeToNode(clean, JsonOptions)!.AsObject();
        var definition = node["definition"]!.AsObject();
        var modelProfile = agent.Definition.ModelProfile.Resolve(agent.Namespace, ResourceKinds.ModelProfile);
        var runtimeProfile = agent.Definition.RuntimeProfile.Resolve(agent.Namespace, ResourceKinds.RuntimeProfile);
        definition["modelProfile"] = ReferenceNode(bindings, modelProfile);
        definition["runtimeProfile"] = ReferenceNode(bindings, runtimeProfile);
        return ToElement(node);
    }

    private async Task<JsonElement> ExportFlowAsync(
        PackCompositionResourceKey key,
        IReadOnlyDictionary<ResourceAddress, string> bindings,
        CancellationToken token)
    {
        var flow = (await flows.GetAsync(workplaceContext.WorkspaceId, new(key.Name, key.NamespaceValue), token))?.Value
            ?? throw new KeyNotFoundException($"Flow '{key.Name}' was not found.");
        var envelope = new PackResourceEnvelope<PackFlowDefinition>
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Flow,
            Metadata = new ResourceMetadata { Name = flow.Name },
            Definition = new PackFlowDefinition
            {
                DisplayName = flow.DisplayName,
                Description = flow.Description,
                Version = flow.Version,
                Enabled = flow.Enabled,
                Spec = flow.Definition,
                Metadata = WithoutProvenance(flow.Metadata),
                Graph = flow.Graph,
                Publish = true,
                Activate = true
            }
        };
        var node = JsonSerializer.SerializeToNode(envelope, JsonOptions)!.AsObject();
        if (flow.Graph is not null)
        {
            var steps = node["definition"]?["graph"]?["steps"]?.AsArray();
            for (var index = 0; index < flow.Graph.Steps.Count; index++)
            {
                if (flow.Graph.Steps[index] is not AgentFlowStepDefinition { ModelProfileOverride: { } profile }) continue;
                var target = ResourceAddress.Create(profile.Namespace ?? flow.Id.Namespace, ResourceKinds.ModelProfile, profile.ResourceId);
                steps![index]!.AsObject()["modelProfileOverride"] = ReferenceNode(bindings, target);
            }
        }
        return ToElement(node);
    }

    private async Task<JsonElement> ExportModelProfileAsync(
        PackCompositionResourceKey key,
        IReadOnlyDictionary<ResourceAddress, string> bindings,
        CancellationToken token)
    {
        var profile = (await store.GetAsync<ModelProfileResource>(ResourceKey.Create(ResourceKinds.ModelProfile, key.Name, key.NamespaceValue), token))?.Value
            ?? throw new KeyNotFoundException($"Model Profile '{key.Name}' was not found.");
        var clean = profile with
        {
            Uid = Guid.Empty,
            TenantId = Guid.Empty,
            WorkspaceId = Guid.Empty,
            Generation = 1,
            ETag = null,
            Metadata = CleanMetadata(profile.Metadata),
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Accepted }
        };
        var node = JsonSerializer.SerializeToNode(clean, JsonOptions)!.AsObject();
        var target = profile.Definition.Provider.Resolve(profile.Namespace, ResourceKinds.ModelProvider);
        node["definition"]!.AsObject()["provider"] = ReferenceNode(bindings, target);
        return ToElement(node);
    }

    private async Task<JsonElement> ExportModelProviderAsync(
        PackCompositionResourceKey key,
        IReadOnlyDictionary<ResourceAddress, string> bindings,
        CancellationToken token)
    {
        var provider = (await store.GetAsync<ModelProviderResource>(ResourceKey.Create(ResourceKinds.ModelProvider, key.Name, key.NamespaceValue), token))?.Value
            ?? throw new KeyNotFoundException($"Model Provider '{key.Name}' was not found.");
        var clean = provider with
        {
            Uid = Guid.Empty,
            TenantId = Guid.Empty,
            WorkspaceId = Guid.Empty,
            Generation = 1,
            ETag = null,
            Metadata = CleanMetadata(provider.Metadata),
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Accepted }
        };
        var node = JsonSerializer.SerializeToNode(clean, JsonOptions)!.AsObject();
        if (provider.Definition.Credential is { } credential)
        {
            var target = credential.Resolve(provider.Namespace, ResourceKinds.Secret);
            node["definition"]!.AsObject()["credential"] = BindingNode(bindings, target);
        }
        return ToElement(node);
    }

    private async Task<JsonElement> ExportRuntimeProfileAsync(PackCompositionResourceKey key, CancellationToken token)
    {
        var runtime = (await store.GetAsync<RuntimeProfileResource>(ResourceKey.Create(ResourceKinds.RuntimeProfile, key.Name, key.NamespaceValue), token))?.Value
            ?? throw new KeyNotFoundException($"Runtime Profile '{key.Name}' was not found.");
        var clean = runtime with
        {
            Uid = Guid.Empty,
            TenantId = Guid.Empty,
            WorkspaceId = Guid.Empty,
            Generation = 1,
            ETag = null,
            Metadata = CleanMetadata(runtime.Metadata),
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Accepted }
        };
        return JsonSerializer.SerializeToElement(clean, JsonOptions);
    }

    private async Task<JsonElement> ExportEntryAsync(PackCompositionResourceKey key, CancellationToken token)
    {
        var entry = await workplace.GetEntryDraftAsync(workplaceContext.WorkspaceId, new(key.Name, key.NamespaceValue), token)
            ?? throw new KeyNotFoundException($"Entry '{key.Name}' was not found.");
        var envelope = new PackResourceEnvelope<PackEntryDefinition>
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Entry,
            Metadata = new ResourceMetadata { Name = entry.Name },
            Definition = new PackEntryDefinition
            {
                DisplayName = entry.DisplayName,
                Description = entry.Description,
                Presentation = entry.Presentation,
                Binding = Relative(entry.Binding, entry.Id.Namespace),
                Behavior = entry.Behavior,
                Publish = true
            }
        };
        return JsonSerializer.SerializeToElement(envelope, JsonOptions);
    }

    private IEnumerable<PackCompositionDependency> FlowDependencies(FlowResource flow)
    {
        foreach (var target in DefinitionTargets(flow.Definition))
            if (!Dynamic(target.Id)) yield return IncludeDependency(target.Id, target.Namespace ?? flow.Id.Namespace, target.Kind == FlowTargetKind.Agent ? ResourceKinds.Agent : ResourceKinds.Flow, "flowTarget");
        if (flow.Graph is null) yield break;
        foreach (var step in flow.Graph.Steps)
        {
            if (step is AgentFlowStepDefinition agent)
            {
                if (!Dynamic(agent.Agent.ResourceId)) yield return IncludeDependency(agent.Agent.ResourceId, agent.Agent.Namespace ?? flow.Id.Namespace, ResourceKinds.Agent, "graphAgent");
                if (agent.ModelProfileOverride is { } profile && !Dynamic(profile.ResourceId))
                    yield return BindingDependency(new(profile.ResourceId, @namespace: profile.Namespace), flow.Id.Namespace, ResourceKinds.ModelProfile, PackBindingTargetKind.ModelProfile, "modelProfileOverride");
            }
            else if (step is RouterFlowStepDefinition router)
            {
                foreach (var candidate in router.Candidates.Where(candidate => !Dynamic(candidate.Agent.ResourceId)))
                    yield return IncludeDependency(candidate.Agent.ResourceId, candidate.Agent.Namespace ?? flow.Id.Namespace, ResourceKinds.Agent, "routerAgent");
                if (router.Fallback is { } fallback && !Dynamic(fallback.ResourceId))
                    yield return IncludeDependency(fallback.ResourceId, fallback.Namespace ?? flow.Id.Namespace, ResourceKinds.Agent, "routerFallback");
            }
        }
    }

    private static IEnumerable<FlowTargetReference> DefinitionTargets(FlowDefinition definition) => definition switch
    {
        DirectFlowDefinition direct => [direct.Target],
        RoutingFlowDefinition routing => routing.Destinations.Concat(routing.Fallback is null ? [] : [routing.Fallback]),
        WorkflowFlowDefinition workflow => workflow.Nodes.Where(node => node.Target is not null).Select(node => node.Target!),
        OrchestrationFlowDefinition orchestration => orchestration.Participants.Concat(orchestration.Pattern is MagenticOrchestrationPattern magentic ? [magentic.Manager] : []),
        CompositeFlowDefinition composite => composite.Flows.Select(flow => new FlowTargetReference(FlowTargetKind.Flow, flow.FlowId.Value, flow.Version, flow.Namespace ?? flow.FlowId.Namespace)),
        _ => []
    };

    private async Task AddUnsupportedAsync<T>(ICollection<PackCompositionCatalogItem> target, string kind, string reason, CancellationToken token) where T : Resource
    {
        foreach (var stored in await store.ListAsync<T>(ResourceNamespace.Default, kind, 0, 1000, token))
            target.Add(new PackCompositionCatalogItem { Resource = new(kind, stored.Value.Name), DisplayName = DisplayName(stored.Value), Availability = PackCompositionAvailability.Unsupported, AvailabilityReason = reason });
    }

    private static PackCompositionCatalogItem AgentItem(AgentResource value) => new() { Resource = new(ResourceKinds.Agent, value.Name, value.Namespace), DisplayName = value.Definition.DisplayName, Description = value.Definition.Description, Version = value.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), Status = value.Status.ProvisioningState.ToString() };
    private static PackCompositionCatalogItem FlowItem(FlowResource value) => new() { Resource = new(ResourceKinds.Flow, value.Name, value.Id.Namespace), DisplayName = value.DisplayName ?? value.Name, Description = value.Description, Version = value.ActiveVersion ?? value.Version, Status = value.ActiveVersion is null ? "Draft" : "Published" };
    private static PackCompositionCatalogItem EntryItem(EntryDraft value) => new() { Resource = new(ResourceKinds.Entry, value.Name, value.Id.Namespace), DisplayName = value.DisplayName, Description = value.Description, Version = value.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), Status = value.PublishedBinding is null ? "Draft" : "Published" };
    private static PackCompositionCatalogItem ModelProfileItem(ModelProfileResource value) => new() { Resource = new(ResourceKinds.ModelProfile, value.Name, value.Namespace), DisplayName = value.Definition.DisplayName, Description = value.Definition.Description, Version = value.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), Status = value.Status.ProvisioningState.ToString() };
    private static PackCompositionCatalogItem ModelProviderItem(ModelProviderResource value) => new() { Resource = new(ResourceKinds.ModelProvider, value.Name, value.Namespace), DisplayName = value.Definition.DisplayName, Description = $"{value.Definition.ProviderType} · {value.Definition.Endpoint}", Version = value.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), Status = value.Status.ProvisioningState.ToString() };
    private static PackCompositionCatalogItem RuntimeProfileItem(RuntimeProfileResource value) => new() { Resource = new(ResourceKinds.RuntimeProfile, value.Name, value.Namespace), DisplayName = value.Definition.DisplayName, Description = $"Runtime type: {value.Definition.RuntimeType}", Version = value.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), Status = value.Status.ProvisioningState.ToString() };
    private static PackCompositionCatalogItem BindingItem(Resource value, string displayName, string reason) => new() { Resource = new(value.Kind, value.Name, value.Namespace), DisplayName = displayName, Status = value.Status.ProvisioningState.ToString(), Availability = PackCompositionAvailability.BindingOnly, AvailabilityReason = reason };
    private static PackCompositionDependency IncludeDependency(string name, ResourceNamespace @namespace, string kind, string relationship) => new() { Target = new(kind, name, @namespace), Relationship = relationship };
    private static PackCompositionDependency BindingDependency(ResourceReference reference, ResourceNamespace owner, string kind, PackBindingTargetKind targetKind, string relationship) => new() { Target = new(kind, reference.Name, reference.Namespace ?? owner), Relationship = relationship, Mode = PackCompositionDependencyMode.Binding, BindingTargetKind = targetKind };
    private static PackCompositionDependency UnsupportedDependency(ResourceReference reference, ResourceNamespace owner, string kind, string relationship) => new() { Target = new(kind, reference.Name, reference.Namespace ?? owner), Relationship = relationship, Mode = PackCompositionDependencyMode.Unsupported };
    private static JsonObject BindingNode(IReadOnlyDictionary<ResourceAddress, string> bindings, ResourceAddress target) => new() { ["binding"] = bindings.TryGetValue(target, out var name) ? name : throw new InvalidOperationException($"No Pack binding was generated for '{target}'.") };
    private static JsonNode ReferenceNode(IReadOnlyDictionary<ResourceAddress, string> bindings, ResourceAddress target) =>
        bindings.TryGetValue(target, out var name)
            ? new JsonObject { ["binding"] = name }
            : JsonSerializer.SerializeToNode(new ResourceReference(target.Name), JsonOptions)!;
    private static JsonElement ToElement(JsonNode node) { using var document = JsonDocument.Parse(node.ToJsonString(JsonOptions)); return document.RootElement.Clone(); }
    private static EntryBinding Relative(EntryBinding binding, ResourceNamespace owner) => binding with { Namespace = binding.Namespace is null || binding.Namespace == owner ? null : binding.Namespace };
    private static ResourceMetadata CleanMetadata(ResourceMetadata metadata) => metadata with { Namespace = ResourceNamespace.Default, Annotations = WithoutProvenance(metadata.Annotations) };
    private static IReadOnlyDictionary<string, string> WithoutProvenance(IReadOnlyDictionary<string, string> values) => values.Where(pair => !pair.Key.StartsWith("agentstration.io/pack.", StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    private static string DisplayName(Resource value) => value switch { ModelProviderResource provider => provider.Definition.DisplayName, RuntimeProfileResource runtime => runtime.Definition.DisplayName, VaultResource vault => vault.Definition.DisplayName, ToolProviderResource provider => provider.Definition.DisplayName, ToolResource tool => tool.Definition.DisplayName, _ => value.Name };
    private static bool Dynamic(string value) => value.StartsWith("${", StringComparison.Ordinal);
    private static string BindingLabel(PackBindingTargetKind kind) => kind switch
    {
        PackBindingTargetKind.Secret => "Secret",
        PackBindingTargetKind.ModelProvider => "Model Provider",
        PackBindingTargetKind.RuntimeProfile => "Runtime Profile",
        _ => "Model Profile"
    };
    private static int KindOrder(string kind) => kind switch { ResourceKinds.Entry => 10, ResourceKinds.Flow => 20, ResourceKinds.Agent => 30, ResourceKinds.ModelProfile => 40, ResourceKinds.ModelProvider => 50, ResourceKinds.RuntimeProfile => 60, ResourceKinds.Secret => 70, _ => 100 };
    private static JsonSerializerOptions CreateJsonOptions() { var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }; options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)); return options; }
}
