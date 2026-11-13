using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.Components.JsonSchema;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.Diagramming;
using Agentstration.Web.FlowDesigner.State;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using static Agentstration.Web.FlowDesigner.Components.FlowInspectorPresentation;

namespace Agentstration.Web.FlowDesigner.Components;

public partial class FlowDesigner
{
    private async Task AddStepAsync(string type)
    {
        var index = Editor.State.Resource!.Definition.Steps.Count; var name = Unique(type);
        var defaultAgent = agents.FirstOrDefault();
        FlowStepDefinition step = type switch
        {
            "input" => new InputFlowStepDefinition { Name = name, DisplayName = "Input" },
            "agent" => new AgentFlowStepDefinition { Name = name, DisplayName = T("CallAgent"), Agent = defaultAgent is null ? new(UnconfiguredAgent) : AgentReference(defaultAgent), InputMapping = JsonSerializer.SerializeToElement("${transition.output}") },
            "flow" => new FlowCallStepDefinition { Name = name, DisplayName = "Flow", Flow = DefaultFlowReference(), InputMapping = JsonSerializer.SerializeToElement("${transition.output}") },
            "tool" => new ToolFlowStepDefinition { Name = name, DisplayName = T("Step.tool"), Tool = DefaultToolReference(), ArgumentsMapping = JsonSerializer.SerializeToElement("${transition.output}") },
            "router" => new RouterFlowStepDefinition { Name = name, DisplayName = "Router" },
            "condition" => new ConditionFlowStepDefinition { Name = name, DisplayName = "Condition", Left = "${input.prompt}", Right = "" },
            "composition" => new FlowCompositionPointStepDefinition { Name = name, Point = name, DisplayName = T("Step.composition"), InputMapping = JsonSerializer.SerializeToElement("${transition.output}") },
            "artifactTransform" => new ArtifactTransformFlowStepDefinition { Name = name, DisplayName = T("Step.artifactTransform"),
                ArtifactsMapping = JsonSerializer.SerializeToElement("${transition.output}") },
            "transform" => new TransformFlowStepDefinition { Name = name, DisplayName = "Transform", Mapping = JsonSerializer.SerializeToElement(new { }) },
            "output" => new OutputFlowStepDefinition { Name = name, DisplayName = "Output", OutputMapping = JsonSerializer.SerializeToElement("${transition.output}") },
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported Flow step type.")
        };
        IReadOnlyList<FlowDesignerFlowVersion>? callVersions = null;
        if (step is FlowCallStepDefinition flowCall)
        {
            callVersions = await Resources.GetFlowVersionsAsync(ResolveFlowScope(flowCall.Flow), flowCall.Flow.Namespace ?? Namespace, flowCall.Flow.ResourceId, cancellation.Token);
            flowVersions = callVersions;
        }
        var errorEvent = step switch
        {
            AgentFlowStepDefinition or ToolFlowStepDefinition or ArtifactTransformFlowStepDefinition => "error",
            FlowCallStepDefinition selectedCall => ResolveFlowVersion(selectedCall, callVersions ?? [])?.NamedOutputs
                .FirstOrDefault(output => output.Outcome == FlowOutputOutcome.Error)?.Name,
            _ => null
        };
        var errorTransition = errorEvent is null
            ? null
            : ErrorOutputTransition(Editor.State.Resource.Definition, name, errorEvent);
        await DispatchAsync(new AddStepCommand(step, new(40 + index * 180, 250), errorTransition));
        Editor.SelectStep(name);
        if (step is FlowCallStepDefinition addedCall) await SyncFlowCallOutputsAsync(addedCall, callVersions ?? []);
    }

    private static FlowTransitionDefinition? ErrorOutputTransition(FlowGraphDefinition definition, string sourceStep, string errorEvent)
    {
        var target = definition.Steps.OfType<OutputFlowStepDefinition>()
            .FirstOrDefault(output => output.Outcome == FlowOutputOutcome.Error);
        return target is null
            ? null
            : new($"{sourceStep}-{errorEvent}-{target.Name}", sourceStep, errorEvent, target.Name);
    }

    private async Task AddTransitionAsync() { if (!string.IsNullOrWhiteSpace(transitionFrom) && !string.IsNullOrWhiteSpace(transitionTo)) await CreateTransitionAsync(new($"{transitionFrom}-{transitionEvent}-{transitionTo}", transitionFrom, transitionEvent, transitionTo)); }
    private Task UpdateTransitionFromAsync(ChangeEventArgs e) => UpdateSelectedTransitionAsync(transition => transition with { FromStep = Text(e) });
    private Task UpdateTransitionEventAsync(ChangeEventArgs e) => UpdateSelectedTransitionAsync(transition => transition with { Event = Text(e) });
    private Task UpdateTransitionToAsync(ChangeEventArgs e) => UpdateSelectedTransitionAsync(transition => transition with { ToStep = Text(e) });
    private Task UpdateTransitionConditionAsync(ChangeEventArgs e) => UpdateSelectedTransitionAsync(transition => transition with { Condition = EmptyToNull(Text(e)) });
    private Task UpdateTransitionPriorityAsync(ChangeEventArgs e)
    {
        var text = Text(e);
        var priority = int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : (int?)null;
        return UpdateSelectedTransitionAsync(transition => transition with { Priority = priority });
    }
    private Task UpdateSelectedTransitionAsync(Func<FlowTransitionDefinition, FlowTransitionDefinition> update) =>
        ReadOnly || SelectedTransition is not { } transition
            ? Task.CompletedTask
            : DispatchAsync(new UpdateTransitionCommand(update(transition)));
    private async Task RemoveSelectedAsync()
    {
        if (SelectedStep is not { } step) return;
        await RemoveStepAsync(step.Name);
        if (Editor.State.Resource?.Definition.Steps.All(candidate => candidate.Name != step.Name) == true)
            showStepSettings = false;
    }
    private async Task RemoveStepAsync(string name)
    {
        if (ReadOnly || Editor.State.Resource is not { } resource || name == resource.Definition.EntryStep
            || resource.Definition.Steps.All(step => step.Name != name)) return;
        await DispatchAsync(new RemoveStepCommand(name));
        if (Editor.State.Selection.StepName == name) Editor.SelectStep(null);
    }
    private Task UpdateDisplayNameAsync(ChangeEventArgs e) => UpdateSelectedAsync(SelectedStep! with { DisplayName = Text(e) });
    private Task UpdateAgentAsync(ChangeEventArgs e)
    {
        if (SelectedStep is not AgentFlowStepDefinition step) return Task.CompletedTask;
        var value = Text(e);
        if (value == "${steps.route-request.output.selectedAgent}")
            return UpdateSelectedAsync(step with { Agent = new(value) });
        if (!TryParseFlowKey(value, out var target)) return Task.CompletedTask;
        var selected = agents.FirstOrDefault(agent => ResourceKey(agent.ScopeRef, agent.Namespace, agent.Name) == ResourceKey(target.ScopeRef, target.Namespace, target.Name));
        return selected is null ? Task.CompletedTask : UpdateSelectedAsync(step with { Agent = AgentReference(selected) });
    }
    private async Task UpdateFlowTargetAsync(ChangeEventArgs e)
    {
        if (SelectedStep is not FlowCallStepDefinition call || !TryParseFlowKey(Text(e), out var target)) return;
        var reference = call.Flow with { ResourceId = target.Name, Namespace = target.Namespace == Namespace ? null : target.Namespace, ScopeRef = target.ScopeRef };
        var versions = await Resources.GetFlowVersionsAsync(target.ScopeRef, reference.Namespace ?? Namespace, reference.ResourceId, cancellation.Token);
        if (reference.VersionStrategy == FlowCallVersionStrategy.Exact && versions.All(version => version.Version != reference.Version))
            reference = reference with { Version = versions.FirstOrDefault()?.Version };
        var updated = call with { Flow = reference };
        await UpdateSelectedAsync(updated);
        flowVersions = versions;
        await SyncFlowCallOutputsAsync(updated, versions);
    }
    private async Task UpdateFlowVersionStrategyAsync(ChangeEventArgs e)
    {
        if (SelectedStep is not FlowCallStepDefinition call || !Enum.TryParse<FlowCallVersionStrategy>(Text(e), out var strategy)) return;
        var reference = call.Flow with { VersionStrategy = strategy, Version = strategy == FlowCallVersionStrategy.Exact ? flowVersions.FirstOrDefault()?.Version : null };
        var updated = call with { Flow = reference };
        await UpdateSelectedAsync(updated);
        await SyncFlowCallOutputsAsync(updated, flowVersions);
    }
    private async Task UpdateFlowExactVersionAsync(ChangeEventArgs e)
    {
        if (SelectedStep is not FlowCallStepDefinition call) return;
        var updated = call with { Flow = call.Flow with { Version = Text(e) } };
        await UpdateSelectedAsync(updated);
        await SyncFlowCallOutputsAsync(updated, flowVersions);
    }
    private Task UpdateFlowMappingAsync(string propertyName, ChangeEventArgs e)
    {
        if (SelectedStep is not FlowCallStepDefinition call) return Task.CompletedTask;
        var mapping = call.InputMapping is { ValueKind: JsonValueKind.Object } value
            ? value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        mapping[propertyName] = JsonSerializer.SerializeToElement(Text(e));
        return UpdateSelectedAsync(call with { InputMapping = JsonSerializer.SerializeToElement(mapping) });
    }
    private Task UpdateFlowTransitionPassthroughAsync(ChangeEventArgs e)
    {
        if (SelectedStep is not FlowCallStepDefinition call) return Task.CompletedTask;
        var enabled = e.Value is bool value && value;
        var mapping = enabled
            ? JsonSerializer.SerializeToElement("${transition.output}")
            : JsonSerializer.SerializeToElement(new { });
        return UpdateSelectedAsync(call with { InputMapping = mapping });
    }
    private Task UseCalledFlowSharedInputAsync()
    {
        if (ReadOnly
            || SelectedStep is not FlowCallStepDefinition call
            || !FlowCallReceivesParentInput(call)
            || ParentInputHasExplicitContract
            || ParentInputStep is not { } input
            || SelectedFlowVersion?.InputSchemaReference is not { } reference)
            return Task.CompletedTask;
        return SetParentInputSharedContractAsync(input, reference);
    }
    private Task SetParentInputSharedContractAsync(InputFlowStepDefinition input, SchemaVersionReference reference) =>
        DispatchAsync(new UpdateFlowInputContractCommand(input with
        {
            Schema = null,
            SchemaBinding = new SchemaBinding { Reference = reference }
        }));
    private Task CopyCalledFlowInlineInputAsync()
    {
        if (ReadOnly
            || SelectedStep is not FlowCallStepDefinition call
            || !FlowCallReceivesParentInput(call)
            || ParentInputHasExplicitContract
            || ParentInputStep is not { } input
            || SelectedFlowVersion is not { InputSchema: { } schema, InputSchemaReference: null })
            return Task.CompletedTask;
        return SetParentInputInlineContractAsync(input, schema);
    }
    private Task SetParentInputInlineContractAsync(InputFlowStepDefinition input, JsonElement schema) =>
        DispatchAsync(new UpdateFlowInputContractCommand(input with
        {
            Schema = null,
            SchemaBinding = new SchemaBinding
            {
                Inline = new InlineSchemaBinding
                {
                    Format = SchemaFormatIds.JsonSchema,
                    Dialect = "https://json-schema.org/draft/2020-12/schema",
                    Content = schema.Clone()
                }
            }
        }));
    private async Task OfferConnectedInputContractAsync(FlowTransitionDefinition transition)
    {
        if (ReadOnly || ParentInputStep is not { } input
            || transition.FromStep != input.Name
            || Editor.State.Resource?.Definition.Steps.FirstOrDefault(step => step.Name == transition.ToStep) is not { } target)
            return;

        switch (target)
        {
            case FlowCallStepDefinition call when PassesIncomingTransitionOutput(call.InputMapping):
                var versions = await Resources.GetFlowVersionsAsync(ResolveFlowScope(call.Flow), call.Flow.Namespace ?? Namespace, call.Flow.ResourceId, cancellation.Token);
                var version = ResolveFlowVersion(call, versions);
                if (version?.InputSchema is { } flowSchema)
                    connectionContractPrompt = new(FlowConnectionContractSource.Flow, call.DisplayName ?? call.Name,
                        flowSchema, version.InputSchemaReference, ParentInputHasExplicitContract);
                break;
            case AgentFlowStepDefinition agent when PassesIncomingTransitionOutput(agent.InputMapping):
                var selectedAgent = ResolveAgent(agent.Agent);
                if (selectedAgent?.InputSchema is { } agentSchema)
                    connectionContractPrompt = new(FlowConnectionContractSource.Agent, selectedAgent.DisplayName,
                        agentSchema, selectedAgent.InputSchemaReference, ParentInputHasExplicitContract);
                break;
            case ToolFlowStepDefinition tool when PassesIncomingTransitionOutput(tool.ArgumentsMapping):
                var selectedTool = tools.FirstOrDefault(item => ToolKey(item) == ToolKey(tool.Tool));
                if (selectedTool is not null)
                    connectionContractPrompt = new(FlowConnectionContractSource.Tool, selectedTool.DisplayName,
                        selectedTool.InputSchema, selectedTool.InputSchemaReference, ParentInputHasExplicitContract);
                break;
        }
    }
    private async Task ApplyConnectionContractAsync()
    {
        if (connectionContractPrompt is not { } prompt || ParentInputStep is not { } input)
        {
            connectionContractPrompt = null;
            return;
        }
        if (prompt.Reference is { } reference)
            await SetParentInputSharedContractAsync(input, reference);
        else
            await SetParentInputInlineContractAsync(input, prompt.Schema);
        connectionContractPrompt = null;
    }
    private void DismissConnectionContractPrompt() => connectionContractPrompt = null;
    private Task SelectToolAsync(FlowDesignerTool selected)
    {
        toolSearch = string.Empty;
        toolPickerRevision++;
        if (ReadOnly || SelectedStep is not ToolFlowStepDefinition tool || !tools.Contains(selected)) return Task.CompletedTask;
        if (ToolKey(tool.Tool) == ToolKey(selected)) return Task.CompletedTask;
        return UpdateSelectedAsync(tool with { Tool = new(selected.Name, selected.Namespace == Namespace ? null : selected.Namespace, selected.ScopeRef), ArgumentsMapping = JsonSerializer.SerializeToElement("${transition.output}") });
    }
    private void HandleToolPickerKeyDown(KeyboardEventArgs e)
    {
        if (e.Key != "Escape") return;
        toolSearch = string.Empty;
        toolPickerRevision++;
    }
    private Task UpdateToolMappingAsync(string propertyName, ChangeEventArgs e)
    {
        if (SelectedStep is not ToolFlowStepDefinition tool) return Task.CompletedTask;
        var mapping = tool.ArgumentsMapping is { ValueKind: JsonValueKind.Object } value
            ? value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        mapping[propertyName] = JsonSerializer.SerializeToElement(Text(e));
        return UpdateSelectedAsync(tool with { ArgumentsMapping = JsonSerializer.SerializeToElement(mapping) });
    }
    private Task UpdateToolTransitionPassthroughAsync(ChangeEventArgs e)
    {
        if (SelectedStep is not ToolFlowStepDefinition tool) return Task.CompletedTask;
        var mapping = e.Value is bool value && value
            ? JsonSerializer.SerializeToElement("${transition.output}")
            : JsonSerializer.SerializeToElement(new { });
        return UpdateSelectedAsync(tool with { ArgumentsMapping = mapping });
    }
    private Task UpdateSelectedAsync(FlowStepDefinition step) => DispatchAsync(new UpdateStepCommand(step));

    private async Task LoadAgentsAsync() { try { loadingAgents = true; routerAgents = await Resources.GetAgentsAsync(OwnerScope, cancellation.Token); } catch (Exception exception) { error = exception.Message; } finally { loadingAgents = false; } }
    private async Task AddAllRouterAgentsAsync() { await LoadAgentsAsync(); if (SelectedStep is not RouterFlowStepDefinition router) return; var candidates = router.Candidates.ToList(); foreach (var item in routerAgents.Where(item => candidates.All(candidate => AgentSelectionValue(candidate.Agent) != ResourceKey(item.ScopeRef, item.Namespace, item.Name)))) candidates.Add(new(UniqueRoute(RouteKey(item.DisplayName), candidates), AgentReference(item), $"Agent {item.DisplayName}.")); await UpdateSelectedAsync(router with { Candidates = candidates }); }
    private Task AddRouterCandidateAsync() { var router = (RouterFlowStepDefinition)SelectedStep!; var candidates = router.Candidates.ToList(); var selected = routerAgents.FirstOrDefault() ?? agents.FirstOrDefault(); candidates.Add(new(UniqueRoute("route", candidates), selected is null ? new(UnconfiguredAgent) : AgentReference(selected))); return UpdateSelectedAsync(router with { Candidates = candidates }); }
    private Task RemoveRouterCandidateAsync(int index) { var router = (RouterFlowStepDefinition)SelectedStep!; return UpdateSelectedAsync(router with { Candidates = router.Candidates.Where((_, i) => i != index).ToArray() }); }
    private Task UpdateRouterCandidateAgentAsync(int index, ChangeEventArgs eventArgs)
    {
        if (!TryParseFlowKey(Text(eventArgs), out var target)) return Task.CompletedTask;
        var selected = routerAgents.FirstOrDefault(agent => ResourceKey(agent.ScopeRef, agent.Namespace, agent.Name) == ResourceKey(target.ScopeRef, target.Namespace, target.Name));
        return selected is null ? Task.CompletedTask : UpdateRouterCandidateAsync(index, candidate => candidate with { Agent = AgentReference(selected) });
    }
    private Task UpdateRouterCandidateAsync(int index, Func<FlowRouterCandidate, FlowRouterCandidate> update) { var router = (RouterFlowStepDefinition)SelectedStep!; var candidates = router.Candidates.ToArray(); candidates[index] = update(candidates[index]); return UpdateSelectedAsync(router with { Candidates = candidates }); }

    private async Task UpdateOutputNameAsync(ChangeEventArgs e)
    {
        if (ReadOnly || SelectedStep is not OutputFlowStepDefinition output) return;
        var name = Text(e);
        if (string.IsNullOrWhiteSpace(name) || name == output.Name) return;
        if (!IsValidStepName(name)) { error = T("OutputNameInvalid"); return; }
        if (Editor.State.Resource!.Definition.Steps.Any(step => step.Name == name)) { error = T("OutputNameDuplicate", name); return; }
        await DispatchAsync(new RenameStepCommand(output.Name, name));
    }
    private Task UpdateOutputOutcomeAsync(ChangeEventArgs e) => SelectedStep is OutputFlowStepDefinition output && Enum.TryParse<FlowOutputOutcome>(Text(e), out var outcome)
        ? UpdateSelectedAsync(output with { Outcome = outcome }) : Task.CompletedTask;
    private Task UpdateTransformMappingAsync(ChangeEventArgs e)
    {
        if (ReadOnly || SelectedStep is not TransformFlowStepDefinition transform) return Task.CompletedTask;
        var text = e.Value?.ToString()?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return UpdateSelectedAsync(transform with { Mapping = null });
        if (!TryParseJson(text, out var value))
        {
            error = T("TransformMappingInvalid");
            return Task.CompletedTask;
        }
        error = null;
        return UpdateSelectedAsync(transform with { Mapping = value });
    }
    private Task UpdateInputSchemaAsync(SchemaBindingEditorValue value) =>
        SelectedStep is InputFlowStepDefinition input
            ? DispatchAsync(new UpdateFlowInputContractCommand(input with { Schema = null, SchemaBinding = value.Binding }))
            : Task.CompletedTask;
    private Task UpdateTransformSchemaBindingAsync(SchemaBindingEditorValue value) =>
        SelectedStep is TransformFlowStepDefinition transform
            ? UpdateSelectedAsync(transform with { Schema = null, SchemaBinding = value.Binding })
            : Task.CompletedTask;
}
