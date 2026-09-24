using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Assistant;

public sealed class AssistantDiagnosticsMcpTool(
    IAgentstrationStorageInitializer storage,
    IIdentityStore identities,
    IResourceStore resources,
    FlowRunService flowRuns) : IInternalMcpToolHandler
{
    private const int MaximumInventoryEntries = 1000;
    private static readonly HashSet<string> Areas =
        ["installation", "workspace", "flows", "models", "tools", "sources", "extensions", "flow-run"];

    public InternalMcpToolDefinition Definition { get; } = new(
        AgentstrationInternalTools.AssistantDiagnosticsInspect,
        "Inspect Agentstration diagnostics",
        "Reads bounded, non-secret evidence for the current Workspace. Findings distinguish observations from hypotheses and never mutate resources. A flow run inspection returns causality metadata, status, error codes, and Tool attempt metadata without prompts, inputs, outputs, or secret values.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                area = new { type = "string", @enum = Areas.OrderBy(value => value).ToArray(), description = "Diagnostic area to inspect." },
                flowRunId = new { type = "string", minLength = 1, maxLength = 200, description = "Required only for the flow-run area." }
            },
            required = new[] { "area" },
            additionalProperties = false
        }),
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                area = new { type = "string" },
                availability = new { type = "string", @enum = new[] { "available", "unavailable" } },
                evidence = new { type = "array", items = new { type = "object" } },
                findings = new { type = "array", items = new { type = "object" } },
                navigation = new { type = "array", items = new { type = "string" } }
            },
            required = new[] { "area", "availability", "evidence", "findings", "navigation" },
            additionalProperties = false
        }));

    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        ValidateObject(invocation.Arguments);
        var area = RequiredString(invocation.Arguments, "area");
        if (!Areas.Contains(area))
            throw new ToolDefinitionInvocationException("assistant_diagnostics_area_invalid", "Argument 'area' is not a supported diagnostic area.");
        var flowRunId = OptionalString(invocation.Arguments, "flowRunId");
        if (area == "flow-run" && string.IsNullOrWhiteSpace(flowRunId))
            throw new ToolDefinitionInvocationException("assistant_diagnostics_flow_run_required", "Argument 'flowRunId' is required for the flow-run area.");
        if (area != "flow-run" && flowRunId is not null)
            throw new ToolDefinitionInvocationException("assistant_diagnostics_flow_run_not_applicable", "Argument 'flowRunId' is supported only for the flow-run area.");

        try
        {
            return area == "flow-run"
                ? await InspectFlowRunAsync(invocation, flowRunId!, cancellationToken)
                : await InspectInventoryAsync(invocation, area, cancellationToken);
        }
        catch (ToolDefinitionInvocationException) { throw; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ToolDefinitionInvocationException(
                "assistant_diagnostics_unavailable",
                "Diagnostic evidence is unavailable for the current Workspace.",
                exception);
        }
    }

    private async Task<JsonElement> InspectInventoryAsync(
        InternalMcpToolInvocation invocation,
        string area,
        CancellationToken cancellationToken)
    {
        var workspace = await identities.GetWorkspaceAsync(invocation.TenantId, invocation.WorkspaceId.Value, cancellationToken);
        if (workspace is null)
            return Result(area, "unavailable", new object[] { new { source = "identity", observed = "workspace-not-found" } },
                new[] { Finding("observed", "The invocation Workspace was not found in the current Tenant.") }, ["/workspaces"]);

        var inventory = await resources.ListExactInventoryAsync(
            ResourceScopeRef.Workspace(invocation.WorkspaceId.Value), 0, MaximumInventoryEntries, cancellationToken);
        var selected = inventory.Where(value => Matches(area, value.Kind)).ToArray();
        var counts = selected.GroupBy(value => value.Kind, StringComparer.Ordinal)
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => new { kind = value.Key, count = value.Count() })
            .ToArray();
        var truncated = inventory.Count == MaximumInventoryEntries;
        var evidence = new object[]
        {
            new { source = "storage", ready = storage.IsReady },
            new { source = "workspace", name = workspace.Name, status = workspace.Status.ToString().ToLowerInvariant() },
            new { source = "resource-inventory", resourceCounts = counts, inspected = selected.Length, boundedAt = MaximumInventoryEntries, truncated }
        };
        var findings = new List<object>
        {
            Finding("observed", storage.IsReady ? "Storage initialization reports ready." : "Storage initialization does not report ready."),
            Finding("observed", $"The Workspace contains {selected.Length} matching {area} resource(s) in the inspected inventory.")
        };
        if (selected.Length == 0)
            findings.Add(Finding("hypothesis", $"The {area} capability may not be installed or configured in this Workspace."));
        if (truncated)
            findings.Add(Finding("observed", "The resource inventory reached its safety bound; counts may be incomplete."));
        return Result(area, "available", evidence, findings, Navigation(area));
    }

    private async Task<JsonElement> InspectFlowRunAsync(
        InternalMcpToolInvocation invocation,
        string flowRunId,
        CancellationToken cancellationToken)
    {
        FlowRunCausalityPage page;
        try
        {
            page = await flowRuns.GetCausalityAsync(flowRunId, 0, 100,
                new FlowRunScope(invocation.TenantId, invocation.WorkspaceId, invocation.PrincipalId), cancellationToken);
        }
        catch (FlowValidationException exception)
        {
            throw new ToolDefinitionInvocationException("assistant_diagnostics_flow_run_unavailable", "Flow run evidence is unavailable in the current invocation scope.", exception);
        }
        var evidence = page.Items.Select(node => new
        {
            flowRunId = node.FlowRunId,
            flowId = node.FlowId.Value,
            status = node.Status.ToString(),
            node.ParentFlowRunId,
            node.ParentStepName,
            node.NestingDepth,
            node.ErrorCode,
            agents = node.AgentExecutions.Select(agent => new { agent.StepName, status = agent.Status.ToString(), agent.ErrorCode }),
            toolCalls = node.ToolCalls.Select(call => new
            {
                call.StepName,
                call.ToolNamespace,
                call.ToolName,
                call.Status,
                attempts = call.Attempts.Select(attempt => new { attempt.Attempt, attempt.Status, attempt.ErrorCode, attempt.FailureKind, attempt.GovernanceEvaluationCount })
            })
        }).ToArray();
        var failures = page.Items.Count(value => value.ErrorCode is not null || value.Status == FlowRunStatus.Failed);
        var findings = new List<object>
        {
            Finding("observed", $"The causality view contains {page.Items.Count} of {page.TotalCount} run node(s)."),
            Finding("observed", failures == 0 ? "No failed run node was observed in this page." : $"{failures} failed run node(s) were observed in this page.")
        };
        if (failures > 0)
            findings.Add(Finding("hypothesis", "The first failing child, Agent execution, or Tool attempt is a likely investigation boundary; inspect its configuration without exposing runtime values."));
        return Result("flow-run", "available", evidence, findings, ["/flows/runs"]);
    }

    private static bool Matches(string area, string kind) => area switch
    {
        "installation" or "workspace" => true,
        "flows" => kind is "Flow" or "Entry" or "Agent",
        "models" => kind.Contains("Model", StringComparison.OrdinalIgnoreCase) || kind.Contains("RuntimeProfile", StringComparison.OrdinalIgnoreCase),
        "tools" => kind is "Tool" or "ToolProvider" or "ToolPolicy",
        "sources" => kind.Contains("Source", StringComparison.OrdinalIgnoreCase),
        "extensions" => kind.Contains("Extension", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static string[] Navigation(string area) => area switch
    {
        "flows" => ["/flows", "/agents", "/entries"],
        "models" => ["/models", "/runtime-profiles"],
        "tools" => ["/tools"],
        "sources" => ["/sources"],
        "extensions" => ["/extensions"],
        _ => ["/workspaces"]
    };

    private static object Finding(string basis, string statement) => new { basis, statement };
    private static JsonElement Result(string area, string availability, object evidence, object findings, string[] navigation) =>
        JsonSerializer.SerializeToElement(new { area, availability, evidence, findings, navigation });

    private static void ValidateObject(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ToolDefinitionInvocationException("assistant_diagnostics_arguments_invalid", "Diagnostic Tool arguments must be a JSON object.");
        var allowed = new HashSet<string>(["area", "flowRunId"], StringComparer.Ordinal);
        if (arguments.EnumerateObject().Select(value => value.Name).FirstOrDefault(value => !allowed.Contains(value)) is { } unknown)
            throw new ToolDefinitionInvocationException("assistant_diagnostics_argument_unknown", $"Argument '{unknown}' is not declared by the Tool schema.");
    }

    private static string RequiredString(JsonElement arguments, string name) =>
        OptionalString(arguments, name) ?? throw new ToolDefinitionInvocationException("assistant_diagnostics_argument_required", $"Argument '{name}' is required.");

    private static string? OptionalString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
