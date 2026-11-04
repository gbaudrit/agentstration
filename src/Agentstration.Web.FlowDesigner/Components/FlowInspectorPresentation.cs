using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.FlowDesigner.Backend;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.FlowDesigner.Components;

internal static class FlowInspectorPresentation
{
    internal static string AgentUrl(FlowDesignerAgent agent) => (agent.Namespace.IsDefault
        ? $"/agents/{Uri.EscapeDataString(agent.Name)}"
        : $"/namespaces/{Uri.EscapeDataString(agent.Namespace.Value)}/agents/{Uri.EscapeDataString(agent.Name)}")
        + $"?scopeRef={Uri.EscapeDataString(agent.ScopeRef.Value)}";

    internal static string FlowUrl(FlowDesignerFlow flow) => (flow.Namespace.IsDefault
        ? $"/flows/{Uri.EscapeDataString(flow.Name)}"
        : $"/namespaces/{Uri.EscapeDataString(flow.Namespace.Value)}/flows/{Uri.EscapeDataString(flow.Name)}")
        + $"?scopeRef={Uri.EscapeDataString(flow.ScopeRef.Value)}";

    internal static string ToolUrl(FlowDesignerTool tool) => (tool.Namespace.IsDefault
        ? $"/tools/{Uri.EscapeDataString(tool.Name)}"
        : $"/tools/{Uri.EscapeDataString(tool.Name)}?namespace={Uri.EscapeDataString(tool.Namespace.Value)}")
        + $"{(tool.Namespace.IsDefault ? "?" : "&")}scopeRef={Uri.EscapeDataString(tool.ScopeRef.Value)}";

    internal static string OutputMappingExpression(OutputFlowStepDefinition output) =>
        output.OutputMapping is { ValueKind: JsonValueKind.String } mapping
            ? mapping.GetString() ?? "${transition.output}"
            : output.OutputMapping is { } complex ? JsonText(complex) : "${transition.output}";

    internal static string ResourceKey(ResourceScopeRef? scopeRef, ResourceNamespace @namespace, string name) => $"{scopeRef?.Value}|{@namespace.Value}|{name}";

    internal static string ResourceIdentity(ResourceScopeRef? scopeRef, ResourceNamespace @namespace, string name) => $"{scopeRef?.Value ?? "?"} · {@namespace.Value}/{name}";

    internal static string MappingValue(JsonElement? mapping, string propertyName) => mapping is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(propertyName, out var property)
        ? property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.GetRawText()
        : string.Empty;

    internal static bool PassesIncomingTransitionOutput(JsonElement? mapping) =>
        mapping is { ValueKind: JsonValueKind.String } value
        && string.Equals(value.GetString(), "${transition.output}", StringComparison.Ordinal);

    internal static string SchemaReferenceLabel(SchemaVersionReference reference) =>
        $"{reference.Namespace?.Value ?? ResourceNamespace.Default.Value}/{reference.Name} · {reference.Version}";

    internal static string Text(ChangeEventArgs e) => e.Value?.ToString()?.Trim() ?? string.Empty;

    internal static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    internal static string JsonText(JsonElement? value) => value?.GetRawText() ?? string.Empty;

    internal static string ResourceOptionLabel(IStringLocalizer<FlowDesignerStrings> localizer, string displayName, ResourceNamespace @namespace, ResourceScopeRef scopeRef) => @namespace.IsDefault
        ? Localize(localizer, "ResourceOption", displayName, ScopeLabel(localizer, scopeRef))
        : Localize(localizer, "ResourceOptionNamespaced", displayName, @namespace.Value, ScopeLabel(localizer, scopeRef));

    internal static string ScopeLabel(IStringLocalizer<FlowDesignerStrings> localizer, ResourceScopeRef scopeRef) => Localize(localizer, $"Scope.{scopeRef.Kind}");

    internal static string ToolState(IStringLocalizer<FlowDesignerStrings> localizer, FlowDesignerTool tool) => !tool.Enabled
        ? Localize(localizer, "ToolDisabled")
        : !tool.Available
            ? Localize(localizer, "ToolUnavailable")
            : tool.RequiresApproval ? Localize(localizer, "ToolApprovalRequired") : string.Empty;

    private static string Localize(IStringLocalizer<FlowDesignerStrings> localizer, string key, params object[] arguments) => localizer[key, arguments].Value;
}
