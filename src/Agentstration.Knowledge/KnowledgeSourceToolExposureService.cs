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
    async Task<IReadOnlyDictionary<KnowledgeSourceOperation, ToolRelayTarget>> ResolveManyAsync(
        IReadOnlyCollection<KnowledgeSourceOperation> operations, ResourceScopeRef consumer, CancellationToken ct)
    {
        var result = new Dictionary<KnowledgeSourceOperation, ToolRelayTarget>();
        foreach (var operation in operations.Distinct()) result.Add(operation, await ResolveAsync(operation, consumer, ct));
        return result;
    }
}

public sealed partial class KnowledgeSourceToolExposureService(
    KnowledgeSourceManagementService sources,
    ToolDefinitionService definitions,
    ToolSetService toolSets,
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    ICurrentRequestContext requestContext,
    IRequestContextScopeFactory requestScopes,
    ISecurityAuditWriter audit,
    IKnowledgeAccessToolResolver? nativeTools = null, IStorageDocumentGateway? documents = null, KnowledgeSnapshotService? snapshots = null,
    KnowledgeToolMetadataService? metadata = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string KnowledgeSourceUidAnnotation = "agentstration.io/knowledge-source-uid";

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
        CancellationToken cancellationToken, string? proposalDigest = null)
    {
        var administrative = KnowledgeOperationalRead.AdministrativeContext(requestContext);
        var executionScope = administrative is null ? CurrentWorkspaceScope() : ResourceScopeRef.Workspace(administrative.WorkspaceId);
        var scopeRef = KnowledgeOperationalRead.AdministrativeScope(requestContext) ?? executionScope;
        var source = await sources.ResolveForUseAsync(id, scopeRef, cancellationToken);
        var readiness = await sources.GetReadinessForUseAsync(id, scopeRef, cancellationToken);
        if (!readiness.Ready)
            throw new KnowledgeSourceValidationException("knowledge_source_not_ready", "Only a ready KnowledgeSource can be exposed as Tools.");
        var proposal = await ProposeAsync(id, cancellationToken);
        if (proposalDigest is not null && proposalDigest != proposal.Digest)
            throw new KnowledgeSourceValidationException("knowledge_tool_proposal_stale", "The Tool proposal changed. Refresh and review it before publication.");
        if (proposal.Issues.FirstOrDefault(issue => issue is "knowledge_source_tool_set_conflict" or "knowledge_source_tool_definition_conflict") is { } conflict)
            throw new KnowledgeSourceValidationException(conflict, $"A Tool resource name for '{source.Value.Name}' is owned by another resource.");
        if (!proposal.Ready) throw new KnowledgeSourceValidationException("knowledge_source_not_ready", "The Tool proposal is not ready.");
        var proposed = proposal.Tools.Where(t => t.Issue is null && t.Change != "removed").ToArray();
        var exposureName = "exposure-" + Digest(new { source.Value.Uid, proposal.Digest, version, scopeRef })[..32];
        var toolSetName = source.Value.Name + ".access";
        var existingToolSet = await toolSets.GetExactAsync(scopeRef,
            source.Value.Namespace, toolSetName, cancellationToken);
        if (existingToolSet is not null && !IsOwnedBy(existingToolSet.Value.Metadata, source.Value.Uid))
            throw new KnowledgeSourceValidationException("knowledge_source_tool_set_conflict",
                $"ToolSet '{source.Value.Namespace}/{source.Value.Name}' is not owned by this KnowledgeSource.");
        var currentDefinitions = new Dictionary<string, StoredResource<ToolDefinitionResource>?>(StringComparer.Ordinal);
        foreach (var candidate in proposed)
        {
            var definitionName = candidate.DefinitionName;
            var current = await store.GetExactAsync<ToolDefinitionResource>(ScopedResourceAddress.Create(scopeRef,
                source.Value.Namespace, ToolResourceKinds.ToolDefinition, definitionName), cancellationToken);
            if (current is not null && !IsOwnedBy(current.Value.Metadata, source.Value.Uid))
                throw new KnowledgeSourceValidationException("knowledge_source_tool_definition_conflict",
                    $"ToolDefinition '{source.Value.Namespace}/{definitionName}' is not owned by this KnowledgeSource.");
            currentDefinitions.Add(candidate.Route, current);
        }
        var targets = nativeTools is not null
            ? await nativeTools.ResolveManyAsync(proposed.Select(t => t.Operation).Distinct().ToArray(), scopeRef, cancellationToken)
            : throw new KnowledgeSourceValidationException("knowledge_access_tools_unavailable", "The platform Knowledge Tools are unavailable.");
        var operations = new List<KnowledgeSourceToolOperationExposure>(proposed.Length);
        var members = new List<ToolSetMember>(proposed.Length);
        foreach (var candidate in proposed)
        {
            var operation = candidate.Operation;
            var route = candidate.Route;
            var definitionName = candidate.DefinitionName;
            var current = currentDefinitions[route];
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
                        [KnowledgeToolMetadataService.UsageAnnotation] = candidate.Usage ?? candidate.Description,
                        ["agentstration.io/knowledge-source-scope"] = source.Value.ScopeRef!.Value.Value,
                        ["agentstration.io/knowledge-operation"] = route
                    }
                },
                ScopeRef = scopeRef,
                Definition = new RelayToolDefinitionProperties
                {
                    Version = version,
                    Publish = true,
                    DisplayName = candidate.DisplayName,
                    Description = candidate.Description,
                    RequiresApproval = requiresApproval,
                    Target = targets[operation],
                    Bindings = [new LiteralToolArgumentBinding
                    {
                        Path = "/source", Value = JsonSerializer.SerializeToElement(new
                        {
                            name = source.Value.Name, @namespace = source.Value.Namespace.Value,
                            scopeRef = source.Value.ScopeRef!.Value.Value, uid = source.Value.Uid
                        })
                    }, .. candidate.Profile is not null
                        ? new ToolArgumentBinding[] { new LiteralToolArgumentBinding { Path = "/request/profile", Value = JsonSerializer.SerializeToElement(candidate.Profile, Json) } } : [],
                    .. candidate.Contribution is not null
                        ? new ToolArgumentBinding[] { new LiteralToolArgumentBinding { Path = "/request/contribution", Value = JsonSerializer.SerializeToElement(candidate.Contribution, Json) } } : [],
                    .. operation == KnowledgeSourceOperation.Describe
                        ? new ToolArgumentBinding[] { new LiteralToolArgumentBinding { Path = "/request/exposure", Value = JsonSerializer.SerializeToElement(exposureName, Json) },
                            new LiteralToolArgumentBinding { Path = "/request/exposureScope", Value = JsonSerializer.SerializeToElement(scopeRef.Value, Json) } } : []]
                }
            }, current?.ETag, current is null, cancellationToken);

            var toolName = AgentstrationToolProvider.ToolResourceName(ToolDefinitionService.VersionName(definitionName, version));
            var tool = new ResourceReference(toolName, scopeRef, source.Value.Namespace);
            var capability = $"knowledge.{route}";
            operations.Add(new KnowledgeSourceToolOperationExposure
            {
                CallableName = ToolDefinitionService.VersionName(definitionName, version),
                Proposal = candidate,
                Operation = operation,
                Tool = tool,
                Capability = capability,
                Route = route
            });
            members.Add(new ToolSetMember { Tool = tool, Capability = capability, Route = route });
        }

        var latest = await sources.ResolveForUseAsync(id, scopeRef, cancellationToken);
        if (latest.Value.Generation != proposal.SourceGeneration || latest.Value.Uid != proposal.SourceUid)
            throw new KnowledgeSourceValidationException("knowledge_tool_proposal_stale", "Refresh and review the changed configuration.");
        var evidence = new KnowledgeToolExposureVersion
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeToolExposureVersion,
            Metadata = new() { Name = exposureName, Namespace = source.Value.Namespace },
            ScopeRef = scopeRef,
            SourceUid = source.Value.Uid,
            DisplayName = source.Value.Definition.DisplayName,
            Description = source.Value.Definition.Description,
            Version = version,
            Proposal = proposal,
            Operations = operations
        };
        _ = await scopeOperations.WriteAsync(evidence, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.CreateImmutableAsync(evidence, token), cancellationToken);

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
