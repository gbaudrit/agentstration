using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.ResourceManagement;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Knowledge;

public sealed class KnowledgeAgentPresentationEnricher(IResourceStore resources, KnowledgeToolMetadataService metadata,
    ICurrentRequestContext current) : IRuntimeAgentPresentationEnricher
{
    public async Task<RuntimeAgentPresentation> EnrichAsync(RuntimeAgentPresentation presentation, CancellationToken cancellationToken)
    {
        if (current.AccessMode is not (ControlPlaneAccessMode.Workspace or ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration))
            return presentation;
        var tools = new List<IAgentTool>();
        var sources = new List<object>();
        foreach (var tool in presentation.Tools)
        {
            tools.Add(tool);
            if (tool.ProviderId != AgentstrationToolProvider.Name || tool.ScopeRef is not { } scope || tool.ExternalId is null) continue;
            var published = (await resources.GetExactAsync<ToolDefinitionVersionResource>(new(scope, tool.Namespace ?? default,
                ToolResourceKinds.ToolDefinitionVersion, tool.ExternalId), cancellationToken))?.Value;
            if (published is null || !published.Metadata.Annotations.ContainsKey("agentstration.io/knowledge-source-uid")) continue;
            KnowledgeSourceResource? source;
            try { source = await metadata.ResolveSourceAsync(published, cancellationToken); }
            catch (Exception exception) when (exception is AuthorizationDeniedException or KnowledgeSourceValidationException
                or KnowledgeSourceNotFoundException or ResourceReferenceOutsideScopeException)
            { continue; }
            if (source is null) continue;
            var selection = KnowledgeToolMetadataService.Selection(published);
            var business = await metadata.DescriptionAsync(source, selection, null, cancellationToken);
            var usage = published.Metadata.Annotations.GetValueOrDefault(KnowledgeToolMetadataService.UsageAnnotation)
                ?? published.Definition.Description ?? string.Empty;
            tools[^1] = new PresentedTool(tool, KnowledgeToolMetadataService.Format(selection?.BindingName ?? source.Definition.DisplayName,
                business.Description, usage));
            if (published.Metadata.Annotations.GetValueOrDefault("agentstration.io/knowledge-operation") == "describe")
                sources.Add(new
                {
                    source.Name,
                    Namespace = source.Namespace.Value,
                    Scope = source.ScopeRef!.Value.Value,
                    source.Definition.DisplayName,
                    Description = source.Definition.Description is { Length: > 320 } description ? description[..320] : source.Definition.Description,
                    DescriptionTruncated = source.Definition.Description?.Length > 320,
                    Version = published.Definition.Version,
                    DescribeTool = tool.Name
                });
        }
        var instructions = presentation.Instructions;
        if (sources.Count > 0)
            instructions += "\n\nKnowledge Sources assigned to this Agent:\n" + JsonSerializer.Serialize(sources) + "\n"
                + "For a question requiring Knowledge data, choose a Knowledge Source from this catalogue and call its describe Tool with request: {} before the first data access. "
                + "This lightweight discovery lists that source's inputs and their business descriptions. Call the same describe Tool with request.input set to the selected binding name for its schemas, fields, supported operations and limits before constructing a query that needs them. "
                + "Reuse descriptions already obtained in this context for the same ToolSet version and exact Snapshot; refresh when that evidence changes. "
                + "Use the exact assigned Tool names and declared fields. Reuse returned snapshotId values to keep related calls coherent.";
        return new(instructions, tools);
    }

    private sealed record PresentedTool(IAgentTool Inner, string Description) : IAgentTool
    {
        public string Id => Inner.Id;
        public string Name => Inner.Name;
        public string? ProviderId => Inner.ProviderId;
        public ResourceNamespace? Namespace => Inner.Namespace;
        public ResourceNamespace? ProviderNamespace => Inner.ProviderNamespace;
        public string? ExternalId => Inner.ExternalId;
        public JsonElement InputSchema => Inner.InputSchema;
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
