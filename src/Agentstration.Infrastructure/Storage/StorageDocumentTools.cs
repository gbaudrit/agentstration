using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Notifications;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Storage;

public abstract class StorageDocumentMcpTool(IStorageDocumentGateway gateway, ICurrentRequestContext current)
    : IInternalMcpToolHandler
{
    protected abstract string Operation { get; }
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public InternalMcpToolDefinition Definition => new("storage.documents." + Operation, "Structured documents " + Operation,
        "Governed structured collection access. A published relay fixes the exact Storage, collection, policy and query profile. No physical destination or SQL is accepted.",
        Schema(Operation), ExposeThroughMcp: false);

    internal static JsonElement Schema(string operation)
    {
        var properties = new JsonObject { ["requestId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128 } };
        var required = new JsonArray("requestId");
        if (operation == "query")
        {
            properties["profile"] = JsonNode.Parse("""{"type":"object","properties":{"id":{"type":"string"},"version":{"type":"string"},"encoding":{"type":"string"},"modelDigest":{"type":"string"}},"required":["id","version","encoding","modelDigest"],"additionalProperties":false}""");
            properties["query"] = new JsonObject();
            properties["pageSize"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 };
            properties["cursor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 4096 };
            required.Add("profile"); required.Add("query");
        }
        else
        {
            properties["id"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 };
            required.Add("id");
            if (operation == "get") properties["revision"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128 };
            else
            {
                properties["notAfter"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" };
                properties["expectedRevision"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128 };
                required.Add("notAfter");
                if (operation == "put") { properties["value"] = new JsonObject { ["type"] = "object" }; required.Add("value"); }
                else required.Add("expectedRevision");
            }
        }
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("target", "request"),
            ["properties"] = new JsonObject
            {
                ["target"] = new JsonObject { ["type"] = "object" },
                ["request"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required,
                    ["additionalProperties"] = false
                }
            }
        });
    }

    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (current.AccessMode is not (ControlPlaneAccessMode.Workspace or ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration)
            || current.Current.PrincipalId != invocation.PrincipalId || current.Current.TenantId != invocation.TenantId
            || current.Current.WorkspaceId != invocation.WorkspaceId.Value)
            throw new ToolDefinitionInvocationException("storage_documents_context_mismatch", "The document caller context does not match.");
        try
        {
            var target = invocation.Arguments.GetProperty("target").Deserialize<StorageDocumentTarget>(Json)
                ?? throw new JsonException();
            var consumer = current.AccessMode switch
            {
                ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
                ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(invocation.TenantId),
                _ => ResourceScopeRef.Workspace(invocation.WorkspaceId.Value)
            };
            // Caller context is always recomputed; even a base Tool invocation cannot choose its scope.
            target = target with
            {
                Consumer = target.Consumer with { ConsumerScopeRef = consumer },
                Policy = target.Policy with { Publish = false }
            };
            if (target.Publication is not null) throw new StorageException("storage_document_mutable_tools_required", "Use Knowledge Tools for immutable publications.");
            var request = invocation.Arguments.GetProperty("request");
            object result;
            switch (Operation)
            {
                case "get":
                    var get = request.Deserialize<GetRequest>(Json) ?? throw new JsonException();
                    result = await gateway.GetAsync(target, get.Id, get.Revision, cancellationToken); break;
                case "put": result = await gateway.PutAsync(target, request.Deserialize<StorageDocumentWrite>(Json)!, cancellationToken); break;
                case "delete": result = await gateway.DeleteAsync(target, request.Deserialize<StorageDocumentDelete>(Json)!, cancellationToken); break;
                default: result = await gateway.QueryAsync(target, request.Deserialize<StorageDocumentQuery>(Json)!, cancellationToken); break;
            }
            return JsonSerializer.SerializeToElement(result, result.GetType(), Json);
        }
        catch (StorageException exception) { throw new ToolDefinitionInvocationException(exception.Code, "The governed document operation failed."); }
        catch (JsonException) { throw new ToolDefinitionInvocationException("storage_document_request_invalid", "The document request is invalid."); }
    }
    private sealed record GetRequest(string RequestId, string Id, string? Revision = null);
}

public sealed class StorageDocumentGetMcpTool(IStorageDocumentGateway gateway, ICurrentRequestContext current) : StorageDocumentMcpTool(gateway, current)
{ protected override string Operation => "get"; }
public sealed class StorageDocumentPutMcpTool(IStorageDocumentGateway gateway, ICurrentRequestContext current) : StorageDocumentMcpTool(gateway, current)
{ protected override string Operation => "put"; }
public sealed class StorageDocumentDeleteMcpTool(IStorageDocumentGateway gateway, ICurrentRequestContext current) : StorageDocumentMcpTool(gateway, current)
{ protected override string Operation => "delete"; }
public sealed class StorageDocumentQueryMcpTool(IStorageDocumentGateway gateway, ICurrentRequestContext current) : StorageDocumentMcpTool(gateway, current)
{ protected override string Operation => "query"; }

public sealed class StorageDocumentToolExposure(IStorageDocumentGateway gateway, ToolDefinitionService definitions,
    InternalMcpToolProjectionService projection, IResourceStore resources) : IStorageDocumentToolExposure
{
    public async Task<IReadOnlyList<ResourceReference>> PublishAsync(StorageDocumentTarget target,
        StorageDocumentToolPublication request, CancellationToken ct)
    {
        if (target.Publication is not null || request.Operations.Count is < 1 or > 4
            || request.Operations.Distinct(StringComparer.Ordinal).Count() != request.Operations.Count
            || request.Operations.Any(o => o is not ("get" or "put" or "delete" or "query")))
            throw new StorageException("storage_document_tool_operations_invalid", "Select explicit collection operations.");
        var description = await gateway.DescribeAsync(target, ct);
        if (request.Operations.Any(o => o switch
            {
                "get" => !description.Policy.Get,
                "put" => !description.Policy.Put,
                "delete" => !description.Policy.Delete,
                _ => !description.Policy.Query
            }) || request.Operations.Contains("query", StringComparer.Ordinal) && (request.QueryProfile is null || !description.QueryProfiles.Contains(request.QueryProfile)))
            throw new StorageException("storage_document_tool_capability_denied", "The selected operation or exact query profile is unavailable.");
        var scope = target.Consumer.ConsumerScopeRef;
        var nativeScope = scope.Kind == ResourceScopeKind.Workspace ? scope : ResourceScopeRef.Instance;
        var ns = target.Storage.Storage.Namespace;
        await projection.EnsureAsync(nativeScope, ResourceNamespace.Default, ct);
        var result = new List<ResourceReference>();
        foreach (var operation in request.Operations)
        {
            var nativeName = AgentstrationToolProvider.ToolResourceName("storage.documents." + operation);
            var native = (await resources.GetExactAsync<ToolResource>(new(nativeScope, ResourceNamespace.Default, ToolResourceKinds.Tool, nativeName), ct))?.Value
                ?? throw new StorageException("storage_document_tool_unavailable", "The platform document Tool is unavailable.");
            var name = "documents-" + target.CollectionUid.ToString("N") + "." + operation;
            var previous = await definitions.GetExactAsync(name, ns, scope, ct);
            if (previous is not null && (!previous.Value.Metadata.Annotations.TryGetValue("agentstration.io/document-storage-uid", out var owner)
                || owner != target.Storage.StorageUid.ToString("D")))
                throw new StorageException("storage_document_tool_conflict", "An unrelated ToolDefinition already owns this name.");
            var policy = description.Policy with
            {
                Get = operation == "get",
                Put = operation == "put",
                Delete = operation == "delete",
                Query = operation == "query",
                Publish = false
            };
            List<ToolArgumentBinding> bindings = [new LiteralToolArgumentBinding { Path = "/target",
                Value = JsonSerializer.SerializeToElement(target with { Policy = policy }, StorageDocumentMcpTool.Json) }];
            if (operation == "query") bindings.Add(new LiteralToolArgumentBinding
            {
                Path = "/request/profile",
                Value = JsonSerializer.SerializeToElement(request.QueryProfile, StorageDocumentMcpTool.Json)
            });
            await definitions.PutAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolDefinition,
                ScopeRef = scope,
                Metadata = new()
                {
                    Name = name,
                    Namespace = ns,
                    Annotations = new Dictionary<string, string>
                    { ["agentstration.io/document-storage-uid"] = target.Storage.StorageUid.ToString("D") }
                },
                Definition = new RelayToolDefinitionProperties
                {
                    Version = request.Version,
                    Publish = true,
                    DisplayName = operation + " " + target.CollectionUid.ToString("N"),
                    Description = "Governed structured collection " + operation + (operation == "query" ? "; profile " + request.QueryProfile!.Id + "/" + request.QueryProfile.Version : ""),
                    RequiresApproval = request.RequiresApproval,
                    Target = new() { Name = nativeName, Namespace = ResourceNamespace.Default, ScopeRef = nativeScope, Uid = native.Uid, Generation = native.Generation },
                    Bindings = bindings
                }
            }, previous?.ETag, previous is null, ct);
            result.Add(new(AgentstrationToolProvider.ToolResourceName(ToolDefinitionService.VersionName(name, request.Version)), scope, ns));
        }
        return result;
    }
}
