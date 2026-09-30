using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;
using Agentstration.Tools;

namespace Agentstration.Knowledge;

public sealed class KnowledgeSourceToolExposureService(
    KnowledgeSourceManagementService sources,
    ToolDefinitionService definitions,
    ToolSetService toolSets,
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    ISecurityAuditWriter audit)
{
    private const string KnowledgeSourceUidAnnotation = "agentstration.io/knowledge-source-uid";
    private static readonly KnowledgeSourceOperation[] Operations =
        [KnowledgeSourceOperation.Search, KnowledgeSourceOperation.Query, KnowledgeSourceOperation.Read];

    public Task<StoredResource<KnowledgeSourceToolExposureResource>?> GetAsync(
        KnowledgeSourceId id,
        CancellationToken cancellationToken) =>
        store.GetAsync<KnowledgeSourceToolExposureResource>(
            new(KnowledgeResourceKinds.KnowledgeSourceToolExposure, id.Value, id.Namespace), cancellationToken);

    public async Task<StoredResource<KnowledgeSourceToolExposureResource>> PublishAsync(
        KnowledgeSourceId id,
        string version,
        bool requiresApproval,
        CancellationToken cancellationToken)
    {
        var source = await sources.GetAsync(id, cancellationToken) ?? throw new KnowledgeSourceNotFoundException(id);
        var scopeRef = source.Value.ScopeRef
            ?? throw new KnowledgeSourceValidationException("knowledge_source_scope_invalid", "A KnowledgeSource requires an ownership scope.");
        var readiness = await sources.GetReadinessAsync(id, cancellationToken);
        var retrieval = readiness.Retrieval
            ?? throw new KnowledgeSourceValidationException("knowledge_source_retrieval_flow_unavailable", "The KnowledgeSource retrieval Flow is unavailable.");
        if (!readiness.Ready)
            throw new KnowledgeSourceValidationException("knowledge_source_not_ready", "Only a ready KnowledgeSource can be exposed as Tools.");
        var existingToolSet = await toolSets.GetAsync(source.Value.Namespace, source.Value.Name, cancellationToken);
        if (existingToolSet is not null
            && (existingToolSet.Value.ScopeRef != scopeRef || !IsOwnedBy(existingToolSet.Value.Metadata, source.Value.Uid)))
            throw new KnowledgeSourceValidationException("knowledge_source_tool_set_conflict",
                $"ToolSet '{source.Value.Namespace}/{source.Value.Name}' is not owned by this KnowledgeSource.");
        var currentDefinitions = new Dictionary<KnowledgeSourceOperation, StoredResource<ToolDefinitionResource>?>();
        foreach (var operation in Operations)
        {
            var definitionName = $"{source.Value.Name}.{operation.ToString().ToLowerInvariant()}";
            var current = await definitions.GetAsync(definitionName, source.Value.Namespace, cancellationToken);
            if (current is not null && !IsOwnedBy(current.Value.Metadata, source.Value.Uid))
                throw new KnowledgeSourceValidationException("knowledge_source_tool_definition_conflict",
                    $"ToolDefinition '{source.Value.Namespace}/{definitionName}' is not owned by this KnowledgeSource.");
            currentDefinitions.Add(operation, current);
        }
        var publicSchema = PublicSchema(retrieval.InputSchema);
        var operations = new List<KnowledgeSourceToolOperationExposure>(Operations.Length);
        var members = new List<ToolSetMember>(Operations.Length);
        foreach (var operation in Operations)
        {
            var route = operation.ToString().ToLowerInvariant();
            var definitionName = $"{source.Value.Name}.{route}";
            var current = currentDefinitions[operation];
            _ = await definitions.PutAsync(new ToolDefinitionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolDefinition,
                Metadata = new ResourceMetadata
                {
                    Name = definitionName,
                    Namespace = source.Value.Namespace,
                    Tags = new Dictionary<string, string> { ["agentstration.io/category"] = "knowledge-source" },
                    Annotations = new Dictionary<string, string>
                    {
                        ["agentstration.io/knowledge-source"] = source.Value.Name,
                        [KnowledgeSourceUidAnnotation] = source.Value.Uid.ToString("D"),
                        ["agentstration.io/knowledge-operation"] = route
                    }
                },
                ScopeRef = scopeRef,
                Definition = new ToolDefinitionProperties
                {
                    DisplayName = $"{source.Value.Definition.DisplayName} {route}",
                    Description = $"{route} operation for KnowledgeSource '{source.Value.Name}'.",
                    RequiresApproval = requiresApproval,
                    InputSchema = publicSchema.Clone(),
                    FixedArguments = JsonSerializer.SerializeToElement(new
                    {
                        knowledgeSourceId = ToolResourceIdentity.CatalogId(source.Value.Namespace, source.Value.Name),
                        operation = route
                    }),
                    OutputSchema = retrieval.OutputSchema?.Clone(),
                    Flow = new ToolDefinitionFlowTarget
                    {
                        Name = retrieval.Name,
                        Namespace = retrieval.Namespace,
                        Version = retrieval.Version,
                        UseActiveVersion = false
                    }
                }
            }, current?.ETag, current is null, cancellationToken);

            var toolName = AgentstrationToolProvider.ToolResourceName(definitionName);
            var tool = new ResourceReference(toolName, scopeRef, source.Value.Namespace);
            var capability = $"knowledge.{route}";
            operations.Add(new KnowledgeSourceToolOperationExposure
            {
                Operation = operation,
                Tool = tool,
                Capability = capability,
                Route = route
            });
            members.Add(new ToolSetMember { Tool = tool, Capability = capability, Route = route });
        }

        var toolSetDefinition = new ToolSetProperties
        {
            DisplayName = source.Value.Definition.DisplayName,
            Description = $"Governed ToolSet for KnowledgeSource '{source.Value.Name}'.",
            Category = new ResourceReference("knowledge-source"),
            Version = version,
            Publish = true,
            Members = members
        };
        if (existingToolSet is null)
        {
            _ = await toolSets.CreateAsync(new ToolSetResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolSet,
                Metadata = new ResourceMetadata
                {
                    Name = source.Value.Name,
                    Namespace = source.Value.Namespace,
                    Tags = new Dictionary<string, string> { ["agentstration.io/category"] = "knowledge-source" },
                    Annotations = new Dictionary<string, string>
                    {
                        ["agentstration.io/knowledge-source"] = source.Value.Name,
                        [KnowledgeSourceUidAnnotation] = source.Value.Uid.ToString("D")
                    }
                },
                ScopeRef = scopeRef,
                Definition = toolSetDefinition
            }, cancellationToken);
        }
        else
        {
            _ = await toolSets.PutAsync(source.Value.Namespace, source.Value.Name, toolSetDefinition,
                existingToolSet.ETag, cancellationToken);
        }

        var existing = await GetAsync(id, cancellationToken);
        var exposure = new KnowledgeSourceToolExposureResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSourceToolExposure,
            Metadata = new ResourceMetadata { Name = source.Value.Name, Namespace = source.Value.Namespace },
            ScopeRef = scopeRef,
            Uid = existing?.Value.Uid ?? Guid.Empty,
            Generation = existing is null ? 1 : checked(existing.Value.Generation + 1),
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
            KnowledgeSourceUid = source.Value.Uid,
            KnowledgeSourceName = source.Value.Name,
            KnowledgeSourceGeneration = source.Value.Generation,
            RetrievalFlow = retrieval,
            ToolSet = new ResourceReference(source.Value.Name, scopeRef, source.Value.Namespace),
            ToolSetVersion = version,
            Operations = operations
        };
        var stored = await scopeOperations.WriteAsync(exposure, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, exposure, existing?.ETag, existing is null, token), cancellationToken);
        await audit.WriteAsync(new(SecurityAuditActions.KnowledgeSourceToolExposurePublished,
            WorkspaceId: scopeRef.TargetId), cancellationToken);
        return stored;
    }

    private static bool IsOwnedBy(ResourceMetadata metadata, Guid sourceUid) =>
        metadata.Annotations.TryGetValue(KnowledgeSourceUidAnnotation, out var owner)
        && Guid.TryParse(owner, out var ownerUid)
        && ownerUid == sourceUid;

    private static JsonElement PublicSchema(JsonElement? inputSchema)
    {
        if (inputSchema is not { ValueKind: JsonValueKind.Object } schema)
            throw new KnowledgeSourceValidationException("knowledge_source_retrieval_input_schema_invalid", "The retrieval Flow requires an object input schema.");
        var root = JsonNode.Parse(schema.GetRawText())?.AsObject()
            ?? throw new KnowledgeSourceValidationException("knowledge_source_retrieval_input_schema_invalid", "The retrieval Flow input schema is invalid.");
        if (root["properties"] is not JsonObject properties
            || !properties.ContainsKey("knowledgeSourceId")
            || !properties.ContainsKey("operation"))
            throw new KnowledgeSourceValidationException("knowledge_source_retrieval_contract_invalid", "The retrieval Flow must declare knowledgeSourceId and operation inputs.");
        properties.Remove("knowledgeSourceId");
        properties.Remove("operation");
        if (root["required"] is JsonArray required)
            for (var index = required.Count - 1; index >= 0; index--)
                if (required[index]?.GetValue<string>() is "knowledgeSourceId" or "operation") required.RemoveAt(index);
        return JsonSerializer.SerializeToElement(root);
    }
}
