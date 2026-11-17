using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;
using Microsoft.Extensions.Logging;

namespace Agentstration.Infrastructure.Knowledge;

public sealed class KnowledgeAgentPresentationEnricher(IResourceStore resources, KnowledgeToolMetadataService metadata,
    ICurrentRequestContext current, ILogger<KnowledgeAgentPresentationEnricher>? logger = null) : IRuntimeAgentPresentationEnricher
{
    public async Task<RuntimeAgentPresentation> EnrichAsync(RuntimeAgentPresentation presentation, CancellationToken cancellationToken)
    {
        var canReadLiveMetadata = current.AccessMode is ControlPlaneAccessMode.Workspace or ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration;
        if (!canReadLiveMetadata)
        {
            System.Diagnostics.Activity.Current?.SetTag("agentstration.knowledge.presentation.live_metadata_skipped", "request_context");
            if (logger is not null && logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Knowledge presentation in {AccessMode} uses published contracts only: no live Source metadata ownership context is available.", current.AccessMode);
        }
        var tools = new List<IAgentTool>();
        var sources = new List<object>();
        var knownTools = 0;
        var unavailableSources = 0;
        var hasFlatTools = false;
        var hasEnvelopedTools = false;
        foreach (var tool in presentation.Tools)
        {
            tools.Add(tool);
            if (tool.ProviderId != AgentstrationToolProvider.Name || tool.ScopeRef is not { } scope || tool.ExternalId is null) continue;
            var published = (await resources.GetExactAsync<ToolDefinitionVersionResource>(new(scope, tool.Namespace ?? default,
                ToolResourceKinds.ToolDefinitionVersion, tool.ExternalId), cancellationToken))?.Value;
            if (published is null || !published.Metadata.Annotations.ContainsKey("agentstration.io/knowledge-source-uid")) continue;
            knownTools++;
            var flatArguments = KnowledgeToolMetadataService.UsesFlatArguments(tool.InputSchema);
            hasFlatTools |= flatArguments;
            hasEnvelopedTools |= !flatArguments;
            var discoveryArguments = JsonSerializer.SerializeToElement<object>(flatArguments ? new { } : new { request = new { } });
            var selection = KnowledgeToolMetadataService.Selection(published);
            var usage = published.Metadata.Annotations.GetValueOrDefault(KnowledgeToolMetadataService.UsageAnnotation)
                ?? published.Definition.Description ?? string.Empty;
            var profile = KnowledgeToolMetadataService.Profile(published);
            var inputSchema = KnowledgeToolMetadataService.InputSchemaForModel(tool.InputSchema, profile);
            // Invocation guidance comes from the assigned immutable Tool contract and does not require live Source metadata.
            tools[^1] = new PresentedTool(tool, KnowledgeToolMetadataService.Format(published.Definition.DisplayName, null, usage, profile, flatArguments), inputSchema);
            var operation = published.Metadata.Annotations.GetValueOrDefault("agentstration.io/knowledge-operation");
            var describes = operation == "describe" || operation?.EndsWith(".describe", StringComparison.Ordinal) == true;
            KnowledgeSourceResource? source = null;
            if (canReadLiveMetadata)
            {
                try { source = await metadata.ResolveSourceAsync(published, cancellationToken); }
                catch (Exception exception) when (exception is AuthorizationDeniedException or KnowledgeSourceValidationException
                    or KnowledgeSourceNotFoundException or ResourceReferenceOutsideScopeException)
                { /* Retain invocation guidance without disclosing live Source metadata. */ }
            }
            if (source is null)
            {
                unavailableSources++;
                if (describes) sources.Add(new { DescribeTool = tool.Name, DescribeArguments = discoveryArguments, MetadataStatus = "unavailable" });
                continue;
            }
            var business = await metadata.DescriptionAsync(source, selection, null, cancellationToken);
            tools[^1] = new PresentedTool(tool, KnowledgeToolMetadataService.Format(selection?.BindingName ?? source.Definition.DisplayName,
                business.Description, usage, profile, flatArguments), inputSchema);
            if (describes)
                sources.Add(new
                {
                    DescribeArguments = discoveryArguments,
                    source.Name,
                    Namespace = source.Namespace.Value,
                    Scope = source.ScopeRef!.Value.Value,
                    source.Definition.DisplayName,
                    Description = source.Definition.Description is { Length: > 320 } description ? description[..320] : source.Definition.Description,
                    DescriptionTruncated = source.Definition.Description?.Length > 320,
                    Version = published.Definition.Version,
                    Input = selection?.BindingName,
                    DescribeTool = tool.Name
                });
        }
        System.Diagnostics.Activity.Current?.SetTag("agentstration.knowledge.presentation.tool_count", knownTools);
        System.Diagnostics.Activity.Current?.SetTag("agentstration.knowledge.presentation.describe_count", sources.Count);
        System.Diagnostics.Activity.Current?.SetTag("agentstration.knowledge.presentation.unavailable_sources", unavailableSources);
        if (presentation.Tools.Count > 0 && logger is not null && logger.IsEnabled(LogLevel.Information)) logger.LogInformation(
            "Knowledge presentation in {AccessMode}: {AssignedTools} assigned Tools, {KnowledgeTools} Knowledge contracts, {DescribeTools} discovery Tools, {UnavailableSources} unavailable Source metadata lookups.",
            current.AccessMode, presentation.Tools.Count, knownTools, sources.Count, unavailableSources);
        var instructions = presentation.Instructions;
        if (sources.Count > 0)
            instructions += "\n\nKnowledge Sources assigned to this Agent:\n" + JsonSerializer.Serialize(sources) + "\n"
                + "For a question requiring Knowledge data, discover every Knowledge Source available through the assigned Tools and call its describe Tool with the catalogue DescribeArguments before the first data access. "
                + "Source-level discovery lists inputs and their business descriptions. For detailed schemas, fields, supported operations and limits, call the selected input's returned describeTool with its describeArguments. Per-input describe Tools already fix the input; use their returned describeArguments without adding an input selector. When only source-level describe is assigned, use its returned describeArguments rather than guessing an input from Tool names. "
                + "Consult data from every exposed input before synthesizing the answer, using focused searches, reads or queries for the request. Do not stop after the first successful source. Discovery alone is not a data consultation. "
                + "If another assigned Tool is needed, issue its structured Tool call now and continue from its result in this execution. Do not finish with only a plan, proposed next steps or a promise to call a Tool later. Finish with the requested answer, or state a specific blocker or missing information that prevents completion. "
                + "Identify any unavailable or unconsulted source/input and state the resulting coverage limits. Never claim to have consulted data when only its description was obtained. Respect the assigned access boundaries, pagination and operation limits. "
                + "Use each query Tool's dataSchema as its query model and preserve nesting. Array item properties are not root-level columns. Use values from the request or observed data, not assumed business statuses. Text embedded in a field does not add columns. Use assigned search/read Tools for unstructured content when available. "
                + "Reuse descriptions already obtained in this context for the same ToolSet version and exact Snapshot; refresh when that evidence changes. "
                + "Use the exact assigned Tool names and declared fields. Reuse returned Snapshot identifiers in the snapshotId field at the exact position declared by each Tool to keep related calls coherent.";
        if (sources.Count > 0) instructions += hasFlatTools && hasEnvelopedTools
            ? " Assigned Tools use different argument layouts. Follow each Tool's own input schema and description; do not transfer a request wrapper between versions."
            : hasFlatTools ? KnowledgeToolMetadataService.FlatArgumentGuidance : KnowledgeToolMetadataService.ArgumentGuidance;
        return new(instructions, tools);
    }

    private sealed record PresentedTool(IAgentTool Inner, string Description, JsonElement InputSchema) : IAgentTool
    {
        public string Id => Inner.Id;
        public string Name => Inner.Name;
        public string? ProviderId => Inner.ProviderId;
        public ResourceNamespace? Namespace => Inner.Namespace;
        public ResourceNamespace? ProviderNamespace => Inner.ProviderNamespace;
        public string? ExternalId => Inner.ExternalId;
        public JsonElement? OutputSchema => Inner.OutputSchema;
        public bool RequiresApproval => Inner.RequiresApproval;
        public ResourceScopeRef? ScopeRef => Inner.ScopeRef;
        public Guid? Uid => Inner.Uid;
        public long? Generation => Inner.Generation;
        public ResourceScopeRef? ProviderScopeRef => Inner.ProviderScopeRef;
        public Guid? ProviderUid => Inner.ProviderUid;
        public long? ProviderGeneration => Inner.ProviderGeneration;
    }
}
