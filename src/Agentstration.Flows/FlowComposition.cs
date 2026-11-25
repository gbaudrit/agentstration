using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Flows;

/// <summary>A resource-owned ordered binding for a named point in an immutable Flow.</summary>
public sealed record FlowCompositionCall
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, Agentstration.Schemas.Contracts.SchemaBindingSnapshot>? FunctionSchemaSnapshots { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FlowCallReference? Flow { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Agentstration.Functions.Contracts.FunctionReference? Function { get; init; }
    // Resolved at admission and retained in the server-owned receipt. Null on older receipts means previous result.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FlowCompositionFunctionInputSource? FunctionInputSource { get; init; }
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    // Used by platform-owned acquisition bindings whose existing contract receives the complete envelope.
    public bool PassInputDirectly { get; init; }
}

public sealed record FlowCompositionPointStepDefinition : FlowStepDefinition
{
    public required string Point { get; init; }
    public bool AllowEmpty { get; init; } = true;
    public JsonElement InputSchema { get; init; } = JsonSerializer.SerializeToElement(new { type = "object" });
    public JsonElement OutputSchema { get; init; } = JsonSerializer.SerializeToElement(new { type = "object" });
    public JsonElement? InputMapping { get; init; }
}

public sealed record FlowCompositionReceipt(string TemplateHash, string ExecutableHash,
    IReadOnlyDictionary<string, IReadOnlyList<FlowCompositionCall>> Bindings);

/// <summary>Schema-level diagnostics only; never contains invocation values or schema defaults.</summary>
public sealed record FlowCompositionDiagnostic(string Point, string Boundary, string Template, string Version,
    string ActualType, string ExpectedType);

public sealed record ResolvedFlowComposition(FlowCallReference Orchestration, JsonElement? InputSchema,
    JsonElement? OutputSchema, FlowCompositionReceipt? Receipt);

public interface IFlowCompositionResolver
{
    // Only application-owned required boundaries may supply owner-side definition authority.
    Task<ResolvedFlowComposition> ResolveOwnedAsync(ResourceScopeRef consumer, ResourceNamespace ownerNamespace,
        FlowCallReference orchestration, IReadOnlyDictionary<string, IReadOnlyList<FlowCompositionCall>> bindings,
        ResourceScopeRef? templateUseScope, IReadOnlyDictionary<string, ResourceScopeRef> ownedPointUseScopes,
        bool accepted, CancellationToken cancellationToken) => accepted
        ? ResolveAcceptedAsync(consumer, ownerNamespace, orchestration, bindings, cancellationToken)
        : ResolveAsync(consumer, ownerNamespace, orchestration, bindings, cancellationToken);

    // Accepted receipts originate in server storage, never in caller-authored resource definitions.
    Task<ResolvedFlowComposition> ResolveAcceptedAsync(ResourceScopeRef consumer, ResourceNamespace ownerNamespace,
        FlowCallReference orchestration, IReadOnlyDictionary<string, IReadOnlyList<FlowCompositionCall>> bindings,
        CancellationToken cancellationToken) => ResolveAsync(consumer, ownerNamespace, orchestration, bindings, cancellationToken);

    Task<ResolvedFlowComposition> ResolveAsync(ResourceScopeRef consumer, ResourceNamespace ownerNamespace,
        FlowCallReference orchestration, IReadOnlyDictionary<string, IReadOnlyList<FlowCompositionCall>> bindings,
        CancellationToken cancellationToken);
}
