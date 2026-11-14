using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Storage.Contracts;
using Agentstration.Tools.Mcp;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;

namespace Agentstration.Infrastructure.Storage;

/// <summary>Protected configuration exchange followed by SDK-owned MCP content operations.</summary>
public sealed class StorageAspRemoteAdapter(IResourceStore resources, IStorageResolver storages,
    AepMcpTransport transport, IParameterResolver parameters,
    ISecretCapabilityService capabilities, ISecretResolver secrets, IConfiguration configuration,
    IRequestContextScopeFactory requestScopes, TimeProvider clock,
    ICurrentRequestContext current, IAuthorizationService authorization)
{
    private sealed class Lease(McpClient connection, AspClient client, Action revoke) : IAsyncDisposable
    {
        public AspClient Client { get; } = client;
        public async ValueTask DisposeAsync() { revoke(); await connection.DisposeAsync(); }
    }

    public async Task<StorageObjectPage> ListAsync(ProtectedStorageContext context, StorageContentRequest target,
        StorageTypeResource type, AspAccessContext policy, string prefix, int limit, string? cursor, CancellationToken cancellationToken)
    {
        await using var lease = await ConnectAsync(context, target, type, policy, cancellationToken);
        var result = await lease.Client.ListAsync(new(Guid.NewGuid().ToString("N"), prefix, limit, cursor), cancellationToken);
        return new(result.Objects.Select(Map).ToArray(), result.Cursor);
    }
    public async Task<StorageObject> StatAsync(ProtectedStorageContext context, StorageContentRequest target,
        StorageTypeResource type, AspAccessContext policy, string reference, CancellationToken cancellationToken)
    {
        await using var lease = await ConnectAsync(context, target, type, policy, cancellationToken);
        return Map(await lease.Client.StatAsync(new(Guid.NewGuid().ToString("N"), reference), cancellationToken));
    }
    public async Task<StorageObjectContent> ReadAsync(ProtectedStorageContext context, StorageContentRequest target,
        StorageTypeResource type, AspAccessContext policy, string reference, long offset, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var lease = await ConnectAsync(context, target, type, policy, cancellationToken);
        // This product port returns one bounded payload even though ASP can transfer larger selections.
        AspValidation.Require(maximumBytes > 0 && maximumBytes <= lease.Client.Description.Policy.MaxReadBytes,
            "limit_exceeded", "The requested product payload exceeds the provider's effective bound.");
        var request = new AspReadBlobRequest(Guid.NewGuid().ToString("N"), Selection: new(reference, new(offset, maximumBytes), maximumBytes),
            NotAfter: clock.GetUtcNow().AddSeconds(25));
        using var output = new MemoryStream();
        AspObject? item = null;
        while (true)
        {
            var chunk = await lease.Client.ReadBlobAsync(request, cancellationToken);
            item ??= chunk.Item;
            AspValidation.Require(chunk.Item == item && output.Length + chunk.ByteLength <= maximumBytes,
                "invalid_result", "The provider changed the selected object or exceeded its range.");
            await output.WriteAsync(Convert.FromBase64String(chunk.ContentBase64), cancellationToken);
            if (chunk.Final) break;
            request = new(Guid.NewGuid().ToString("N"), chunk.Sequence + 1, chunk.SessionRef);
        }
        return new(Map(item), offset, Convert.ToBase64String(output.ToArray()), offset + output.Length == item.Length);
    }
    public async Task<StorageObject> WriteAsync(ProtectedStorageContext context, StorageContentRequest target,
        StorageTypeResource type, AspAccessContext policy, StorageWrite request, CancellationToken cancellationToken)
    {
        await using var lease = await ConnectAsync(context, target, type, policy, cancellationToken);
        var written = await lease.Client.WriteBlobAsync(new(request.RequestId, 0, true, request.ContentBase64,
            Options: new(request.Key, request.ExpectedVersion is null ? "create" : "replace", request.ExpectedVersion),
            NotAfter: request.NotAfter), cancellationToken);
        return Map(written.Receipt!.Item with { Sha256 = written.Receipt.Sha256 });
    }

    public async Task<bool> RetireAsync(ProtectedStorageContext context, StorageContentRequest target,
        StorageTypeResource type, AspAccessContext policy, StorageObject exact, CancellationToken cancellationToken)
    {
        await using var lease = await ConnectAsync(context, target, type, policy, cancellationToken);
        var transfers = lease.Client.Description.Transfers;
        // Retire the producer-owned value through a conditional empty replacement. ASP v1 offers no generic delete.
        if (!policy.Policy.CanRead || !policy.Policy.CanWrite || transfers is not { Replace: true, EmptyWrite: true, ETagConditions: true }) return false;
        var page = await lease.Client.ListAsync(new(Guid.NewGuid().ToString("N"), exact.Key, 1), cancellationToken);
        var item = page.Objects.SingleOrDefault(value => value.Key == exact.Key);
        if (item is null || item.Length == 0 && exact.Length > 0) return true;
        if (item.Length != exact.Length || exact.Version is not null && item.Version != exact.Version || string.IsNullOrEmpty(item.ETag)) return false;
        AspValidation.Require(item.Length is > 0 and <= 65536 && exact.Sha256 is not null,
            "version_unavailable", "An exact bounded content receipt is required for retirement.");
        using var bytes = new MemoryStream();
        var request = new AspReadBlobRequest(Guid.NewGuid().ToString("N"), Selection: new(item.Reference,
            PreferredChunkSizeBytes: Math.Min(65536, lease.Client.Description.Policy.MaxReadBytes)), NotAfter: clock.GetUtcNow().AddSeconds(25));
        while (true)
        {
            var chunk = await lease.Client.ReadBlobAsync(request, cancellationToken);
            AspValidation.Require(chunk.Item.Key == exact.Key && chunk.Item.Length == exact.Length
                && chunk.Item.ETag == item.ETag && bytes.Length + chunk.ByteLength <= exact.Length,
                "version_unavailable", "The selected result changed during retirement verification.");
            await bytes.WriteAsync(Convert.FromBase64String(chunk.ContentBase64), cancellationToken);
            if (chunk.Final) break;
            request = new(Guid.NewGuid().ToString("N"), chunk.Sequence + 1, chunk.SessionRef);
        }
        AspValidation.Require(bytes.Length == exact.Length && string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())), exact.Sha256,
            StringComparison.OrdinalIgnoreCase), "version_unavailable", "The result does not match its retained digest.");
        var replaced = await lease.Client.WriteBlobAsync(new(Guid.NewGuid().ToString("N"), 0, true, string.Empty,
            Options: new(exact.Key, "replace", IfMatch: item.ETag), NotAfter: clock.GetUtcNow().AddSeconds(25)), cancellationToken);
        AspValidation.Require(replaced.Receipt is { Item.Length: 0, WrittenBytes: 0 }, "invalid_result", "The provider did not attest empty result retirement.");
        return true;
    }

    private async Task<Lease> ConnectAsync(ProtectedStorageContext context, StorageContentRequest target,
        StorageTypeResource type, AspAccessContext policy, CancellationToken cancellationToken)
    {
        var version = type.Definition.Versions.Single(v => v.Version == context.Type.Version);
        if (version.Contribution is not { } proof || version.Asp is not { } mapping || mapping.Profile != AspProtocol.Profile)
            throw new AspException("provider_unavailable", "This exact type has no configured ASP implementation.");
        ExtensionRegistrationResource registration;
        using (requestScopes.PushSystem())
            registration = (await resources.GetExactAsync<ExtensionRegistrationResource>(proof.Provider, cancellationToken))?.Value
                ?? throw new AspException("provider_unavailable", "The provider registration is unavailable.");
        AspValidation.Require(registration.Uid == proof.ProviderUid && registration.Generation == proof.ProviderGeneration
            && registration.Definition.Enabled && registration.Definition.ExpectedExtensionId == type.Governance.Origin.Reference,
            "provider_unavailable", "The exact provider registration is stale or unavailable.");
        var extension = new ResolvedAepExtension(registration, AepExtensionCredentials.Create(registration, secrets));
        var manifest = await transport.DiscoverAsync(extension, registration.Definition.ExpectedExtensionId, cancellationToken);
        var contribution = manifest.Contributions.StorageTypes?.SingleOrDefault(c => c.Id == proof.ContributionId);
        var declared = contribution?.Versions.SingleOrDefault(v => v.Version == proof.Version);
        AspValidation.Require(declared is not null && AepStorageProtocol.Digest(declared) == proof.Digest,
            "provider_stale", "The pinned Storage implementation contribution has changed.");
        var server = manifest.Mcp?.Servers.SingleOrDefault(s => s.Id == mapping.McpServer)
            ?? throw new AspException("provider_unavailable", "The declared MCP server is unavailable.");
        var handles = new List<SecretCapabilityHandle>();
        McpClient? connection = null;
        try
        {
            // Repeat exact identity/permission checks immediately before issuing scoped value grants.
            _ = await storages.ResolveAsync(target.Storage, target.Consumer, cancellationToken);
            if (current.AccessMode != ControlPlaneAccessMode.System)
                await authorization.EnsurePermissionAsync(current.Current, AuthorizationPermissions.RunsExecute, cancellationToken);
            var values = await BindAsync(context, proof, manifest, handles, cancellationToken);
            var request = new AepStorageBindingRequest(proof.ContributionId, proof.Version, proof.Digest,
                JsonSerializer.SerializeToElement(policy.Store, AspProtocol.JsonOptions), values,
                context.Configuration, context.Envelope, JsonSerializer.SerializeToElement(policy.Policy, AspProtocol.JsonOptions),
                AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new { policy.Subject, policy.AuthorizationRevision }, AspProtocol.JsonOptions)),
                clock.GetUtcNow().AddSeconds(30));
            using var http = transport.CreateConfigurationClient(extension);
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(registration.Definition.Endpoint, mapping.BindingPath));
            message.Content = JsonContent.Create(request, options: AepProtocol.JsonOptions);
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            AspValidation.Require(response.IsSuccessStatusCode, "binding_refused", "The extension refused the protected binding.");
            AspValidation.Require(response.Content.Headers.ContentLength is null or <= 4096, "invalid_result", "The binding response is oversized.");
            await response.Content.LoadIntoBufferAsync(4096, cancellationToken);
            var bound = await response.Content.ReadFromJsonAsync<AepStorageBindingLease>(AepProtocol.JsonOptions, cancellationToken)
                ?? throw new AspException("invalid_result", "The extension returned no binding lease.");
            AspValidation.Require(bound.Token is { Length: >= 32 and <= 512 } && bound.Token.All(char.IsAsciiLetterOrDigit)
                && bound.ExpiresAt > clock.GetUtcNow() && bound.ExpiresAt <= request.ExpiresAt,
                "invalid_result", "The extension returned an invalid binding lease.");
            connection = await transport.ConnectAsync(extension, server,
                new Dictionary<string, string> { [AepStorageProtocol.BindingHeader] = bound.Token }, cancellationToken);
            var client = await AspClient.ConnectAsync(connection, mapping.ToolPrefix, policy.Store,
                expectedProviderId: mapping.ProviderId, expectedProviderVersion: mapping.ProviderVersion, cancellationToken: cancellationToken);
            AspValidation.Require(AspValidation.IsNarrower(client.Description.Policy, policy.Policy),
                "invalid_result", "The provider widened the effective restrictions.");
            AspValidation.Require(StorageDigests.Schema(client.Description.ConfigurationSchema) == version.SchemaDigest,
                "provider_stale", "The provider schema differs from the pinned implementation.");
            return new(connection, client, () => { foreach (var handle in handles) capabilities.Revoke(handle); });
        }
        catch
        {
            foreach (var handle in handles) capabilities.Revoke(handle);
            if (connection is not null) await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<IReadOnlyList<AepBoundValue>> BindAsync(ProtectedStorageContext context, StorageContributionEvidence proof,
        AepManifest manifest, List<SecretCapabilityHandle> handles, CancellationToken cancellationToken)
    {
        if (context.ConnectedService is not { } selected) return [];
        var serviceAddress = selected.Service.Address;
        var service = (await resources.GetExactAsync<ConnectedServiceResource>(new(selected.Service.ScopeRef,
            serviceAddress.Namespace, serviceAddress.Kind, serviceAddress.Name), cancellationToken))?.Value;
        AspValidation.Require(service is not null && service.Uid == selected.ServiceUid && service.Generation == selected.Generation
            && service.Definition.Enabled, "service_stale", "The exact Connected Service changed.");
        var result = new List<AepBoundValue>();
        var execution = Guid.NewGuid().ToString("N");
        var declared = context.Bindings.Select(b => b.RequirementId).ToArray();
        foreach (var mapping in context.Bindings)
        {
            var input = mapping.Export.SourceInputId;
            var binding = service.Definition.Bindings.SingleOrDefault(b => b.InputId == input)
                ?? throw new AspException("export_unavailable", "The selected export has no executable value binding.");
            if (mapping.Export.Protection == ConnectedServiceValueProtection.Secured)
            {
                AspValidation.Require(binding.Kind == ConnectedServiceBindingKind.Secret && binding.Secret is not null,
                    "export_incompatible", "The secured export has no Secret binding.");
                AspValidation.Require(manifest.Capabilities.TryGetValue(AepCapabilityNames.SecretAccess, out var capability)
                    && capability.Version == AepProtocol.SecretAccessVersion, "unsupported_capability", "The provider cannot redeem secured exports.");
                var publicBase = configuration["Agentstration:Aep:SecretAccess:PublicBaseUrl"];
                AspValidation.Require(Uri.TryCreate(publicBase, UriKind.Absolute, out var baseUri)
                    && (baseUri.Scheme == Uri.UriSchemeHttps || baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback),
                    "configuration_invalid", "A protected Secret access endpoint is required.");
                var consumer = new ScopedResourceAddress(selected.Service.ScopeRef, service.Namespace, service.Kind, service.Name);
                var handle = await capabilities.IssueAsync(new(proof.Provider, manifest.Extension.Id, consumer, mapping.RequirementId, execution),
                    new(mapping.RequirementId, binding.Secret!), declared, cancellationToken, cancellationToken);
                handles.Add(handle);
                result.Add(AepBoundValue.Secured(mapping.RequirementId, new(AepProtocol.SecretAccessVersion,
                    new Uri(baseUri!, AepProtocol.SecretAccessPath), manifest.Extension.Id, mapping.RequirementId, execution, handle.RevealForTransport())));
            }
            else
            {
                JsonElement value;
                if (binding.Kind == ConnectedServiceBindingKind.Literal && binding.Literal is { } literal) value = literal;
                else if (binding.Kind == ConnectedServiceBindingKind.Parameter && binding.Parameter is { } parameter)
                    value = (await parameters.ResolveAsync(parameter, new(selected.Service.ScopeRef, service.Address), cancellationToken))?.Value
                        ?? throw new AspException("export_unavailable", "A selected Parameter is unavailable.");
                else throw new AspException("export_incompatible", "A standard export has an incompatible value binding.");
                result.Add(AepBoundValue.Inline(mapping.RequirementId, value));
            }
        }
        return result;
    }
    private static StorageObject Map(AspObject item) => new(item.Key, item.Version, item.Length, item.Sha256, item.Reference);
}
