using System.Text;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

internal static class SecretEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/resource-scopes/targets", async (string kind, ResourceScopeOperationService scopes, CancellationToken token) =>
            Results.Ok((await scopes.ListTargetsAsync(kind, AuthorizationPermissions.ResourcesWrite, token))
                .Select(value => new ResourceScopeTargetResponse(value.ScopeRef, value.Kind, value.DisplayName, value.CanWrite))))
            .Produces<IEnumerable<ResourceScopeTargetResponse>>()
            .WithSummary("List writable resource scope targets")
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);

        var vaults = endpoints.MapGroup("/api/vaults");
        vaults.MapGet("/", async (SecretManagementService service, CancellationToken token) => Results.Ok((await service.ListVaultViewsAsync(token)).Select(Response))).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        vaults.MapGet("/{name}", async (string name, string? scopeRef, HttpResponse response, SecretManagementService service, CancellationToken token) => await Execute(async () =>
        {
            var scope = Scope(scopeRef);
            var stored = scope is null ? await service.GetVaultAsync(name, token) : await service.GetVaultExactAsync(scope.Value, name, token);
            if (stored is null) throw new VaultResourceNotFoundException(name);
            response.Headers.ETag = stored.ETag;
            return Results.Ok(Response(scope is null ? await service.GetVaultViewAsync(name, token) : await service.GetVaultViewExactAsync(scope.Value, name, token)));
        })).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        vaults.MapPost("/", async (CreateVaultRequest body, HttpResponse response, SecretManagementService service, CancellationToken token) => await Execute(async () => Resource(await service.CreateVaultAsync(new VaultResource { ApiVersion = ManagementApiVersions.CoreV1, Kind = ResourceKinds.Vault, Metadata = new() { Name = body.Name }, ScopeRef = body.ScopeRef, Definition = body.Properties }, token), response, 201))).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        vaults.MapPut("/{name}", async (string name, string? scopeRef, PutVaultRequest body, HttpRequest request, HttpResponse response, SecretManagementService service, CancellationToken token) => await Execute(async () => Resource(Scope(scopeRef) is { } scope ? await service.PutVaultExactAsync(scope, name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token) : await service.PutVaultAsync(name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token), response, 200))).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        vaults.MapPost("/{name}/initialize", async (string name, string? scopeRef, SecretManagementService service, CancellationToken token) => await Execute(async () =>
        {
            var result = Scope(scopeRef) is { } scope ? await service.InitializeVaultExactAsync(scope, name, token) : await service.InitializeVaultAsync(name, token);
            return Results.Ok(new VaultInitializationResponse("initialized", result.KeyFilePath));
        })).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        vaults.MapDelete("/{name}", async (string name, string? scopeRef, HttpRequest request, SecretManagementService service, CancellationToken token) => await Execute(async () => { if (Scope(scopeRef) is { } scope) await service.DeleteVaultExactAsync(scope, name, request.Headers.IfMatch.FirstOrDefault(), token); else await service.DeleteVaultAsync(name, request.Headers.IfMatch.FirstOrDefault(), token); return Results.NoContent(); })).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);

        var secrets = endpoints.MapGroup("/api/secrets");
        secrets.MapGet("/", async (SecretManagementService service, CancellationToken token) => Results.Ok((await service.ListSecretsAsync(token)).Select(Response))).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        secrets.MapGet("/{name}", async (string name, string? scopeRef, HttpResponse response, SecretManagementService service, CancellationToken token) => await Execute(async () => { var view = Scope(scopeRef) is { } scope ? await service.GetSecretViewExactAsync(scope, name, token) : await service.GetSecretViewAsync(name, token); response.Headers.ETag = view.Resource.ETag; return Results.Ok(Response(view)); })).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        secrets.MapGet("/{name}/usages", async (string name, string? scopeRef, SecretManagementService service, CancellationToken token) => await Execute(async () =>
        {
            var scope = Scope(scopeRef);
            _ = scope is null
                ? await service.GetSecretAsync(name, token) ?? throw new SecretResourceNotFoundException(name)
                : await service.GetSecretExactAsync(scope.Value, name, token) ?? throw new SecretResourceNotFoundException(name);
            var usages = (scope is null ? await service.GetSecretUsagesAsync(name, token) : await service.GetSecretUsagesAsync(scope.Value, name, token)).Select(value => new SecretUsageResponse(value.Kind, value.Name, value.DisplayName, $"/modelproviders/{Uri.EscapeDataString(value.Name)}")).ToArray();
            return Results.Ok(new SecretUsagesResponse(usages, usages.Length));
        })).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        secrets.MapPost("/", async (CreateSecretRequest body, HttpResponse response, SecretManagementService service, CancellationToken token) => await Execute(async () => Resource(await service.CreateSecretAsync(new SecretResource { ApiVersion = ManagementApiVersions.CoreV1, Kind = ResourceKinds.Secret, Metadata = new() { Name = body.Name }, ScopeRef = body.ScopeRef, Definition = body.Properties }, token), response, 201))).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        secrets.MapPut("/{name}", async (string name, string? scopeRef, PutSecretRequest body, HttpRequest request, HttpResponse response, SecretManagementService service, CancellationToken token) => await Execute(async () => Resource(Scope(scopeRef) is { } scope ? await service.PutSecretExactAsync(scope, name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token) : await service.PutSecretAsync(name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token), response, 200))).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        secrets.MapPut("/{name}/value", async (string name, string? scopeRef, SetSecretValueRequest body, SecretManagementService service, CancellationToken token) => await Execute(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(body.Value); using var value = new SecretValue(Encoding.UTF8.GetBytes(body.Value)); if (Scope(scopeRef) is { } scope) await service.SetValueExactAsync(scope, name, value, token); else await service.SetValueAsync(name, value, token); return Results.NoContent();
        })).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        secrets.MapDelete("/{name}/value", async (string name, string? scopeRef, SecretManagementService service, CancellationToken token) => await Execute(async () => { if (Scope(scopeRef) is { } scope) await service.DeleteValueExactAsync(scope, name, token); else await service.DeleteValueAsync(name, token); return Results.NoContent(); })).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
        secrets.MapDelete("/{name}", async (string name, string? scopeRef, HttpRequest request, SecretManagementService service, CancellationToken token) => await Execute(async () => { if (Scope(scopeRef) is { } scope) await service.DeleteSecretExactAsync(scope, name, request.Headers.IfMatch.FirstOrDefault(), token); else await service.DeleteSecretAsync(name, request.Headers.IfMatch.FirstOrDefault(), token); return Results.NoContent(); })).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }

    private static SecretResponse Response(SecretView value) => new(value.Resource, value.ValueStatus.ToString(), value.ValueStatus == SecretValueStatus.Configured);
    private static VaultResponse Response(VaultView value) => new(value.Resource, value.Status);
    private static ResourceScopeRef? Scope(string? value) => string.IsNullOrWhiteSpace(value) ? null : ResourceScopeRef.Parse(value);
    private static IResult Resource<T>(StoredResource<T> stored, HttpResponse response, int status) where T : Resource { response.Headers.ETag = stored.ETag; return Results.Json(stored.Value, statusCode: status); }
    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (SecretResourceNotFoundException exception) { return Results.Problem(statusCode: 404, title: "Secret not found", detail: exception.Message); }
        catch (VaultResourceNotFoundException exception) { return Results.Problem(statusCode: 404, title: "Vault not found", detail: exception.Message); }
        catch (VaultInUseException exception) { return Results.Problem(statusCode: 409, title: "Vault in use", detail: exception.Message); }
        catch (VaultAlreadyInitializedException exception) { return Results.Problem(statusCode: 409, title: "Vault already initialized", detail: exception.Message); }
        catch (VaultInitializationNotSupportedException exception) { return Results.Problem(statusCode: 422, title: "Vault initialization unsupported", detail: exception.Message); }
        catch (ControlPlaneConcurrencyException exception) { return Results.Problem(statusCode: 409, title: "Resource version conflict", detail: exception.Message); }
        catch (ResourceScopeAccessDeniedException exception) { return Results.Problem(statusCode: 403, title: "Resource scope access denied", detail: exception.Message); }
        catch (ResourceScopePolicyException exception) { return Results.Problem(statusCode: 422, title: "Invalid resource scope", detail: exception.Message); }
        catch (Exception exception) when (exception is SecretManagementException or ArgumentException or InvalidOperationException) { return Results.Problem(statusCode: 422, title: "Invalid secret operation", detail: exception.Message); }
    }
}
