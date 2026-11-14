using System.Diagnostics;
using System.Text.Json;
using Agentstration.Asp.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Security.Contracts;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

/// <summary>The same governed boundary serves API, Flow, Agent Tools and Knowledge publication.</summary>
public sealed class StorageDocumentGateway(IStorageResolver resolver, ICurrentRequestContext current,
    IAuthorizationService authorization, StorageAspRemoteAdapter remote, ISecurityAuditWriter? audit = null) : IStorageDocumentGateway
{
    private static readonly ActivitySource Activities = new("Agentstration.Storage.Documents");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public async Task<StorageDocumentDescription> DescribeAsync(StorageDocumentTarget target, CancellationToken ct)
    {
        var (context, collection, policy) = await ResolveAsync(target, "describe", ct);
        var capabilities = context.Documents!;
        var model = AspDocumentValidation.Digest(collection.Schema);
        return new(capabilities, From(policy), capabilities.QueryProfiles.Where(p => policy.QueryProfiles!.Contains(p.Id, StringComparer.Ordinal))
            .Select(p => new StorageQueryProfilePin(p.Id, p.Version, p.Encoding, model)).ToArray(), collection.Schema, model) { SchemaSnapshot = collection.SchemaSnapshot };
    }
    public Task<StorageDocumentValue> GetAsync(StorageDocumentTarget target, string id, string? revision, CancellationToken ct) =>
        ExecuteAsync(target, "get", async (client, token) => Value(await client.GetAsync(new(Guid.NewGuid().ToString("N"), id, revision), token)), ct);
    public Task<StorageDocumentValue> PutAsync(StorageDocumentTarget target, StorageDocumentWrite request, CancellationToken ct) =>
        ExecuteAsync(target, "put", async (client, token) => Value(await client.PutAsync(new(request.RequestId, request.NotAfter, request.Id, request.Value, request.ExpectedRevision), token)), ct);
    public Task<StorageDocumentValue> DeleteAsync(StorageDocumentTarget target, StorageDocumentDelete request, CancellationToken ct) =>
        ExecuteAsync(target, "delete", async (client, token) =>
        {
            var result = await client.DeleteAsync(new(request.RequestId, request.NotAfter, request.Id, request.ExpectedRevision), token);
            return new StorageDocumentValue(result.Id, result.Revision, JsonSerializer.SerializeToElement(new { deleted = true }));
        }, ct);
    public Task<StorageDocumentPage> QueryAsync(StorageDocumentTarget target, StorageDocumentQuery request, CancellationToken ct) =>
        ExecuteAsync(target, "query", async (client, token) =>
        {
            var result = await client.QueryAsync(new(request.RequestId, Profile(request.Profile), request.Query, request.PageSize, request.Cursor), token);
            return new StorageDocumentPage(result.Documents.Select(Value).ToArray(), result.Groups, result.Cursor);
        }, ct);
    public Task<StorageDocumentPublicationReference> PrepareAsync(StorageDocumentTarget target, string publicationId,
        IReadOnlyList<StorageDocumentValue> documents, string requestId, DateTimeOffset notAfter, CancellationToken ct) =>
        ExecuteAsync(target, "prepare", async (client, token) =>
        {
            var publication = await client.PrepareAsync(new(requestId, notAfter, publicationId, documents.Select(d => new AspDocumentInput(d.Id, d.Value)).ToArray()), token);
            var profile = client.Description.QueryProfiles.FirstOrDefault()
                ?? throw new AspException("unsupported_capability", "Structured publication needs an implemented query profile.");
            return new StorageDocumentPublicationReference(target with { Publication = publication.Id }, new(publication.Id, publication.Digest, publication.Count, publication.SchemaDigest),
                new(profile.Reference.Id, profile.Reference.Version, profile.Reference.Encoding, profile.Reference.ModelDigest))
            { SchemaSnapshot = (await DescribeAsync(target, token)).SchemaSnapshot };
        }, ct);
    public Task SealAsync(StorageDocumentPublicationReference reference, CancellationToken ct) => PublicationAsync(reference, "seal", ct);
    public Task ValidatePublicationAsync(StorageDocumentPublicationReference reference, CancellationToken ct) => PublicationAsync(reference, "inspect", ct);
    public Task RetireAsync(StorageDocumentPublicationReference reference, CancellationToken ct) => PublicationAsync(reference, "retire", ct);
    private Task<bool> PublicationAsync(StorageDocumentPublicationReference reference, string operation, CancellationToken ct) =>
        ExecuteAsync(reference.Target, operation, async (client, token) =>
        {
            var request = new AspDocumentPublicationRequest(Guid.NewGuid().ToString("N"), reference.Publication.Id, reference.Publication.Digest);
            var result = operation switch
            {
                "seal" => await client.SealAsync(request, token),
                "retire" => await client.RetireAsync(request, token),
                _ => await client.InspectAsync(request, token)
            };
            AspValidation.Require(result.Id == reference.Publication.Id && result.Digest == reference.Publication.Digest
                && (operation == "retire" || result.SchemaDigest == reference.Publication.SchemaDigest && result.Count == reference.Publication.Count), "integrity_mismatch", "The publication evidence differs.");
            return true;
        }, ct);

    private async Task<T> ExecuteAsync<T>(StorageDocumentTarget target, string operation,
        Func<Agentstration.Asp.Client.AspDocumentClient, CancellationToken, Task<T>> execute, CancellationToken ct)
    {
        using var activity = Activities.StartActivity("storage.documents." + operation);
        activity?.SetTag("storage.uid", target.Storage.StorageUid.ToString("N"));
        activity?.SetTag("collection.uid", target.CollectionUid.ToString("N"));
        activity?.SetTag("workspace.id", target.Consumer.ConsumerScopeRef.Value);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var (context, _, policy) = await ResolveAsync(target, operation, timeout.Token);
            var subject = current.ActorPrincipalId?.ToString("N") ?? (current.AccessMode == ControlPlaneAccessMode.System ? "system" : throw new AspException("forbidden", "Authentication is required."));
            var effective = new AspDocumentContext(new(context.Reference.StorageUid.ToString("N"), context.Type.TypeUid.ToString("N"), context.Reference.BindingDigest),
                new(target.CollectionUid.ToString("N"), target.Publication), subject,
                AspDocumentValidation.Digest(JsonSerializer.SerializeToElement(new { context.Reference, target.Consumer, policy }, AspProtocol.JsonOptions)), policy);
            AspDocumentValidation.Demand(effective, operation);
            var result = await remote.ExecuteDocumentsAsync(context, target, effective, execute, timeout.Token);
            if (audit is not null) await audit.WriteAsync(new(SecurityAuditActions.StorageDocumentAccessed,
                WorkspaceId: current.IsInitialized ? current.Current.WorkspaceId : null, ReasonCode: operation), timeout.Token);
            return result;
        }
        catch (AspException ex) { throw new StorageException("storage_documents_" + ex.Code, "The governed document operation was refused or could not complete.", ex.Code switch
        {
            "forbidden" => 403,
            "backend_timeout" => 504,
            "backend_connection_refused" or "backend_unavailable" or "provider_unavailable" => 503,
            _ => ex.Code.Contains("conflict", StringComparison.Ordinal) ? 409 : 422
        }); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new StorageException("storage_documents_timeout", "The document operation timed out.", 504); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException)
        { throw new StorageException("storage_documents_unavailable", "The exact document provider is unavailable.", 503); }
    }
    private async Task<(ProtectedStorageContext Context, StorageDocumentCollection Collection, AspDocumentPolicy Policy)> ResolveAsync(
        StorageDocumentTarget target, string operation, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(target.Storage, target.Consumer, ct);
        var capability = context.Documents ?? throw new StorageException("storage_documents_unsupported", "This Storage does not support documents.");
        var collection = context.Collections.SingleOrDefault(c => c.Uid == target.CollectionUid && c.Enabled)
            ?? throw new StorageException("storage_collection_unavailable", "The exact collection is unavailable.", 403);
        if (current.AccessMode != ControlPlaneAccessMode.System && operation != "describe")
        {
            await authorization.EnsurePermissionAsync(current.Current, AuthorizationPermissions.RunsExecute, ct);
            await authorization.EnsurePermissionAsync(current.Current, operation is "put" or "prepare" or "seal" or "retire"
                ? AuthorizationPermissions.ResourcesWrite : operation == "delete" ? AuthorizationPermissions.ResourcesDelete : AuthorizationPermissions.ResourcesRead, ct);
        }
        var id = target.CollectionUid.ToString("N");
        var envelope = context.Envelope.Deserialize<StorageDocumentPolicy>(Json)
            ?? throw new StorageException("storage_document_envelope_invalid", "The document profile envelope is invalid.");
        envelope = envelope with { QueryProfiles = envelope.QueryProfiles ?? capability.QueryProfiles.Select(p => p.Id).ToArray() };
        var policy = AspDocumentValidation.Intersect(Map(envelope, id),
            AspDocumentValidation.Intersect(Map(collection.Policy, id), Map(target.Policy with { QueryProfiles = target.Policy.QueryProfiles ?? capability.QueryProfiles.Select(p => p.Id).ToArray() }, id)));
        policy = policy with
        {
            Get = policy.Get && capability.Get,
            Put = policy.Put && capability.Put,
            Delete = policy.Delete && capability.Delete,
            Query = policy.Query && capability.Query,
            Publish = policy.Publish && capability.ImmutablePublications,
            QueryProfiles = policy.QueryProfiles!.Where(p => capability.QueryProfiles.Any(c => c.Id == p)).ToArray()
        };
        if (operation != "describe") policy = policy with
        {
            Get = policy.Get && operation is "get" or "inspect",
            Put = policy.Put && operation == "put",
            Delete = policy.Delete && operation == "delete",
            Query = policy.Query && operation is "query" or "inspect",
            Publish = policy.Publish && operation is "prepare" or "seal" or "retire" or "inspect"
        };
        return (context, collection, policy);
    }
    internal static AspDocumentPolicy Map(StorageDocumentPolicy p, string id) => new(id, p.Get, p.Put, p.Delete, p.Query, p.Publish,
        p.MaximumPageSize, p.MaximumDocumentBytes, p.MaximumResultBytes, p.QueryProfiles ?? []);
    internal static StorageDocumentPolicy From(AspDocumentPolicy p) => new(p.Get, p.Put, p.Delete, p.Query, p.Publish,
        p.MaximumPageSize, p.MaximumDocumentBytes, p.MaximumResultBytes, p.QueryProfiles);
    private static StorageDocumentValue Value(AspDocument d) => new(d.Id, d.Revision, d.Value);
    private static AspQueryProfileReference Profile(StorageQueryProfilePin p) => new(p.Id, p.Version, p.Encoding, p.ModelDigest);
}
