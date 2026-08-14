using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Infrastructure.Packs;

internal static class PackProvenance
{
    public static ResourceMetadata Add(ResourceMetadata metadata, PackIdentity pack, string version)
    {
        var annotations = new Dictionary<string, string>(metadata.Annotations, StringComparer.Ordinal)
        {
            ["agentstration.io/pack.publisher"] = pack.Publisher,
            ["agentstration.io/pack.name"] = pack.Name,
            ["agentstration.io/pack.version"] = version
        };
        return metadata with { Annotations = annotations };
    }

    public static IReadOnlyDictionary<string, string> Add(IReadOnlyDictionary<string, string>? metadata, PackIdentity pack, string version)
    {
        var result = metadata is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        result["agentstration.io/pack.publisher"] = pack.Publisher;
        result["agentstration.io/pack.name"] = pack.Name;
        result["agentstration.io/pack.version"] = version;
        return result;
    }
}

public sealed class ModelProviderPackResourceHandler(ModelProviderManagementService service) : IPackResourceHandler
{
    public string Kind => ResourceKinds.ModelProvider;
    public int InstallOrder => 10;
    public Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken) { _ = Parse(resource); return Task.CompletedTask; }
    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) => await service.GetAsync(name, cancellationToken) is not null;
    public async Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, string packVersion, CancellationToken cancellationToken)
    {
        var value = Parse(resource); var stored = await service.CreateAsync(value with { Metadata = PackProvenance.Add(value.Metadata, pack, packVersion) }, cancellationToken);
        return Managed(resource, stored.ETag);
    }
    public async Task<string?> GetVersionTokenAsync(string name, CancellationToken cancellationToken) => (await service.GetAsync(name, cancellationToken))?.ETag;
    public Task DeleteAsync(ManagedPackResource resource, CancellationToken cancellationToken) => service.DeleteAsync(resource.Name, resource.VersionToken, cancellationToken);
    private static ModelProviderResource Parse(PackResourceDocument resource) => ResourceManifestSerializer.FromJson<ModelProviderResource>(resource.Manifest.GetRawText());
    private static ManagedPackResource Managed(PackResourceDocument resource, string token) => new() { Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = token };
}

public sealed class RuntimeProfilePackResourceHandler(RuntimeProfileManagementService service) : IPackResourceHandler
{
    public string Kind => ResourceKinds.RuntimeProfile;
    public int InstallOrder => 20;
    public Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken) { _ = Parse(resource); return Task.CompletedTask; }
    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) => await service.GetAsync(name, cancellationToken) is not null;
    public async Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, string packVersion, CancellationToken cancellationToken)
    {
        var value = Parse(resource); var stored = await service.CreateAsync(value with { Metadata = PackProvenance.Add(value.Metadata, pack, packVersion) }, cancellationToken);
        return Managed(resource, stored.ETag);
    }
    public async Task<string?> GetVersionTokenAsync(string name, CancellationToken cancellationToken) => (await service.GetAsync(name, cancellationToken))?.ETag;
    public Task DeleteAsync(ManagedPackResource resource, CancellationToken cancellationToken) => service.DeleteAsync(resource.Name, resource.VersionToken, cancellationToken);
    private static RuntimeProfileResource Parse(PackResourceDocument resource) => ResourceManifestSerializer.FromJson<RuntimeProfileResource>(resource.Manifest.GetRawText());
    private static ManagedPackResource Managed(PackResourceDocument resource, string token) => new() { Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = token };
}

public sealed class ModelProfilePackResourceHandler(ModelProfileManagementService service) : IPackResourceHandler
{
    public string Kind => ResourceKinds.ModelProfile;
    public int InstallOrder => 30;
    public Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken) { _ = Parse(resource); return Task.CompletedTask; }
    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) => await service.GetAsync(name, cancellationToken) is not null;
    public async Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, string packVersion, CancellationToken cancellationToken)
    {
        var value = Parse(resource); var stored = await service.CreateAsync(value with { Metadata = PackProvenance.Add(value.Metadata, pack, packVersion) }, cancellationToken);
        return Managed(resource, stored.ETag);
    }
    public async Task<string?> GetVersionTokenAsync(string name, CancellationToken cancellationToken) => (await service.GetAsync(name, cancellationToken))?.ETag;
    public Task DeleteAsync(ManagedPackResource resource, CancellationToken cancellationToken) => service.DeleteAsync(resource.Name, resource.VersionToken, cancellationToken);
    private static ModelProfileResource Parse(PackResourceDocument resource) => ResourceManifestSerializer.FromJson<ModelProfileResource>(resource.Manifest.GetRawText());
    private static ManagedPackResource Managed(PackResourceDocument resource, string token) => new() { Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = token };
}

public sealed class AgentPackResourceHandler(AgentManagementService service) : IPackResourceHandler
{
    public string Kind => ResourceKinds.Agent;
    public int InstallOrder => 40;
    public Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken) { _ = Parse(resource); return Task.CompletedTask; }
    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) => await service.GetAgentAsync(name, cancellationToken) is not null;
    public async Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, string packVersion, CancellationToken cancellationToken)
    {
        var value = Parse(resource); var stored = await service.PutAgentAsync(value with { Metadata = PackProvenance.Add(value.Metadata, pack, packVersion) }, null, true, cancellationToken);
        return Managed(resource, stored.ETag);
    }
    public async Task<string?> GetVersionTokenAsync(string name, CancellationToken cancellationToken) => (await service.GetAgentAsync(name, cancellationToken))?.ETag;
    public Task DeleteAsync(ManagedPackResource resource, CancellationToken cancellationToken) => service.DeleteAgentAsync(resource.Name, resource.VersionToken, cancellationToken);
    private static AgentResource Parse(PackResourceDocument resource) => ResourceManifestSerializer.FromJson<AgentResource>(resource.Manifest.GetRawText());
    private static ManagedPackResource Managed(PackResourceDocument resource, string token) => new() { Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = token };
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

public sealed class FlowPackResourceHandler(FlowService service, IFlowDefinitionValidator graphValidator, TimeProvider timeProvider) : IPackResourceHandler
{
    public string Kind => ResourceKinds.Flow;
    public int InstallOrder => 50;
    public async Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken)
    {
        var value = Parse(resource); var now = timeProvider.GetUtcNow();
        FlowValidator.Validate(new(new(resource.Name), resource.Name, value.Definition.Description, value.Definition.Version, value.Definition.Enabled, null, value.Definition.Spec, value.Definition.Metadata, now, now, value.Definition.DisplayName, value.Definition.Graph));
        if (value.Definition.Graph is not null)
        {
            var validation = await graphValidator.ValidateAsync(value.Definition.Graph, new(ResolveResources: false), cancellationToken);
            var error = validation.Issues.FirstOrDefault(issue => issue.Severity == FlowValidationSeverity.Error);
            if (error is not null) throw new FlowValidationException(error.Code, error.Message);
        }
    }
    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) => await service.GetAsync(new(name), cancellationToken) is not null;
    public async Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, string packVersion, CancellationToken cancellationToken)
    {
        var value = Parse(resource); var definition = value.Definition;
        var stored = await service.CreateAsync(new(resource.Name, definition.Description, definition.Version, definition.Enabled, definition.Spec, PackProvenance.Add(definition.Metadata, pack, packVersion), definition.Graph, definition.DisplayName), cancellationToken);
        if (definition.Publish) { _ = await service.PublishVersionAsync(new(resource.Name), definition.Version, definition.Activate, cancellationToken); stored = (await service.GetAsync(new(resource.Name), cancellationToken))!; }
        return new() { Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = stored.ETag };
    }
    public async Task<string?> GetVersionTokenAsync(string name, CancellationToken cancellationToken) => (await service.GetAsync(new(name), cancellationToken))?.ETag;
    public Task DeleteAsync(ManagedPackResource resource, CancellationToken cancellationToken) => service.DeleteAsync(new(resource.Name), resource.VersionToken, cancellationToken);
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

public sealed class EntryPackResourceHandler(EntryAdministrationService service, IWorkplaceRepository repository, TimeProvider timeProvider) : IPackResourceHandler
{
    public string Kind => ResourceKinds.Entry;
    public int InstallOrder => 60;
    public Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken)
    {
        var value = Parse(resource); WorkplaceValidation.Validate(ToDraft(resource.Name, value.Definition, timeProvider.GetUtcNow())); return Task.CompletedTask;
    }
    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) => await repository.GetEntryDraftAsync(new(name), cancellationToken) is not null || await repository.GetEntryAsync(new(name), cancellationToken) is not null;
    public async Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, string packVersion, CancellationToken cancellationToken)
    {
        var definition = Parse(resource).Definition; var saved = await service.SaveAsync(ToDraft(resource.Name, definition, timeProvider.GetUtcNow()), cancellationToken);
        var published = definition.Publish ? await service.PublishAsync(saved.Id, cancellationToken) : null;
        return new() { Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = $"{saved.Revision}:{published?.Version ?? 0}" };
    }
    public async Task<string?> GetVersionTokenAsync(string name, CancellationToken cancellationToken)
    {
        var id = new EntryId(name); var draft = await repository.GetEntryDraftAsync(id, cancellationToken); if (draft is null) return null;
        var published = await repository.GetEntryAsync(id, cancellationToken); return $"{draft.Revision}:{published?.Version ?? 0}";
    }
    public Task DeleteAsync(ManagedPackResource resource, CancellationToken cancellationToken) => service.DeleteAsync(new(resource.Name), cancellationToken);
    private static PackResourceEnvelope<PackEntryDefinition> Parse(PackResourceDocument resource) => ResourceManifestSerializer.FromJson<PackResourceEnvelope<PackEntryDefinition>>(resource.Manifest.GetRawText());
    private static EntryDraft ToDraft(string name, PackEntryDefinition definition, DateTimeOffset now) => new() { Id = new(name), Name = name, DisplayName = definition.DisplayName ?? name, Description = definition.Description, Presentation = definition.Presentation, Binding = definition.Binding, Behavior = definition.Behavior, UpdatedAt = now };
}
