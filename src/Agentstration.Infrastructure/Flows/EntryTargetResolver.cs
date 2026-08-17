using Agentstration.Application.Work;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Infrastructure.Flows;

public sealed class EntryTargetResolver(
    FlowService flows,
    AgentManagementService agents,
    IWorkplaceRepository workplace) : IEntryTargetResolver
{
    public async Task<EntryResolvedTarget> ResolveAsync(EntryDraft draft, CancellationToken cancellationToken)
    {
        WorkplaceValidation.Validate(draft);
        return draft.Binding.Kind switch
        {
            EntryBindingKind.Flow => await ResolveFlowAsync(draft, cancellationToken),
            EntryBindingKind.Agent => await ResolveAgentAsync(draft, cancellationToken),
            _ => throw new WorkValidationException("entry_binding_kind_invalid", "The Entry binding kind is not supported.")
        };
    }

    public async Task<IReadOnlyList<EntryDependency>> GetDependenciesAsync(WorkspaceId workspaceId, EntryId entryId, CancellationToken cancellationToken)
    {
        var dependencies = new List<EntryDependency>();
        var draft = await workplace.GetEntryDraftAsync(workspaceId, entryId, cancellationToken);
        if (draft is not null)
            dependencies.Add(new EntryDependency(draft.Binding.ResourceId, draft.Binding.Kind.ToString(), "DependsOn"));
        var published = await workplace.GetEntryAsync(workspaceId, entryId, cancellationToken);
        if (published is not null)
            dependencies.Add(new EntryDependency(published.ResolvedTarget.FlowResourceId, "Flow", "ResolvedTarget"));
        return dependencies.DistinctBy(value => (value.ResourceId, value.Relationship)).ToArray();
    }

    private async Task<EntryResolvedTarget> ResolveFlowAsync(EntryDraft draft, CancellationToken cancellationToken)
    {
        var resourceId = draft.Binding.ResourceId;
        var targetNamespace = draft.Binding.Namespace ?? draft.Id.Namespace;
        var flowId = FlowIdFrom(resourceId, targetNamespace);
        var stored = await flows.GetAsync(flowId, cancellationToken)
            ?? throw new KeyNotFoundException($"Flow '{resourceId}' was not found.");
        var version = stored.Value.ActiveVersion
            ?? throw new WorkValidationException("entry_flow_not_published", $"Flow '{resourceId}' has no active published version.");
        var resolved = await flows.ResolveAsync(new FlowReference(flowId, version, UseActiveVersion: false, targetNamespace), draft.Id.Namespace, cancellationToken);
        ValidateFlowInputs(draft, resolved);
        return new EntryResolvedTarget(resourceId, version) { Namespace = targetNamespace };
    }

    private async Task<EntryResolvedTarget> ResolveAgentAsync(EntryDraft draft, CancellationToken cancellationToken)
    {
        var resourceId = draft.Binding.ResourceId;
        var targetNamespace = draft.Binding.Namespace ?? draft.Id.Namespace;
        var primary = draft.Presentation.Fields.Single(value => value.Role == EntryFieldRole.PrimaryInput);
        if (primary.Type is not EntryFieldType.Prompt and not EntryFieldType.Text and not EntryFieldType.Textarea and not EntryFieldType.Conversation)
            throw new WorkValidationException("entry_agent_input_incompatible", "A Direct Agent Flow primary input must be textual.");
        var agent = await agents.GetAgentAsync(targetNamespace, resourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"Agent '{resourceId}' was not found.");
        if (agent.Value.Generation < 1)
            throw new WorkValidationException("entry_agent_version_unresolved", $"Agent '{resourceId}' has no resolvable generation.");

        var name = ResourceName(resourceId);
        var flowId = new FlowId($"system-direct-agent-{name}", draft.Id.Namespace);
        var version = $"1.0.{agent.Value.Generation - 1}";
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, resourceId));
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["systemManaged"] = bool.TrueString,
            ["systemKind"] = "DirectAgentFlow",
            ["sourceAgentResourceId"] = resourceId,
            ["sourceAgentGeneration"] = agent.Value.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var current = await flows.GetAsync(flowId, cancellationToken);
        if (current is null)
        {
            await flows.CreateAsync(new CreateFlowCommand(flowId.Value, $"System-managed direct invocation for {agent.Value.Definition.DisplayName}.", version, true, definition, metadata), draft.Id.Namespace, cancellationToken);
        }
        else if (!string.Equals(current.Value.Version, version, StringComparison.Ordinal))
        {
            await flows.UpdateAsync(flowId, new UpdateFlowCommand(
                $"System-managed direct invocation for {agent.Value.Definition.DisplayName}.", version, true, definition, metadata), current.ETag, cancellationToken);
        }

        if (await flows.GetVersionAsync(flowId, version, cancellationToken) is null)
            await flows.PublishVersionAsync(flowId, version, activate: true, cancellationToken);
        return new EntryResolvedTarget(flowId.Value, version) { Namespace = draft.Id.Namespace };
    }

    private static FlowId FlowIdFrom(string resourceId, Agentstration.Resources.ResourceNamespace @namespace) => new(ResourceName(resourceId), @namespace);
    private static string ResourceName(string resourceId) => resourceId;

    private static void ValidateFlowInputs(EntryDraft draft, FlowVersion flow)
    {
        if (flow.Graph?.InputSchema is not { ValueKind: System.Text.Json.JsonValueKind.Object } schema
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        foreach (var field in draft.Presentation.Fields)
        {
            if (!properties.TryGetProperty(field.Name, out var property))
                throw new WorkValidationException("entry_flow_input_missing", $"Flow '{flow.FlowId}' does not declare input '{field.Name}'.");
            if (!property.TryGetProperty("type", out var type) || type.ValueKind != System.Text.Json.JsonValueKind.String) continue;
            var expected = type.GetString();
            var compatible = field.Type switch
            {
                EntryFieldType.Number => expected is "number" or "integer",
                EntryFieldType.Boolean => expected == "boolean",
                EntryFieldType.MultiChoice or EntryFieldType.Files => expected == "array",
                EntryFieldType.File => expected is "artifact" or "object" or "string",
                _ => expected == "string"
            };
            if (!compatible) throw new WorkValidationException("entry_flow_input_incompatible", $"Entry field '{field.Name}' is not compatible with Flow input type '{expected}'.");
        }
    }
}
