using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components.Models;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Console;

public sealed class ToolGovernanceAuditApiClient(HttpClient httpClient) : IToolGovernanceAuditClient
{
    public Task<ToolGovernanceAuditPage> GetAsync(
        ToolExecutionOwnerKind ownerKind,
        string runId,
        long afterSequence,
        int limit,
        ToolGovernanceAuditFilters filters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var owner = ownerKind switch
        {
            ToolExecutionOwnerKind.RuntimeRun => "runtime",
            ToolExecutionOwnerKind.FlowRun => "flow",
            _ => throw new ArgumentOutOfRangeException(nameof(ownerKind))
        };
        var query = new List<string>
        {
            $"afterSequence={afterSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        };
        Add(query, "toolCallId", filters.ToolCallId);
        Add(query, "invocationId", filters.InvocationId);
        Add(query, "toolId", filters.ToolId);
        Add(query, "hookId", filters.HookId);
        if (filters.ResourceGeneration is { } generation)
            query.Add($"resourceGeneration={generation.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (filters.Decision is { } decision)
            query.Add($"decision={Uri.EscapeDataString(decision.ToString().ToLowerInvariant())}");
        return ApiResponse.ReadAsync<ToolGovernanceAuditPage>(
            httpClient,
            $"api/tool-governance/{owner}/{Uri.EscapeDataString(runId)}?{string.Join('&', query)}",
            cancellationToken);
    }

    private static void Add(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) query.Add($"{name}={Uri.EscapeDataString(value)}");
    }
}

