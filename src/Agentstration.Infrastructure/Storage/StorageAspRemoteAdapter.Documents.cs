using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Storage.Contracts;
using Agentstration.Tools.Mcp;
using ModelContextProtocol.Client;

namespace Agentstration.Infrastructure.Storage;

public sealed partial class StorageAspRemoteAdapter
{
    public async Task<T> ExecuteDocumentsAsync<T>(ProtectedStorageContext context, StorageDocumentTarget target,
        AspDocumentContext effective, Func<AspDocumentClient, CancellationToken, Task<T>> execute, CancellationToken ct)
    {
        var reference = context.Type.Type;
        var type = (await resources.GetExactAsync<StorageTypeResource>(new(reference.ScopeRef!.Value,
            reference.Namespace ?? ResourceNamespace.Default, StorageResourceKinds.StorageType, reference.Name), ct))?.Value
            ?? throw new AspException("provider_unavailable", "The exact document implementation is unavailable.");
        var version = type.Definition.Versions.Single(v => v.Version == context.Type.Version);
        if (version.Contribution is not { } proof || version.Asp is not { Profile: AspDocuments.Profile } mapping)
            throw new AspException("unsupported_capability", "This Storage has no document implementation.");
        ExtensionRegistrationResource registration;
        using (requestScopes.PushSystem()) registration = (await resources.GetExactAsync<ExtensionRegistrationResource>(proof.Provider, ct))?.Value
            ?? throw new AspException("provider_unavailable", "The exact extension registration is unavailable.");
        AspValidation.Require(registration.Uid == proof.ProviderUid && registration.Generation == proof.ProviderGeneration
            && registration.Definition.Enabled && registration.Definition.ExpectedExtensionId == type.Governance.Origin.Reference,
            "provider_unavailable", "The document extension registration changed.");
        var extension = new ResolvedAepExtension(registration, AepExtensionCredentials.Create(registration, secrets));
        var manifest = await transport.DiscoverAsync(extension, registration.Definition.ExpectedExtensionId, ct);
        var declared = manifest.Contributions.StorageTypes?.SingleOrDefault(c => c.Id == proof.ContributionId)?.Versions.SingleOrDefault(v => v.Version == proof.Version);
        AspValidation.Require(declared?.Documents is not null && AepStorageProtocol.Digest(declared) == proof.Digest,
            "provider_stale", "The exact document contribution changed.");
        var server = manifest.Mcp?.Servers.SingleOrDefault(s => s.Id == mapping.McpServer)
            ?? throw new AspException("provider_unavailable", "The declared document MCP server is unavailable.");
        var handles = new List<SecretCapabilityHandle>(); McpClient? connection = null;
        try
        {
            _ = await storages.ResolveAsync(target.Storage, target.Consumer, ct);
            if (current.AccessMode != ControlPlaneAccessMode.System)
                await authorization.EnsurePermissionAsync(current.Current, AuthorizationPermissions.RunsExecute, ct);
            var values = await BindAsync(context, proof, manifest, handles, ct);
            var declarations = context.Collections.Where(c => c.Enabled && c.Uid == target.CollectionUid).Select(c => new AspDocumentCollectionDeclaration(c.Uid.ToString("N"),
                c.Schema, StorageDocumentGateway.Map(c.Policy, c.Uid.ToString("N")))).ToArray();
            var request = new AepStorageBindingRequest(proof.ContributionId, proof.Version, proof.Digest,
                JsonSerializer.SerializeToElement(effective.Store, AspProtocol.JsonOptions), values, context.Configuration, context.Envelope,
                JsonSerializer.SerializeToElement(effective.Policy, AspProtocol.JsonOptions),
                AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new { effective.Subject, effective.AuthorizationRevision }, AspProtocol.JsonOptions)),
                clock.GetUtcNow().AddSeconds(30))
            {
                DocumentCollections = JsonSerializer.SerializeToElement(declarations, AspProtocol.JsonOptions),
                DocumentSelection = JsonSerializer.SerializeToElement(effective.Selection, AspProtocol.JsonOptions)
            };
            using var http = transport.CreateConfigurationClient(extension);
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(registration.Definition.Endpoint, mapping.BindingPath))
            { Content = JsonContent.Create(request, options: AepProtocol.JsonOptions) };
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureDocumentBindingSuccessAsync(response, ct);
            await response.Content.LoadIntoBufferAsync(4096, ct);
            var lease = await response.Content.ReadFromJsonAsync<AepStorageBindingLease>(AepProtocol.JsonOptions, ct)
                ?? throw new AspException("invalid_result", "No document binding lease was returned.");
            AspValidation.Require(lease.Token is { Length: >= 32 and <= 512 } && lease.Token.All(char.IsAsciiLetterOrDigit)
                && lease.ExpiresAt > clock.GetUtcNow() && lease.ExpiresAt <= request.ExpiresAt, "invalid_result", "Invalid document binding lease.");
            connection = await transport.ConnectAsync(extension, server, new Dictionary<string, string> { [AepStorageProtocol.BindingHeader] = lease.Token }, ct);
            var client = await AspDocumentClient.ConnectAsync(connection, mapping.ToolPrefix, effective.Store, effective.Selection, ct);
            AspValidation.Require(client.Description.ProviderId == mapping.ProviderId && client.Description.ProviderVersion == mapping.ProviderVersion
                && StorageDigests.Schema(client.Description.ConfigurationSchema) == version.SchemaDigest
                && client.Description.SchemaDigest == AspDocumentValidation.Digest(declarations.Single().Schema)
                && AspDocumentValidation.IsNarrower(client.Description.Policy, effective.Policy), "invalid_result", "The document provider changed identity/schema or widened policy.");
            return await execute(client, ct);
        }
        finally
        {
            foreach (var handle in handles) capabilities.Revoke(handle);
            if (connection is not null) await connection.DisposeAsync();
        }
    }

    internal static async Task EnsureDocumentBindingSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "forbidden",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "backend_timeout",
            _ when (int)response.StatusCode >= 500 => "provider_unavailable",
            _ => "binding_refused"
        };
        if (code != "forbidden" && response.Content.Headers.ContentLength is null or <= 4096)
        {
            try
            {
                await response.Content.LoadIntoBufferAsync(4096, ct);
                using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
                    new JsonDocumentOptions { MaxDepth = 8 }, ct);
                if (body.RootElement.ValueKind == JsonValueKind.Object
                    && body.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is "backend_connection_refused" or "backend_unavailable" or "backend_timeout")
                    code = value.GetString()!;
            }
            catch (Exception exception) when (exception is JsonException or HttpRequestException or IOException)
            { /* A malformed or oversized error body never replaces the bounded status classification. */ }
        }
        throw new AspException(code, "The document binding could not be established.");
    }
}
