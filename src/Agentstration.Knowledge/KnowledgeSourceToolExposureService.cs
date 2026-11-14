using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;

namespace Agentstration.Knowledge;

public interface IKnowledgeAccessToolResolver
{
    Task<ToolRelayTarget> ResolveAsync(KnowledgeSourceOperation operation, ResourceScopeRef consumer, CancellationToken cancellationToken);
}

public sealed class KnowledgeSourceToolExposureService(
    KnowledgeSourceManagementService sources,
    ToolDefinitionService definitions,
    ToolSetService toolSets,
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    ICurrentRequestContext requestContext,
    IRequestContextScopeFactory requestScopes,
    ISecurityAuditWriter audit,
    IKnowledgeAccessToolResolver? nativeTools = null, IStorageDocumentGateway? documents = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string KnowledgeSourceUidAnnotation = "agentstration.io/knowledge-source-uid";
    private static readonly KnowledgeSourceOperation[] Operations =
        [KnowledgeSourceOperation.Read, KnowledgeSourceOperation.SearchRegex, KnowledgeSourceOperation.GetStatus];

    public async Task<StoredResource<KnowledgeSourceToolExposureResource>?> GetAsync(
        KnowledgeSourceId id,
        CancellationToken cancellationToken)
    {
        var administrative = KnowledgeOperationalRead.AdministrativeContext(requestContext);
        var source = administrative is null ? null
            : await sources.ResolveForUseAsync(id, KnowledgeOperationalRead.AdministrativeScope(requestContext)!.Value, cancellationToken);
        var scopeRef = administrative is null ? CurrentWorkspaceScope() : ResourceScopeRef.Workspace(administrative.WorkspaceId);
        using var operational = administrative is null ? null : requestScopes.Push(administrative);
        var exposure = await store.GetExactAsync<KnowledgeSourceToolExposureResource>(
            ScopedResourceAddress.Create(scopeRef, id.Namespace,
                KnowledgeResourceKinds.KnowledgeSourceToolExposure, id.Value), cancellationToken);
        return source is null || exposure?.Value.KnowledgeSourceUid == source.Value.Uid ? exposure : null;
    }

    public async Task<StoredResource<KnowledgeSourceToolExposureResource>> PublishAsync(
        KnowledgeSourceId id,
        string version,
        bool requiresApproval,
        CancellationToken cancellationToken)
    {
        var administrative = KnowledgeOperationalRead.AdministrativeContext(requestContext);
        var executionScope = administrative is null ? CurrentWorkspaceScope() : ResourceScopeRef.Workspace(administrative.WorkspaceId);
        var scopeRef = KnowledgeOperationalRead.AdministrativeScope(requestContext) ?? executionScope;
        var source = await sources.ResolveForUseAsync(id, scopeRef, cancellationToken);
        var readiness = await sources.GetReadinessForUseAsync(id, scopeRef, cancellationToken);
        if (!readiness.Ready)
            throw new KnowledgeSourceValidationException("knowledge_source_not_ready", "Only a ready KnowledgeSource can be exposed as Tools.");
        var supportedOperations = Operations;
        StorageQueryProfilePin? queryProfile = null;
        if (source.Value.Definition.Output is { DocumentCollectionUid: { } collection } output)
        {
            if (documents is null) throw new KnowledgeSourceValidationException("knowledge_documents_unavailable", "Document capability discovery is unavailable.");
            var description = await documents.DescribeAsync(new(output.Storage, new(scopeRef, source.Value.Address), collection,
                new(QueryProfiles: null)), cancellationToken);
            if (description.Policy.Query && description.Capabilities.ImmutablePublications)
                queryProfile = description.QueryProfiles.FirstOrDefault();
            supportedOperations = description.Policy.Get ? Operations : [KnowledgeSourceOperation.GetStatus];
            if (queryProfile is not null) supportedOperations = [.. supportedOperations, KnowledgeSourceOperation.StructuredQuery];
        }
        var toolSetName = source.Value.Name + ".access";
        var existingToolSet = await toolSets.GetExactAsync(scopeRef,
            source.Value.Namespace, toolSetName, cancellationToken);
        if (existingToolSet is not null && !IsOwnedBy(existingToolSet.Value.Metadata, source.Value.Uid))
            throw new KnowledgeSourceValidationException("knowledge_source_tool_set_conflict",
                $"ToolSet '{source.Value.Namespace}/{source.Value.Name}' is not owned by this KnowledgeSource.");
        var currentDefinitions = new Dictionary<KnowledgeSourceOperation, StoredResource<ToolDefinitionResource>?>();
        foreach (var operation in supportedOperations)
        {
            var definitionName = $"{source.Value.Name}.access.{KnowledgeAccessSchemas.Route(operation).Replace('_', '-')}";
            var current = await store.GetExactAsync<ToolDefinitionResource>(ScopedResourceAddress.Create(scopeRef,
                source.Value.Namespace, ToolResourceKinds.ToolDefinition, definitionName), cancellationToken);
            if (current is not null && !IsOwnedBy(current.Value.Metadata, source.Value.Uid))
                throw new KnowledgeSourceValidationException("knowledge_source_tool_definition_conflict",
                    $"ToolDefinition '{source.Value.Namespace}/{definitionName}' is not owned by this KnowledgeSource.");
            currentDefinitions.Add(operation, current);
        }
        var operations = new List<KnowledgeSourceToolOperationExposure>(supportedOperations.Length);
        var members = new List<ToolSetMember>(supportedOperations.Length);
        foreach (var operation in supportedOperations)
        {
            var route = KnowledgeAccessSchemas.Route(operation);
            var definitionName = $"{source.Value.Name}.access.{route.Replace('_', '-')}";
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
                        ["agentstration.io/knowledge-source-scope"] = source.Value.ScopeRef!.Value.Value,
                        ["agentstration.io/knowledge-operation"] = route
                    }
                },
                ScopeRef = scopeRef,
                Definition = new RelayToolDefinitionProperties
                {
                    Version = version,
                    Publish = true,
                    DisplayName = $"{source.Value.Definition.DisplayName} {route}",
                    Description = operation switch
                    {
                        KnowledgeSourceOperation.StructuredQuery => "Query structured documents in an exact immutable Snapshot. The relay fixes the query profile and model. Initial requests provide query; continuations provide cursor only. No document writes, SQL or physical destination selection. Profile: " + queryProfile!.Id + "/" + queryProfile.Version,
                        KnowledgeSourceOperation.Read => "Read Unicode character chunks. Without a Snapshot or cursor, prepare current data if necessary. Continue with cursor only; a cursor stays on the same Snapshot. Reuse search_regex snapshotId, documentId and readRange as range.",
                        KnowledgeSourceOperation.SearchRegex => "Search a Snapshot using a bounded .NET non-backtracking regular expression. May be called before read; prepares data if necessary. Returns Unicode matchRange and readRange for read. Continue with cursor only. Unsupported lookarounds and backreferences are rejected.",
                        _ => "Inspect active and latest published Snapshots and input freshness without starting acquisition or projection. Call with request {} or {snapshotId: null} for current status. Only supply a non-empty snapshotId returned by Knowledge access to compare a specific Snapshot. Freshness is recorded evidence, not a guarantee about the remote origin."
                    },
                    RequiresApproval = requiresApproval,
                    Target = nativeTools is not null ? await nativeTools.ResolveAsync(operation, scopeRef, cancellationToken)
                        : throw new KnowledgeSourceValidationException("knowledge_access_tools_unavailable", "The platform Knowledge Tools are unavailable."),
                    Bindings = [new LiteralToolArgumentBinding
                    {
                        Path = "/source", Value = JsonSerializer.SerializeToElement(new
                        {
                            name = source.Value.Name, @namespace = source.Value.Namespace.Value,
                            scopeRef = source.Value.ScopeRef!.Value.Value, uid = source.Value.Uid
                        })
                    }, .. operation == KnowledgeSourceOperation.StructuredQuery
                        ? new ToolArgumentBinding[] { new LiteralToolArgumentBinding { Path = "/request/profile", Value = JsonSerializer.SerializeToElement(queryProfile, Json) } }
                        : []]
                }
            }, current?.ETag, current is null, cancellationToken);

            var toolName = AgentstrationToolProvider.ToolResourceName(ToolDefinitionService.VersionName(definitionName, version));
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
                    Name = toolSetName,
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
            _ = await toolSets.PutAsync(source.Value.Namespace, toolSetName, toolSetDefinition,
                existingToolSet.ETag, cancellationToken);
        }

        var existing = await GetAsync(id, cancellationToken);
        var exposure = new KnowledgeSourceToolExposureResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSourceToolExposure,
            Metadata = new ResourceMetadata { Name = source.Value.Name, Namespace = source.Value.Namespace },
            ScopeRef = executionScope,
            Uid = existing?.Value.Uid ?? Guid.Empty,
            Generation = existing is null ? 1 : checked(existing.Value.Generation + 1),
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
            KnowledgeSourceUid = source.Value.Uid,
            KnowledgeSourceScopeRef = source.Value.ScopeRef,
            KnowledgeSourceName = source.Value.Name,
            KnowledgeSourceGeneration = source.Value.Generation,
            RetrievalFlow = readiness.Retrieval,
            ToolSet = new ResourceReference(toolSetName, scopeRef, source.Value.Namespace),
            ToolSetVersion = version,
            Operations = operations
        };
        StoredResource<KnowledgeSourceToolExposureResource> stored;
        using (administrative is null ? null : requestScopes.Push(administrative))
            stored = await scopeOperations.WriteAsync(exposure, executionScope, AuthorizationPermissions.ResourcesWrite,
                token => store.PutExactAsync(executionScope, exposure, existing?.ETag, existing is null, token), cancellationToken);
        await audit.WriteAsync(new(SecurityAuditActions.KnowledgeSourceToolExposurePublished,
            WorkspaceId: executionScope.TargetId), cancellationToken);
        return stored;
    }

    private ResourceScopeRef CurrentWorkspaceScope()
    {
        if (!requestContext.IsInitialized)
            throw new KnowledgeSourceValidationException("knowledge_source_context_required",
                "A Workspace request context is required to materialize Knowledge Tools.");
        return ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId);
    }

    private static bool IsOwnedBy(ResourceMetadata metadata, Guid sourceUid) =>
        metadata.Annotations.TryGetValue(KnowledgeSourceUidAnnotation, out var owner)
        && Guid.TryParse(owner, out var ownerUid)
        && ownerUid == sourceUid;

}
