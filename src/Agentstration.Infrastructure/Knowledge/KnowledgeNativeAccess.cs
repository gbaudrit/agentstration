using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Artifacts.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Notifications;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Infrastructure.Knowledge;

public sealed class KnowledgeAccessTokenProtector(IDataProtectionProvider provider, IHostApplicationLifetime? lifetime = null)
    : IKnowledgeAccessTokenProtector, IKnowledgeAccessLifetime
{
    private readonly IDataProtector protector = provider.CreateProtector("Agentstration.Knowledge.NativeAccess.v1");
    public CancellationToken Stopping => lifetime?.ApplicationStopping ?? CancellationToken.None;
    public string Protect(string value) => protector.Protect(value);
    public string Unprotect(string value) => protector.Unprotect(value);
}

public sealed class KnowledgeSnapshotContentReader(IStoragePublicationGateway publications,
    IArtifactDurableStore durable, IResourceStore resources, ICurrentRequestContext current,
    IRequestContextScopeFactory scopes, IAuthorizationService authorization) : IKnowledgeSnapshotContentReader
{
    public async Task<ReadOnlyMemory<byte>> ReadAsync(KnowledgeSnapshotResource snapshot, KnowledgeSnapshotArtifact artifact, CancellationToken cancellationToken)
    {
        var context = current.Current;
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.ArtifactsReadContent, cancellationToken);
        if (artifact.Length is < 0 or > KnowledgeAccessService.MaximumDocumentBytes)
            throw new KnowledgeAccessException("knowledge_access_document_limit", "The document exceeds the read limit.");
        var maximum = Math.Min(65536, artifact.Content?.Target.Policy.MaximumReadBytes ?? 65536);
        if (maximum < 1) throw new KnowledgeAccessException("knowledge_access_content_unavailable", "Content access is unavailable.");
        FlowRunArtifactResource? legacy = null;
        if (artifact.Content is null)
        {
            using var workspace = scopes.Push(context);
            legacy = (await resources.GetExactAsync<FlowRunArtifactResource>(new(ResourceScopeRef.Workspace(context.WorkspaceId),
                ResourceNamespace.Default, ArtifactResourceKinds.FlowRunArtifact, artifact.ArtifactId), cancellationToken))?.Value;
            if (legacy is null || legacy.WorkspaceId.Value != context.WorkspaceId || legacy.Receipt.Length != artifact.Length
                || !string.Equals(legacy.Receipt.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new KnowledgeAccessException("knowledge_access_content_unavailable", "The historical document is unavailable.");
        }
        var result = new byte[(int)artifact.Length];
        for (var offset = 0; offset < result.Length;)
        {
            var count = Math.Min(maximum, result.Length - offset);
            ReadOnlyMemory<byte> chunk;
            if (artifact.Content is { } retained)
                chunk = await publications.ReadAsync(retained, offset, count, cancellationToken);
            else
            {
                using var workspace = scopes.Push(context);
                chunk = await durable.ReadAsync(new(context.WorkspaceId), legacy!.Receipt.OpaqueReference, offset, count, cancellationToken);
            }
            if (chunk.Length < 1 || chunk.Length > count)
                throw new KnowledgeAccessException("knowledge_access_content_incomplete", "The historical document is incomplete.");
            chunk.Span.CopyTo(result.AsSpan(offset));
            offset += chunk.Length;
        }
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(result)), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new KnowledgeAccessException("knowledge_access_content_integrity", "The historical document differs from its receipt.");
        return result;
    }
}

public sealed class KnowledgeAccessToolResolver(InternalMcpToolProjectionService projection, IResourceStore resources)
    : IKnowledgeAccessToolResolver
{
    public async Task<ToolRelayTarget> ResolveAsync(KnowledgeSourceOperation operation, ResourceScopeRef consumer, CancellationToken cancellationToken)
    {
        var scope = consumer.Kind == ResourceScopeKind.Workspace ? consumer : ResourceScopeRef.Instance;
        await projection.EnsureAsync(scope, ResourceNamespace.Default, cancellationToken);
        var name = AgentstrationToolProvider.ToolResourceName(KnowledgeAccessSchemas.ToolName(operation));
        var tool = (await resources.GetExactAsync<ToolResource>(new(scope, ResourceNamespace.Default,
            ToolResourceKinds.Tool, name), cancellationToken))?.Value
            ?? throw new KnowledgeSourceValidationException("knowledge_access_tools_unavailable", "The platform Knowledge Tool is unavailable.");
        return new() { Name = name, ScopeRef = scope, Namespace = ResourceNamespace.Default, Uid = tool.Uid, Generation = tool.Generation };
    }
}

public abstract class KnowledgeAccessMcpTool(KnowledgeAccessService access, KnowledgeSourceManagementService sources,
    ICurrentRequestContext current) : IInternalMcpToolHandler
{
    protected abstract KnowledgeSourceOperation Operation { get; }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public InternalMcpToolDefinition Definition => new(KnowledgeAccessSchemas.ToolName(Operation),
        "Knowledge " + KnowledgeAccessSchemas.Route(Operation),
        "Native governed Knowledge Snapshot access. Source identity is fixed by its published relay Tool.",
        KnowledgeAccessSchemas.Envelope(Operation),
        OutputSchema: Operation == KnowledgeSourceOperation.StructuredQuery ? JsonSerializer.SerializeToElement(new { type = "object" }) : null,
        InitialCategory: KnowledgeBuiltinSchemas.Category, ExposeThroughMcp: false);
    private sealed record Source(string Name, string Namespace, string ScopeRef, Guid Uid);
    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (current.AccessMode is not (ControlPlaneAccessMode.Workspace or ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration)
            || current.Current.PrincipalId != invocation.PrincipalId
            || current.Current.TenantId != invocation.TenantId || current.Current.WorkspaceId != invocation.WorkspaceId.Value)
            throw new ToolDefinitionInvocationException("knowledge_access_context_mismatch", "The Knowledge caller context does not match.");
        try
        {
            var source = invocation.Arguments.GetProperty("source").Deserialize<Source>(Json)
                ?? throw new KnowledgeAccessException("knowledge_access_source_invalid", "The source identity is invalid.");
            var id = new KnowledgeSourceId(source.Name, ResourceNamespace.Parse(source.Namespace), ResourceScopeRef.Parse(source.ScopeRef));
            var consumer = current.AccessMode switch
            {
                ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
                ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(invocation.TenantId),
                _ => ResourceScopeRef.Workspace(invocation.WorkspaceId.Value)
            };
            var stored = await sources.ResolveForUseAsync(id, consumer, cancellationToken);
            if (stored.Value.Uid != source.Uid) throw new KnowledgeAccessException("knowledge_access_source_changed", "The source identity changed.");
            var request = invocation.Arguments.GetProperty("request");
            object result = Operation switch
            {
                KnowledgeSourceOperation.StructuredQuery => await access.QueryDocumentsAsync(id, request.Deserialize<KnowledgeDocumentQueryRequest>(Json)!, cancellationToken),
                KnowledgeSourceOperation.Read => await access.ReadAsync(id, request.Deserialize<KnowledgeReadRequest>(Json)!, cancellationToken),
                KnowledgeSourceOperation.SearchRegex => await access.SearchRegexAsync(id, request.Deserialize<KnowledgeRegexRequest>(Json)!, cancellationToken),
                _ => await access.GetStatusAsync(id, request.Deserialize<KnowledgeStatusRequest>(Json)!, cancellationToken)
            };
            return JsonSerializer.SerializeToElement(result, result.GetType(), Json);
        }
        catch (KnowledgeAccessException exception) { throw new ToolDefinitionInvocationException(exception.Code, exception.Message); }
        catch (KnowledgeSourceNotFoundException) { throw new ToolDefinitionInvocationException("knowledge_access_source_not_found", "The Knowledge source is unavailable."); }
        catch (KnowledgeSourceValidationException exception) { throw new ToolDefinitionInvocationException(exception.Code, "The Knowledge source is unavailable."); }
        catch (KnowledgeSnapshotException exception) { throw new ToolDefinitionInvocationException(exception.Code, "The Knowledge Snapshot is unavailable."); }
        catch (Agentstration.Storage.StorageException exception) { throw new ToolDefinitionInvocationException(exception.Code, "The Knowledge content is unavailable."); }
        catch (ArtifactValidationException exception) { throw new ToolDefinitionInvocationException(exception.Code, "The Knowledge content is unavailable."); }
        catch (JsonException) { throw new ToolDefinitionInvocationException("knowledge_access_request_invalid", "The Knowledge request is invalid."); }
    }
}
public sealed class KnowledgeContentReadMcpTool(KnowledgeAccessService access, KnowledgeSourceManagementService sources,
    ICurrentRequestContext current) : KnowledgeAccessMcpTool(access, sources, current)
{ protected override KnowledgeSourceOperation Operation => KnowledgeSourceOperation.Read; }
public sealed class KnowledgeContentRegexMcpTool(KnowledgeAccessService access, KnowledgeSourceManagementService sources,
    ICurrentRequestContext current) : KnowledgeAccessMcpTool(access, sources, current)
{ protected override KnowledgeSourceOperation Operation => KnowledgeSourceOperation.SearchRegex; }
public sealed class KnowledgeContentStatusMcpTool(KnowledgeAccessService access, KnowledgeSourceManagementService sources,
    ICurrentRequestContext current) : KnowledgeAccessMcpTool(access, sources, current)
{ protected override KnowledgeSourceOperation Operation => KnowledgeSourceOperation.GetStatus; }

public sealed class KnowledgeDocumentQueryMcpTool(KnowledgeAccessService access, KnowledgeSourceManagementService sources,
    ICurrentRequestContext current) : KnowledgeAccessMcpTool(access, sources, current)
{ protected override KnowledgeSourceOperation Operation => KnowledgeSourceOperation.StructuredQuery; }
