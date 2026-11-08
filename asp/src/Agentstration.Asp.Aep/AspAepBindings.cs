using System.Security.Claims;
using System.Security.Cryptography;
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

/// <summary>Provider-owned interpretation of opaque configuration and envelope. Must authorize physical execution independently.</summary>
public interface IAspAepBindingFactory
{
    IReadOnlyList<AepStorageTypeContribution> Contributions { get; }
    ValueTask<AspStoreBinding> CreateAsync(AepStorageBindingRequest request, ClaimsPrincipal authenticatedCaller,
        CancellationToken cancellationToken);
}

/// <summary>Short-lived bounded configuration leases. Content Tool arguments cannot configure or replace bindings.</summary>
public sealed class AspAepBindings(IAspAepBindingFactory factory, IHttpContextAccessor http,
    Func<ClaimsPrincipal, bool> canConfigure, TimeProvider clock)
{
    private sealed record Lease(string Subject, DateTimeOffset ExpiresAt, AspStoreBinding Binding, string ContextRevision);
    private readonly Dictionary<string, Lease> leases = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public async Task<AepStorageBindingLease> ConfigureAsync(AepStorageBindingRequest request, ClaimsPrincipal caller,
        CancellationToken cancellationToken)
    {
        var subject = Subject(caller);
        AspValidation.Require(canConfigure(caller), "forbidden", "The authenticated caller cannot configure logical bindings.");
        var version = factory.Contributions.SingleOrDefault(c => c.Id == request.ContributionId)?.Versions
            .SingleOrDefault(v => v.Version == request.Version);
        AspValidation.Require(version is not null && AepStorageProtocol.Digest(version) == request.Digest,
            "incompatible_provider", "The requested implementation is not available.");
        AspValidation.Require(request.ExpiresAt > clock.GetUtcNow() && request.ExpiresAt <= clock.GetUtcNow().AddSeconds(30)
            && request.AuthorizationRevision is { Length: 64 } && request.AuthorizationRevision.All(char.IsAsciiHexDigit)
            && request.Store.ValueKind == JsonValueKind.Object && request.Store.GetRawText().Length <= 4096
            && request.Configuration.ValueKind == JsonValueKind.Object && request.Configuration.GetRawText().Length <= 16384
            && request.Envelope.ValueKind == JsonValueKind.Object && request.Envelope.GetRawText().Length <= 16384
            && request.DatasetRule.ValueKind == JsonValueKind.Object && request.DatasetRule.GetRawText().Length <= 16384
            && request.Connection is { Count: <= 64 }, "invalid_request", "The protected binding is invalid or exceeds its limits.");
        ValidateDocument(request.Configuration);
        ValidateDocument(request.Envelope);
        ValidateDocument(request.DatasetRule);
        ValidateDocument(request.Store);
        var requirements = version.Requirements.Select(r => new AepValueRequirement(AepStorageProtocol.ContributionKind, request.ContributionId,
            r.Id, r.Required, r.Type, r.Protection, r.Format)).ToArray();
        AspValidation.Require(AepBoundValueValidator.Validate(request.Connection, requirements, AepStorageProtocol.ContributionKind,
            request.ContributionId, requireAll: true).Count == 0, "invalid_request", "The protected connection requirements are invalid.");
        var store = AspValidation.Parse<AspStoreRef>(request.Store);
        AspValidation.Store(store);
        var rule = AspValidation.Parse<AspAccessPolicy>(request.DatasetRule);
        AspValidation.Policy(rule);
        var binding = await factory.CreateAsync(request, caller, cancellationToken);
        AspValidation.Require(binding.Store == store, "invalid_policy", "The provider changed the logical target.");
        AspValidation.Policy(binding.Policy);
        AspValidation.Require(binding.Provider.ProviderId == version.Asp.ProviderId
            && binding.Provider.ProviderVersion == version.Asp.ProviderVersion
            && AepSchemaDigest.Compute(binding.Provider.ConfigurationSchema) == version.SchemaDigest,
            "incompatible_provider", "The provider identity or schema differs from its immutable contribution.");
        AspValidation.Require((!binding.Policy.CanList || version.Capabilities.List)
            && (!binding.Policy.CanStat || version.Capabilities.Stat)
            && (!binding.Policy.CanRead || version.Capabilities.Read)
            && (!binding.Policy.CanWrite || version.Capabilities.Write)
            && binding.Policy.MaxPageSize <= version.Limits.MaximumPageSize
            && binding.Policy.MaxReadBytes <= version.Limits.MaximumReadBytes
            && binding.Policy.MaxWriteBytes <= version.Limits.MaximumWriteBytes,
            "invalid_policy", "The binding exceeds the implementation's declared capabilities or limits.");
        // The factory validates/intersects its opaque envelope with the dataset rule.
        AspValidation.Require(AspValidation.IsNarrower(binding.Policy, rule), "invalid_policy", "The provider widened the requested restrictions.");
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        lock (gate)
        {
            foreach (var expired in leases.Where(pair => pair.Value.ExpiresAt <= clock.GetUtcNow()).Select(pair => pair.Key).ToArray())
                leases.Remove(expired);
            AspValidation.Require(leases.Count < 128, "capacity_exceeded", "The configured binding capacity has been reached.");
            leases.Add(token, new(subject, request.ExpiresAt, binding, request.AuthorizationRevision));
        }
        return new(token, request.ExpiresAt);
    }

    public ValueTask<AspStoreBinding> ResolveAsync(RequestContext<CallToolRequestParams> request, AspStoreRef store,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var caller = request.User ?? throw new AspException("forbidden", "An authenticated caller is required.");
        var subject = Subject(caller);
        var token = http.HttpContext?.Request.Headers[AepStorageProtocol.BindingHeader].ToString();
        Lease? lease = null;
        lock (gate)
        {
            AspValidation.Require(token is { Length: 64 } && leases.TryGetValue(token, out lease!)
                && lease.ExpiresAt > clock.GetUtcNow() && lease.Subject == subject && lease.Binding.Store == store,
                "forbidden", "The configured logical binding is missing, expired or inaccessible.");
        }
        var original = lease!.Binding;
        return ValueTask.FromResult(original with
        {
            Authorize = async (call, ct) =>
            {
                AspValidation.Require(Subject(call.User!) == subject && clock.GetUtcNow() < lease.ExpiresAt && canConfigure(call.User!),
                    "forbidden", "The authenticated caller's binding is no longer authorized.");
                // The provider independently authorizes the actual transport identity on EVERY operation.
                var grant = await original.Authorize(call, ct);
                AspValidation.Require(grant.Subject == subject, "forbidden", "The provider must authorize its actual authenticated caller.");
                return grant with
                {
                    AuthorizationRevision = AspValidation.Digest(System.Text.Encoding.UTF8.GetBytes(
                    grant.AuthorizationRevision + ":" + lease.ContextRevision))
                };
            }
        });
    }

    private static string Subject(ClaimsPrincipal caller)
    {
        var value = caller?.Identity?.IsAuthenticated == true ? caller.FindFirst(ClaimTypes.NameIdentifier)?.Value : null;
        AspValidation.Require(value is { Length: > 0 and <= 128 }, "forbidden", "An authenticated transport subject is required.");
        return value;
    }

    private static void ValidateDocument(JsonElement value, int depth = 0)
    {
        AspValidation.Require(depth <= 16, "invalid_request", "The protected binding exceeds the nesting limit.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                AspValidation.Require(names.Add(property.Name), "invalid_request", "Duplicate configuration keys are not supported.");
                ValidateDocument(property.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateDocument(item, depth + 1);
    }


}

public static class AspAepBindingEndpoints
{
    public static IEndpointConventionBuilder MapAspAepBindings(this IEndpointRouteBuilder endpoints) => endpoints.MapPost(
        AepStorageProtocol.BindingPath, async (HttpContext http,
            AspAepBindings bindings, CancellationToken cancellationToken) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var request = await ReadAsync(http.Request, cancellationToken);
                return Results.Json(await bindings.ConfigureAsync(request, http.User, cancellationToken), AepProtocol.JsonOptions);
            }
            catch (AspException exception)
            {
                return Results.Problem(statusCode: exception.Code switch
                { "forbidden" => 403, "limit_exceeded" => 413, "invalid_request" => 400, _ => 422 },
                    title: "The protected Storage binding was refused.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return Results.Problem(statusCode: 408, title: "The protected Storage binding timed out."); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException)
            { return Results.Problem(statusCode: 503, title: "The protected Storage binding is unavailable."); }
        }).RequireAuthorization(AepAuthenticationDefaults.Policy);

    private static async Task<AepStorageBindingRequest> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        const int maximumBytes = 100000;
        AspValidation.Require(request.ContentLength is null or <= maximumBytes, "limit_exceeded", "The binding payload exceeds its bound.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[maximumBytes + 1];
        try
        {
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(count), deadline.Token);
                if (read == 0) break;
                count += read;
            }
            AspValidation.Require(count <= maximumBytes, "limit_exceeded", "The binding payload exceeds its bound.");
            try
            {
                return JsonSerializer.Deserialize<AepStorageBindingRequest>(buffer.AsSpan(0, count), AepProtocol.JsonOptions)
                    ?? throw new AspException("invalid_request", "A binding request is required.");
            }
            catch (JsonException) { throw new AspException("invalid_request", "The binding payload is malformed."); }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
