using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Aep.Client;
using Agentstration.Asp.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Agentstration.Tools.Mcp;
using Microsoft.AspNetCore.DataProtection;

namespace Agentstration.Infrastructure.Storage;

public sealed class StorageContentGateway(
    IStorageResolver resolver, IResourceStore resources, ICurrentRequestContext current,
    IAuthorizationService authorization,
    LocalStorageObjectProvider local, StorageAspRemoteAdapter remote, IDataProtectionProvider protection,
    TimeProvider clock) : IStorageContentGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector protector = protection.CreateProtector("Agentstration.Storage.Content.v1");
    private sealed record Reference(string Kind, string Authority, string Key, string? Version, DateTimeOffset Expires, string? Query);

    public async Task<StorageObjectPage> ListAsync(StorageContentRequest target, string prefix, int limit,
        string? cursor = null, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(target, async (context, policy, type, token) =>
        {
            AspValidation.Prefix(prefix);
            AspValidation.Require(policy.Policy.CanList && prefix.StartsWith(policy.Policy.Prefix, StringComparison.Ordinal),
                "forbidden", "List is not authorized.");
            AspValidation.Require(limit > 0 && limit <= policy.Policy.MaxPageSize, "limit_exceeded", "Invalid page size.");
            if (!IsLocal(type)) return await remote.ListAsync(context, target, type, policy, prefix, limit, cursor, token);
            var query = AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new { prefix, limit }, Json));
            var after = cursor is null ? null : Open(cursor, "cursor", policy, query).Key;
            var page = await local.ListAsync(context.Reference, policy, prefix, after, limit, token);
            return new StorageObjectPage(page.Objects.Select(item => Public(item, policy)).ToArray(),
                page.NextKey is null ? null : Seal("cursor", policy, page.NextKey, null, query));
        }, cancellationToken);
    }

    public async Task<StorageObject> StatAsync(StorageContentRequest target, string reference,
        CancellationToken cancellationToken = default) => await ExecuteAsync(target, async (context, policy, type, token) =>
    {
        AspValidation.Require(policy.Policy.CanStat, "forbidden", "Stat is not authorized.");
        if (!IsLocal(type)) return await remote.StatAsync(context, target, type, policy, reference, token);
        var item = Open(reference, "object", policy);
        return Public(await local.StatAsync(context.Reference, policy, item.Key, item.Version!, token), policy);
    }, cancellationToken);

    public async Task<StorageObjectContent> ReadAsync(StorageContentRequest target, string reference, long offset,
        int maximumBytes, CancellationToken cancellationToken = default) => await ExecuteAsync(target, async (context, policy, type, token) =>
    {
        AspValidation.Require(policy.Policy.CanRead, "forbidden", "Read is not authorized.");
        AspValidation.Require(offset >= 0 && maximumBytes > 0 && maximumBytes <= policy.Policy.MaxReadBytes,
            "limit_exceeded", "Invalid read range.");
        if (!IsLocal(type)) return await remote.ReadAsync(context, target, type, policy, reference, offset, maximumBytes, token);
        var referenceValue = Open(reference, "object", policy);
        var content = await local.ReadAsync(context.Reference, policy, referenceValue.Key, referenceValue.Version!, offset, maximumBytes, token);
        return new StorageObjectContent(Public(content.Item, policy), offset, Convert.ToBase64String(content.Content), content.EndOfObject);
    }, cancellationToken);

    public async Task<StorageObject> WriteAsync(StorageContentRequest target, StorageWrite request,
        CancellationToken cancellationToken = default) => await ExecuteAsync(target, async (context, policy, type, token) =>
    {
        AspValidation.Require(policy.Policy.CanWrite, "forbidden", "Write is not authorized.");
        AspValidation.RequestId(request.RequestId);
        AspValidation.DemandKey(policy, request.Key);
        AspValidation.Deadline(request.NotAfter, clock);
        if (request.ExpectedVersion is not null) AspValidation.RequestId(request.ExpectedVersion);
        var bytes = AspValidation.Content(request.ContentBase64, policy.Policy.MaxWriteBytes);
        if (!IsLocal(type)) return await remote.WriteAsync(context, target, type, policy, request, token);
        return Public(await local.WriteAsync(context.Reference, policy,
            new(request.RequestId, request.NotAfter, request.Key, bytes, request.ExpectedVersion), token), policy);
    }, cancellationToken);

    private async Task<T> ExecuteAsync<T>(StorageContentRequest target,
        Func<ProtectedStorageContext, AspAccessContext, StorageTypeResource, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var context = await resolver.ResolveAsync(target.Storage, target.Consumer, deadline.Token);
            if (current.AccessMode != ControlPlaneAccessMode.System)
            {
                RequestContext execution;
                try { execution = current.Current; }
                catch (InvalidOperationException)
                { throw new StorageException("storage_execution_context_required", "Content operations require a trusted execution Workspace.", 403); }
                await authorization.EnsurePermissionAsync(execution, AuthorizationPermissions.RunsExecute, deadline.Token);
            }
            var reference = context.Type.Type;
            var type = (await resources.GetExactAsync<StorageTypeResource>(new(reference.ScopeRef!.Value,
                reference.Namespace ?? ResourceNamespace.Default, StorageResourceKinds.StorageType, reference.Name), deadline.Token))?.Value
                ?? throw new AspException("provider_unavailable", "The exact implementation is unavailable.");
            var policy = Effective(context, target.Policy, IsLocal(type));
            var subject = current.ActorPrincipalId?.ToString("N")
                ?? (current.AccessMode == ControlPlaneAccessMode.System ? "system" : throw new AspException("forbidden", "An authenticated caller is required."));
            var store = new AspStoreRef(context.Reference.StorageUid.ToString("N"),
                context.Type.TypeUid.ToString("N"), context.Reference.BindingDigest);
            var authorizationRevision = AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new
            { target.Consumer.ConsumerScopeRef, context.Reference.Generation, policy }, Json));
            var access = new AspAccessContext(store, subject, authorizationRevision, policy);
            return await operation(context, access, type, deadline.Token);
        }
        catch (AspException exception)
        {
            // Do not forward provider exception messages, paths, payloads or credentials.
            throw new StorageException("storage_content_" + SafeCode(exception.Code), "The Storage content operation was refused or could not complete.",
                exception.Code == "forbidden" ? 403 : exception.Code is "conflict" or "idempotency_conflict" ? 409 : 422);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or HttpRequestException)
        { throw new StorageException("storage_content_unavailable", "The Storage content operation could not complete.", 503); }
        catch (Exception exception) when (exception is AepProtocolException or SecretResolutionException or SecretCapabilityException
            or ParameterAccessDeniedException or ToolResolutionException or ModelContextProtocol.McpException)
        { throw new StorageException("storage_content_binding_unavailable", "The exact provider or connection binding is unavailable.", 409); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new StorageException("storage_content_timeout", "The operation timed out. Retry a write only with its unchanged original request identity and arguments.", 504); }
    }

    private static bool IsLocal(StorageTypeResource type) => type.Governance.ManagedBy?.Reference == "storage.local"
        && type.Governance.ManagedBy.Kind == ResourceManagementAuthorityKind.Core;

    private static AspAccessPolicy Effective(ProtectedStorageContext context, StorageContentPolicy requested, bool local)
    {
        ArgumentNullException.ThrowIfNull(requested);
        // Local envelopes have this schema. Remote envelopes remain provider-defined and opaque.
        var declared = local
            ? context.Envelope.Deserialize<StorageContentPolicy>(Json) ?? throw new AspException("invalid_policy", "A Storage envelope is required.")
            : new StorageContentPolicy(Write: true);
        var requestedPolicy = Map(requested);
        var envelope = Map(declared);
        AspValidation.Policy(requestedPolicy);
        AspValidation.Policy(envelope);
        AspValidation.Require(requested.Prefix.StartsWith(declared.Prefix, StringComparison.Ordinal), "forbidden", "The dataset rule widens the Storage envelope.");
        var policy = new AspAccessPolicy(context.Capabilities.List && envelope.CanList && requested.List,
            context.Capabilities.Stat && envelope.CanStat && requested.Stat,
            context.Capabilities.Read && envelope.CanRead && requested.Read,
            context.Capabilities.Write && envelope.CanWrite && requested.Write,
            requested.Prefix, Math.Min(context.Limits.MaximumReadBytes, Math.Min(envelope.MaxReadBytes, requested.MaximumReadBytes)),
            Math.Min(context.Limits.MaximumWriteBytes, Math.Min(envelope.MaxWriteBytes, requested.MaximumWriteBytes)),
            Math.Min(context.Limits.MaximumPageSize, Math.Min(envelope.MaxPageSize, requested.MaximumPageSize)),
            Maximum(envelope.KeyFrom, requested.KeyFrom), Minimum(envelope.KeyBefore, requested.KeyBefore));
        AspValidation.Policy(policy);
        return policy;
    }

    private static AspAccessPolicy Map(StorageContentPolicy policy) => new(policy.List, policy.Stat, policy.Read, policy.Write,
        policy.Prefix, policy.MaximumReadBytes, policy.MaximumWriteBytes, policy.MaximumPageSize, policy.KeyFrom, policy.KeyBefore);
    private static string? Maximum(string? a, string? b) => a is null ? b : b is null ? a : string.CompareOrdinal(a, b) >= 0 ? a : b;
    private static string? Minimum(string? a, string? b) => a is null ? b : b is null ? a : string.CompareOrdinal(a, b) <= 0 ? a : b;
    private static string SafeCode(string code) => code.Length <= 64 && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? code : "provider_failure";
    private StorageObject Public(AspProviderObject item, AspAccessContext context) =>
        new(item.Key, item.Version, item.Length, item.Sha256, Seal("object", context, item.Key, item.Version, null));
    private string Seal(string kind, AspAccessContext context, string key, string? version, string? query) => protector.Protect(
        JsonSerializer.Serialize(new Reference(kind, Authority(context), key, version, clock.GetUtcNow().AddMinutes(15), query), Json));
    private Reference Open(string token, string kind, AspAccessContext context, string? query = null)
    {
        AspValidation.Require(token is { Length: > 0 and <= 8192 }, "invalid_reference", "Invalid content reference.");
        try
        {
            var value = JsonSerializer.Deserialize<Reference>(protector.Unprotect(token), Json)!;
            AspValidation.Require(value.Kind == kind && value.Authority == Authority(context) && value.Expires > clock.GetUtcNow()
                && value.Query == query && (kind != "object" || value.Version is not null), "invalid_reference", "Content reference scope or lifetime differs.");
            AspValidation.DemandKey(context, value.Key);
            return value;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or NullReferenceException)
        { throw new AspException("invalid_reference", "Invalid content reference."); }
    }
    private static string Authority(AspAccessContext context) => AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(context, Json));
}
