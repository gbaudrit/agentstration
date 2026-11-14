using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Asp.Aep;

public interface IAspDocumentAepBindingFactory
{
    IReadOnlyList<AepStorageTypeContribution> Contributions { get; }
    ValueTask<AspDocumentBinding> CreateAsync(AepStorageBindingRequest request, ClaimsPrincipal caller, CancellationToken ct);
}

/// <summary>The existing protected configuration exchange, specialized for the independent document profile.</summary>
public sealed class AspDocumentAepBindings(IAspDocumentAepBindingFactory factory, IHttpContextAccessor http,
    Func<ClaimsPrincipal, bool> canConfigure, TimeProvider clock)
{
    private sealed record Lease(string Subject, DateTimeOffset Expires, string Revision, AspDocumentSelection Selection, AspDocumentBinding Binding);
    private readonly Dictionary<string, Lease> leases = new(StringComparer.Ordinal);
    private readonly object gate = new();
    public async Task<AepStorageBindingLease> ConfigureAsync(AepStorageBindingRequest request, ClaimsPrincipal caller, CancellationToken ct)
    {
        var subject = Subject(caller);
        AspValidation.Require(canConfigure(caller), "forbidden", "The caller cannot configure document bindings.");
        var version = factory.Contributions.SingleOrDefault(c => c.Id == request.ContributionId)?.Versions.SingleOrDefault(v => v.Version == request.Version);
        AspValidation.Require(version?.Documents is not null && version.Asp.Profile == AspDocuments.Profile
            && AepStorageProtocol.Digest(version) == request.Digest, "incompatible_provider", "The exact document contribution is unavailable.");
        AspValidation.Require(request.ExpiresAt > clock.GetUtcNow() && request.ExpiresAt <= clock.GetUtcNow().AddSeconds(30)
            && request.AuthorizationRevision is { Length: 64 } && request.AuthorizationRevision.All(char.IsAsciiHexDigit)
            && request.DocumentCollections is { ValueKind: JsonValueKind.Array } collections && collections.GetArrayLength() is > 0 and <= 32
            && request.Connection.Count <= 64 && JsonSerializer.SerializeToUtf8Bytes(request, AepProtocol.JsonOptions).Length <= 100000,
            "invalid_request", "The document binding exceeds its limits.");
        AspDocumentValidation.ValidateJson(JsonSerializer.SerializeToElement(request, AepProtocol.JsonOptions));
        var requirements = version.Requirements.Select(r => new AepValueRequirement(AepStorageProtocol.ContributionKind,
            request.ContributionId, r.Id, r.Required, r.Type, r.Protection, r.Format)).ToArray();
        AspValidation.Require(AepBoundValueValidator.Validate(request.Connection, requirements, AepStorageProtocol.ContributionKind,
            request.ContributionId, requireAll: true).Count == 0, "invalid_request", "Invalid document connection requirements.");
        var store = AspValidation.Parse<AspStoreRef>(request.Store); AspValidation.Store(store);
        var rule = AspValidation.Parse<AspDocumentPolicy>(request.DatasetRule); AspDocumentValidation.Policy(rule);
        var binding = await factory.CreateAsync(request, caller, ct);
        AspValidation.Require(binding.Store == store && AspDocumentValidation.IsNarrower(binding.Policy, rule),
            "invalid_policy", "The provider widened the requested target/policy.");
        AspValidation.Require(binding.Provider.ProviderId == version.Asp.ProviderId && binding.Provider.ProviderVersion == version.Asp.ProviderVersion
            && AepSchemaDigest.Compute(binding.Provider.ConfigurationSchema) == version.SchemaDigest,
            "incompatible_provider", "The provider changed immutable contribution evidence.");
        var declared = version.Documents;
        AspValidation.Require((!binding.Policy.Get || declared.Get) && (!binding.Policy.Put || declared.Put)
            && (!binding.Policy.Delete || declared.Delete) && (!binding.Policy.Query || declared.Query)
            && (!binding.Policy.Publish || declared.ImmutablePublications)
            && binding.Policy.QueryProfiles!.All(id => declared.QueryProfiles.Any(p => p.Id == id)),
            "invalid_policy", "The document binding exceeds its advertised operations.");
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        lock (gate)
        {
            foreach (var key in leases.Where(v => v.Value.Expires <= clock.GetUtcNow()).Select(v => v.Key).ToArray()) leases.Remove(key);
            AspValidation.Require(leases.Count < 1024, "capacity_exceeded", "Document binding capacity reached.");
            var selection = request.DocumentSelection is { } fixedSelection ? AspValidation.Parse<AspDocumentSelection>(fixedSelection) : new(rule.Collection);
            AspValidation.Require(selection.Collection == binding.Policy.Collection, "invalid_policy", "The selected publication target changed.");
            leases.Add(token, new(subject, request.ExpiresAt, request.AuthorizationRevision, selection, binding));
        }
        return new(token, request.ExpiresAt);
    }
    public ValueTask<AspDocumentBinding> ResolveAsync(RequestContext<CallToolRequestParams> request, AspStoreRef store,
        AspDocumentSelection selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var caller = request.User ?? throw new AspException("forbidden", "Authentication is required.");
        var subject = Subject(caller); var token = http.HttpContext?.Request.Headers[AepStorageProtocol.BindingHeader].ToString(); Lease? lease;
        lock (gate) leases.TryGetValue(token ?? "", out lease);
        AspValidation.Require(lease is not null && lease.Subject == subject && lease.Expires > clock.GetUtcNow()
            && lease.Binding.Store == store && lease.Selection == selection && lease.Binding.Policy.Collection == selection.Collection && canConfigure(caller),
            "forbidden", "The document configuration lease is unavailable or unauthorized.");
        var value = lease;
        return ValueTask.FromResult(value.Binding with
        {
            Authorize = async (call, cancellation) =>
            {
                AspValidation.Require(value.Expires > clock.GetUtcNow() && canConfigure(call.User!) && Subject(call.User!) == subject,
                    "forbidden", "The document caller is no longer authorized.");
                var grant = await value.Binding.Authorize(call, cancellation);
                AspValidation.Require(grant.Subject == subject && grant.Store == store, "forbidden", "The provider changed authenticated identity.");
                return grant with { Selection = selection, AuthorizationRevision = AspValidation.Digest(Encoding.UTF8.GetBytes(grant.AuthorizationRevision + ":" + value.Revision)) };
            }
        });
    }
    private static string Subject(ClaimsPrincipal caller)
    {
        var subject = caller.Identity?.IsAuthenticated == true ? caller.FindFirstValue(ClaimTypes.NameIdentifier) : null;
        AspValidation.Require(subject is { Length: > 0 and <= 128 }, "forbidden", "An authenticated transport subject is required.");
        return subject;
    }
}

public static class AspDocumentAepEndpoints
{
    public static IEndpointConventionBuilder MapAspDocumentAepBindings(this IEndpointRouteBuilder endpoints) => endpoints.MapPost(
        AepStorageProtocol.BindingPath, async (HttpContext http, AspDocumentAepBindings bindings, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var request = await AspAepBindingEndpoints.ReadAsync(http.Request, ct);
                return Results.Json(await bindings.ConfigureAsync(request, http.User, ct), AepProtocol.JsonOptions);
            }
            catch (AspException ex)
            {
                var code = ex.Code is "backend_connection_refused" or "backend_unavailable" or "backend_timeout" or "forbidden"
                    ? ex.Code : "binding_refused";
                var status = code switch
                {
                    "forbidden" => 403,
                    "backend_timeout" => 504,
                    "backend_connection_refused" or "backend_unavailable" => 503,
                    _ => 422
                };
                return Results.Problem(statusCode: status, title: "The protected document binding could not be established.",
                    extensions: new Dictionary<string, object?> { ["code"] = code });
            }
        }).RequireAuthorization(AepAuthenticationDefaults.Policy);
}
