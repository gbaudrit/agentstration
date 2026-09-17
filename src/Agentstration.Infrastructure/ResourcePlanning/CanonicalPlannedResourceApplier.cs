using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.ResourcePlanning;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Infrastructure.ResourcePlanning;

public sealed class CanonicalPlannedResourceApplier(
    AgentManagementService agents,
    FlowService flows,
    EntryAdministrationService entries,
    TimeProvider timeProvider) : IPlannedResourceApplier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool Supports(string kind) => kind is AgentResourceKinds.Agent or FlowResourceKinds.Flow or EntryResourceKinds.Entry;

    public async Task ApplyAsync(ResourceChange change, bool resume, CancellationToken cancellationToken)
    {
        var resource = change.Proposed;
        if (resource.Kind == AgentResourceKinds.Agent)
        {
            if (change.Operation == ResourceChangeOperation.Delete)
            {
                await agents.DeleteAgentAsync(resource.Metadata.Namespace, resource.Metadata.Name, change.Current?.ETag, cancellationToken);
                return;
            }
            var definition = resource.Definition.Deserialize<AgentProperties>(JsonOptions) ?? throw new JsonException("Agent definition is empty.");
            await agents.PutAgentAsync(new AgentResource
            {
                ApiVersion = resource.ApiVersion, Kind = resource.Kind, ScopeRef = resource.ScopeRef,
                Metadata = resource.Metadata, Definition = definition
            }, change.Current?.ETag, change.Operation == ResourceChangeOperation.Create, cancellationToken);
            return;
        }

        var workspaceId = new WorkspaceId(resource.ScopeRef.TargetId!.Value);
        if (resource.Kind == FlowResourceKinds.Flow)
        {
            var id = new FlowId(resource.Metadata.Name, resource.Metadata.Namespace);
            if (change.Operation == ResourceChangeOperation.Delete)
            {
                await flows.DeleteAsync(workspaceId, id, change.Current?.ETag, cancellationToken);
                return;
            }
            var definition = resource.Definition;
            var description = definition.GetProperty("description").GetString();
            var version = definition.GetProperty("version").GetString() ?? throw new JsonException("Flow version is empty.");
            var enabled = definition.GetProperty("enabled").GetBoolean();
            var spec = definition.GetProperty("spec").Deserialize<FlowDefinition>(JsonOptions) ?? throw new JsonException("Flow specification is empty.");
            var displayName = definition.GetProperty("displayName").GetString();
            if (!resume && change.Operation == ResourceChangeOperation.Create)
                await flows.CreateAsync(workspaceId, new(resource.Metadata.Name, description, version, enabled, spec, null, null, displayName), resource.Metadata.Namespace, cancellationToken);
            else if (!resume)
                await flows.UpdateAsync(workspaceId, id, new(description, version, enabled, spec, null, null, displayName), change.Current?.ETag ?? throw new InvalidOperationException("Flow ETag is required."), cancellationToken);
            if (definition.GetProperty("publish").GetBoolean())
            {
                var activate = definition.GetProperty("activate").GetBoolean();
                if (await flows.GetVersionAsync(workspaceId, id, version, cancellationToken) is null)
                    await flows.PublishVersionAsync(workspaceId, id, version, activate, cancellationToken);
                else if (activate)
                    await flows.ActivateVersionAsync(workspaceId, id, version, cancellationToken);
            }
            return;
        }

        if (resource.Kind == EntryResourceKinds.Entry)
        {
            var id = new EntryId(resource.Metadata.Name, resource.Metadata.Namespace);
            if (change.Operation == ResourceChangeOperation.Delete)
            {
                await entries.DeleteAsync(workspaceId, id, removeDashboardReferences: false, closeInteractions: false, cancellationToken);
                return;
            }
            var definition = resource.Definition;
            var draft = new EntryDraft
            {
                WorkspaceId = workspaceId, Id = id, Name = resource.Metadata.Name,
                DisplayName = definition.GetProperty("displayName").GetString() ?? resource.Metadata.Name,
                Description = definition.GetProperty("description").GetString(),
                Presentation = definition.GetProperty("presentation").Deserialize<EntryPresentation>(JsonOptions) ?? throw new JsonException("Entry presentation is empty."),
                Binding = definition.GetProperty("binding").Deserialize<EntryBinding>(JsonOptions) ?? throw new JsonException("Entry binding is empty."),
                Behavior = definition.GetProperty("behavior").Deserialize<EntryBehavior>(JsonOptions) ?? new(),
                UpdatedAt = timeProvider.GetUtcNow()
            };
            if (!resume) await entries.SaveIfRevisionAsync(draft, change.Current?.Revision, cancellationToken);
            if (definition.GetProperty("publish").GetBoolean()) await entries.PublishAsync(workspaceId, id, resume, cancellationToken);
        }
    }
}
