using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Infrastructure.Packs;

internal static class PackProvenance
{
    public static ResourceMetadata Add(ResourceMetadata metadata, PackIdentity pack, ResourceNamespace @namespace, string version)
    {
        var annotations = new Dictionary<string, string>(metadata.Annotations, StringComparer.Ordinal)
        {
            [PackProvenanceAnnotations.Publisher] = pack.Publisher,
            [PackProvenanceAnnotations.Name] = pack.Name,
            [PackProvenanceAnnotations.Version] = version
        };
        return metadata with { Namespace = @namespace, Annotations = annotations };
    }

    public static IReadOnlyDictionary<string, string> Add(IReadOnlyDictionary<string, string>? metadata, PackIdentity pack, string version)
    {
        var result = metadata is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        result[PackProvenanceAnnotations.Publisher] = pack.Publisher;
        result[PackProvenanceAnnotations.Name] = pack.Name;
        result[PackProvenanceAnnotations.Version] = version;
        return result;
    }
}

public sealed class ModelProviderPackResourceHandler(ModelProviderManagementService service) : IPackResourceHandler
{
    public string Kind => ResourceKinds.ModelProvider;
    public int InstallOrder => 10;
    public Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken) { _ = Parse(resource); return Task.CompletedTask; }
    public async Task<bool> ExistsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => await service.GetAsync(@namespace, name, cancellationToken) is not null;
    public async Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, ResourceNamespace @namespace, string packVersion, CancellationToken cancellationToken)
    {
        var value = Parse(resource); var stored = await service.CreateAsync(value with { Metadata = PackProvenance.Add(value.Metadata, pack, @namespace, packVersion) }, cancellationToken);
        return Managed(resource, @namespace, stored.ETag);
    }
    public async Task<ManagedPackResource> UpdateAsync(PackResourceDocument resource, ManagedPackResource current, PackIdentity pack, string packVersion, CancellationToken cancellationToken)
    {
        var stored = await service.PutAsync(current.Namespace, current.Name, Parse(resource).Definition, current.VersionToken, cancellationToken);
        return Managed(resource, current.Namespace, stored.ETag);
    }
    public async Task<string?> GetVersionTokenAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => (await service.GetAsync(@namespace, name, cancellationToken))?.ETag;
    public Task DeleteAsync(ManagedPackResource resource, PackRemovalOptions options, CancellationToken cancellationToken) => service.DeleteAsync(resource.Namespace, resource.Name, resource.VersionToken, cancellationToken);
    private static ModelProviderResource Parse(PackResourceDocument resource) => ResourceManifestSerializer.FromJson<ModelProviderResource>(resource.Manifest.GetRawText());
    private static ManagedPackResource Managed(PackResourceDocument resource, ResourceNamespace @namespace, string token) => new() { Namespace = @namespace, Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = token };
}

public sealed class RuntimeProfilePackResourceHandler(RuntimeProfileManagementService service) : IPackResourceHandler
{
    public string Kind => ResourceKinds.RuntimeProfile;
    public int InstallOrder => 20;
    public Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken) { _ = Parse(resource); return Task.CompletedTask; }
    public async Task<bool> ExistsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => await service.GetAsync(@namespace, name, cancellationToken) is not null;
    public async Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, ResourceNamespace @namespace, string packVersion, CancellationToken cancellationToken)
    {
        var value = Parse(resource); var stored = await service.CreateAsync(value with { Metadata = PackProvenance.Add(value.Metadata, pack, @namespace, packVersion) }, cancellationToken);
        return Managed(resource, @namespace, stored.ETag);
    }
    public async Task<ManagedPackResource> UpdateAsync(PackResourceDocument resource, ManagedPackResource current, PackIdentity pack, string packVersion, CancellationToken cancellationToken)
    {
        var stored = await service.PutAsync(current.Namespace, current.Name, Parse(resource).Definition, current.VersionToken, cancellationToken);
        return Managed(resource, current.Namespace, stored.ETag);
    }
    public async Task<string?> GetVersionTokenAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => (await service.GetAsync(@namespace, name, cancellationToken))?.ETag;
    public Task DeleteAsync(ManagedPackResource resource, PackRemovalOptions options, CancellationToken cancellationToken) => service.DeleteAsync(resource.Namespace, resource.Name, resource.VersionToken, cancellationToken);
    private static RuntimeProfileResource Parse(PackResourceDocument resource) => ResourceManifestSerializer.FromJson<RuntimeProfileResource>(resource.Manifest.GetRawText());
    private static ManagedPackResource Managed(PackResourceDocument resource, ResourceNamespace @namespace, string token) => new() { Namespace = @namespace, Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = token };
}

public sealed class ModelProfilePackResourceHandler(ModelProfileManagementService service) : IPackResourceHandler
{
    public string Kind => ResourceKinds.ModelProfile;
    public int InstallOrder => 30;
    public Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken) { _ = Parse(resource); return Task.CompletedTask; }
    public async Task<bool> ExistsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => await service.GetAsync(@namespace, name, cancellationToken) is not null;
    public async Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, ResourceNamespace @namespace, string packVersion, CancellationToken cancellationToken)
    {
        var value = Parse(resource); var stored = await service.CreateAsync(value with { Metadata = PackProvenance.Add(value.Metadata, pack, @namespace, packVersion) }, cancellationToken);
        return Managed(resource, @namespace, stored.ETag);
    }
    public async Task<ManagedPackResource> UpdateAsync(PackResourceDocument resource, ManagedPackResource current, PackIdentity pack, string packVersion, CancellationToken cancellationToken)
    {
        var stored = await service.PutAsync(current.Namespace, current.Name, Parse(resource).Definition, current.VersionToken, cancellationToken);
        return Managed(resource, current.Namespace, stored.ETag);
    }
    public async Task<string?> GetVersionTokenAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => (await service.GetAsync(@namespace, name, cancellationToken))?.ETag;
    public Task DeleteAsync(ManagedPackResource resource, PackRemovalOptions options, CancellationToken cancellationToken) => service.DeleteAsync(resource.Namespace, resource.Name, resource.VersionToken, cancellationToken);
    private static ModelProfileResource Parse(PackResourceDocument resource) => ResourceManifestSerializer.FromJson<ModelProfileResource>(resource.Manifest.GetRawText());
    private static ManagedPackResource Managed(PackResourceDocument resource, ResourceNamespace @namespace, string token) => new() { Namespace = @namespace, Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = token };
}

public sealed class AgentPackResourceHandler(AgentManagementService service) : IPackResourceHandler
{
    public string Kind => ResourceKinds.Agent;
    public int InstallOrder => 40;
    public Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken) { _ = Parse(resource); return Task.CompletedTask; }
    public async Task<bool> ExistsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => await service.GetAgentAsync(@namespace, name, cancellationToken) is not null;
    public async Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, ResourceNamespace @namespace, string packVersion, CancellationToken cancellationToken)
    {
        var value = Parse(resource); var stored = await service.PutAgentAsync(value with { Metadata = PackProvenance.Add(value.Metadata, pack, @namespace, packVersion) }, null, true, cancellationToken);
        return Managed(resource, @namespace, stored.ETag);
    }
    public async Task<ManagedPackResource> UpdateAsync(PackResourceDocument resource, ManagedPackResource current, PackIdentity pack, string packVersion, CancellationToken cancellationToken)
    {
        var value = Parse(resource);
        var stored = await service.PutAgentAsync(value with { Metadata = PackProvenance.Add(value.Metadata, pack, current.Namespace, packVersion) }, current.VersionToken, false, cancellationToken);
        return Managed(resource, current.Namespace, stored.ETag);
    }
    public async Task<string?> GetVersionTokenAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => (await service.GetAgentAsync(@namespace, name, cancellationToken))?.ETag;
    public Task DeleteAsync(ManagedPackResource resource, PackRemovalOptions options, CancellationToken cancellationToken) => service.DeleteAgentAsync(resource.Namespace, resource.Name, resource.VersionToken, cancellationToken);
    private static AgentResource Parse(PackResourceDocument resource) => ResourceManifestSerializer.FromJson<AgentResource>(resource.Manifest.GetRawText());
    private static ManagedPackResource Managed(PackResourceDocument resource, ResourceNamespace @namespace, string token) => new() { Namespace = @namespace, Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = token };
}

public sealed record PackFlowDefinition
{
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string Version { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public FlowDefinition Spec { get; init; } = null!;
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public FlowGraphDefinition? Graph { get; init; }
    public bool Publish { get; init; } = true;
    public bool Activate { get; init; } = true;
}

public sealed record PackResourceEnvelope<T>
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public ResourceMetadata Metadata { get; init; } = new();
    public T Definition { get; init; } = default!;
}

public sealed class FlowPackResourceHandler(FlowService service, IFlowDefinitionValidator graphValidator, TimeProvider timeProvider, ICurrentRequestContext requestContext) : IPackResourceHandler
{
    public string Kind => ResourceKinds.Flow;
    public int InstallOrder => 50;
    public async Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken)
    {
        var value = Parse(resource); var now = timeProvider.GetUtcNow();
        FlowValidator.Validate(new(CurrentWorkspaceId(), new(resource.Name), resource.Name, value.Definition.Description, value.Definition.Version, value.Definition.Enabled, null, value.Definition.Spec, value.Definition.Metadata, now, now, value.Definition.DisplayName, value.Definition.Graph));
        if (value.Definition.Graph is not null)
        {
            var validation = await graphValidator.ValidateAsync(value.Definition.Graph, new(ResolveResources: false), cancellationToken);
            var error = validation.Issues.FirstOrDefault(issue => issue.Severity == FlowValidationSeverity.Error);
            if (error is not null) throw new FlowValidationException(error.Code, error.Message);
        }
    }
    public async Task<bool> ExistsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => await service.GetAsync(CurrentWorkspaceId(), new(name, @namespace), cancellationToken) is not null;
    public async Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, ResourceNamespace @namespace, string packVersion, CancellationToken cancellationToken)
    {
        var value = Parse(resource); var definition = value.Definition;
        var workspaceId = CurrentWorkspaceId();
        var stored = await service.CreateAsync(workspaceId, new(resource.Name, definition.Description, definition.Version, definition.Enabled, definition.Spec, PackProvenance.Add(definition.Metadata, pack, packVersion), definition.Graph, definition.DisplayName), @namespace, cancellationToken);
        var flowId = new FlowId(resource.Name, @namespace);
        if (definition.Publish) { _ = await service.PublishVersionAsync(workspaceId, flowId, definition.Version, definition.Activate, cancellationToken); stored = (await service.GetAsync(workspaceId, flowId, cancellationToken))!; }
        return new() { Namespace = @namespace, Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = stored.ETag };
    }
    public async Task<ManagedPackResource> UpdateAsync(PackResourceDocument resource, ManagedPackResource current, PackIdentity pack, string packVersion, CancellationToken cancellationToken)
    {
        var value = Parse(resource); var definition = value.Definition; var workspaceId = CurrentWorkspaceId(); var flowId = new FlowId(current.Name, current.Namespace);
        var stored = await service.UpdateAsync(workspaceId, flowId, new(definition.Description, definition.Version, definition.Enabled, definition.Spec, PackProvenance.Add(definition.Metadata, pack, packVersion), definition.Graph, definition.DisplayName), current.VersionToken, cancellationToken);
        if (definition.Publish && await service.GetVersionAsync(workspaceId, flowId, definition.Version, cancellationToken) is null)
        {
            _ = await service.PublishVersionAsync(workspaceId, flowId, definition.Version, definition.Activate, cancellationToken);
            stored = (await service.GetAsync(workspaceId, flowId, cancellationToken))!;
        }
        return new() { Namespace = current.Namespace, Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = stored.ETag };
    }
    public async Task<string?> GetVersionTokenAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => (await service.GetAsync(CurrentWorkspaceId(), new(name, @namespace), cancellationToken))?.ETag;
    public Task DeleteAsync(ManagedPackResource resource, PackRemovalOptions options, CancellationToken cancellationToken) => service.DeleteAsync(CurrentWorkspaceId(), new(resource.Name, resource.Namespace), resource.VersionToken, cancellationToken);
    private WorkspaceId CurrentWorkspaceId() => new(requestContext.Current.WorkspaceId);
    private static PackResourceEnvelope<PackFlowDefinition> Parse(PackResourceDocument resource) => ResourceManifestSerializer.FromJson<PackResourceEnvelope<PackFlowDefinition>>(resource.Manifest.GetRawText());
}

public sealed record PackEntryDefinition
{
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public EntryPresentation Presentation { get; init; } = null!;
    public EntryBinding Binding { get; init; } = null!;
    public EntryBehavior Behavior { get; init; } = new();
    public bool Publish { get; init; } = true;
}

public sealed class EntryPackResourceHandler(EntryAdministrationService service, IWorkplaceRepository repository, TimeProvider timeProvider, ICurrentRequestContext requestContext) : IPackResourceHandler
{
    public string Kind => ResourceKinds.Entry;
    public int InstallOrder => 60;
    public Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken)
    {
        var value = Parse(resource); WorkplaceValidation.Validate(ToDraft(CurrentWorkspaceId(), resource.Name, ResourceNamespace.Default, value.Definition, timeProvider.GetUtcNow())); return Task.CompletedTask;
    }
    public async Task<bool> ExistsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => await repository.GetEntryDraftAsync(CurrentWorkspaceId(), new(name, @namespace), cancellationToken) is not null || await repository.GetEntryAsync(CurrentWorkspaceId(), new(name, @namespace), cancellationToken) is not null;
    public async Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, ResourceNamespace @namespace, string packVersion, CancellationToken cancellationToken)
    {
        var workspaceId = CurrentWorkspaceId(); var definition = Parse(resource).Definition; var saved = await service.SaveAsync(ToDraft(workspaceId, resource.Name, @namespace, definition, timeProvider.GetUtcNow()), cancellationToken);
        var published = definition.Publish ? await service.PublishAsync(workspaceId, saved.Id, cancellationToken) : null;
        return new() { Namespace = @namespace, Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = $"{saved.Revision}:{published?.Version ?? 0}" };
    }
    public async Task<ManagedPackResource> UpdateAsync(PackResourceDocument resource, ManagedPackResource current, PackIdentity pack, string packVersion, CancellationToken cancellationToken)
    {
        var workspaceId = CurrentWorkspaceId(); var definition = Parse(resource).Definition; var saved = await service.SaveAsync(ToDraft(workspaceId, current.Name, current.Namespace, definition, timeProvider.GetUtcNow()), cancellationToken);
        var published = definition.Publish ? await service.PublishAsync(workspaceId, saved.Id, cancellationToken) : null;
        return new() { Namespace = current.Namespace, Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = $"{saved.Revision}:{published?.Version ?? 0}" };
    }
    public async Task<string?> GetVersionTokenAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        var workspaceId = CurrentWorkspaceId(); var id = new EntryId(name, @namespace); var draft = await repository.GetEntryDraftAsync(workspaceId, id, cancellationToken); if (draft is null) return null;
        var published = await repository.GetEntryAsync(workspaceId, id, cancellationToken); return $"{draft.Revision}:{published?.Version ?? 0}";
    }
    public Task DeleteAsync(ManagedPackResource resource, PackRemovalOptions options, CancellationToken cancellationToken) => service.DeleteAsync(CurrentWorkspaceId(), new(resource.Name, resource.Namespace), options.RemoveDashboardReferences, options.CloseInteractions, cancellationToken);
    private static PackResourceEnvelope<PackEntryDefinition> Parse(PackResourceDocument resource) => ResourceManifestSerializer.FromJson<PackResourceEnvelope<PackEntryDefinition>>(resource.Manifest.GetRawText());
    private WorkspaceId CurrentWorkspaceId() => new(requestContext.Current.WorkspaceId);
    private static EntryDraft ToDraft(WorkspaceId workspaceId, string name, ResourceNamespace @namespace, PackEntryDefinition definition, DateTimeOffset now) => new() { WorkspaceId = workspaceId, Id = new(name, @namespace), Name = name, DisplayName = definition.DisplayName ?? name, Description = definition.Description, Presentation = definition.Presentation, Binding = definition.Binding, Behavior = definition.Behavior, UpdatedAt = now };
}
