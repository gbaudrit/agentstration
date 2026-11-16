using System.Text.Json;
using Agentstration.Functions;
using Agentstration.Functions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Functions;

public sealed class TransformationInvokeTool(TransformationFunctionService functions, ICurrentRequestContext current,
    Agentstration.Flows.Storage.Abstractions.IFlowRepository runs) : IInternalMcpToolHandler, IInternalMcpToolDefinitionProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public InternalMcpToolDefinition Definition { get; } = new("transformation.invoke", "Invoke a transformation function", "Execute an exact authorized native transformation function.",
        JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"function":{"type":"object"},"input":{}},"required":["function","input"],"additionalProperties":false}"""),
        JsonSerializer.Deserialize<JsonElement>("{}"), ExposeThroughMcp: false);
    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var function = invocation.Arguments.GetProperty("function").Deserialize<ResolvedTransformationFunction>(Json)
            ?? throw new ArgumentException("An exact function invocation is required.");
        var consumer = current.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(current.Current.TenantId),
            _ => ResourceScopeRef.Workspace(invocation.WorkspaceId.Value)
        };
        try
        {
            IReadOnlyDictionary<string, Agentstration.Schemas.Contracts.SchemaBindingSnapshot>? schemaBindings = null;
            if (FunctionInvocationSchemas.SchemaParameters(function.InputSchema).Count > 0)
            {
                var storedRun = invocation.RunId is { } runId
                    ? await runs.GetRunExactAsync(ResourceScopeRef.Workspace(invocation.WorkspaceId.Value), runId, cancellationToken) : null;
                var step = storedRun?.Value.DefinitionSnapshot.Graph?.Steps.OfType<Agentstration.Flows.ToolFlowStepDefinition>()
                    .SingleOrDefault(step => step.Name == invocation.FlowStepId);
                if (step?.FunctionBinding is not { } accepted || FunctionDigests.Compute(accepted) != FunctionDigests.Compute(function))
                    throw new Agentstration.Functions.Abstractions.TransformationFunctionException("function_schema_coordinate_invalid",
                        "Schema-aware function calls require their accepted server-owned Flow step.");
                schemaBindings = step.FunctionSchemaSnapshots;
            }
            return await functions.InvokeAsync(function, consumer, invocation.Arguments.GetProperty("input"), cancellationToken,
                invocation.RunId is { } run ? run + "/" + invocation.FlowStepId : invocation.CallId,
                schemaBindings);
        }
        catch (Agentstration.Functions.Abstractions.TransformationFunctionException error)
        { throw new Agentstration.Runtime.Abstractions.RuntimeGovernedOperationException(error.Code, error.Message); }
    }
}
