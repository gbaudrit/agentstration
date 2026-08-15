using System.Text;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Web.Api.Models;

internal static class SecretEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var vaults = endpoints.MapGroup("/api/vaults");
        vaults.MapGet("/", async (SecretManagementService service, CancellationToken token) => Results.Ok((await service.ListVaultViewsAsync(token)).Select(Response)));
        vaults.MapGet("/{name}", async (string name, HttpResponse response, SecretManagementService service, CancellationToken token) => await Execute(async () =>
        {
            var stored = await service.GetVaultAsync(name, token) ?? throw new VaultResourceNotFoundException(name); response.Headers.ETag = stored.ETag; return Results.Ok(Response(await service.GetVaultViewAsync(name, token)));
        }));
        vaults.MapPost("/", async (CreateVaultRequest body, HttpResponse response, SecretManagementService service, CancellationToken token) => await Execute(async () => Resource(await service.CreateVaultAsync(new VaultResource { ApiVersion = ManagementApiVersions.CoreV1, Kind = ResourceKinds.Vault, Metadata = new() { Name = body.Name }, Definition = body.Properties }, token), response, 201)));
        vaults.MapPut("/{name}", async (string name, PutVaultRequest body, HttpRequest request, HttpResponse response, SecretManagementService service, CancellationToken token) => await Execute(async () => Resource(await service.PutVaultAsync(name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token), response, 200)));
        vaults.MapPost("/{name}/initialize", async (string name, SecretManagementService service, CancellationToken token) => await Execute(async () =>
        {
            var result = await service.InitializeVaultAsync(name, token);
            return Results.Ok(new VaultInitializationResponse("initialized", result.KeyFilePath));
        })).RequireAuthorization("Administrator");
        vaults.MapDelete("/{name}", async (string name, HttpRequest request, SecretManagementService service, CancellationToken token) => await Execute(async () => { await service.DeleteVaultAsync(name, request.Headers.IfMatch.FirstOrDefault(), token); return Results.NoContent(); }));

        var secrets = endpoints.MapGroup("/api/secrets");
        secrets.MapGet("/", async (SecretManagementService service, CancellationToken token) => Results.Ok((await service.ListSecretsAsync(token)).Select(Response)));
        secrets.MapGet("/{name}", async (string name, HttpResponse response, SecretManagementService service, CancellationToken token) => await Execute(async () => { var view = await service.GetSecretViewAsync(name, token); response.Headers.ETag = view.Resource.ETag; return Results.Ok(Response(view)); }));
        secrets.MapGet("/{name}/usages", async (string name, SecretManagementService service, CancellationToken token) => await Execute(async () =>
        {
            _ = await service.GetSecretAsync(name, token) ?? throw new SecretResourceNotFoundException(name);
            var usages = (await service.GetSecretUsagesAsync(name, token)).Select(value => new SecretUsageResponse(value.Kind, value.Name, value.DisplayName, $"/modelproviders/{Uri.EscapeDataString(value.Name)}")).ToArray();
            return Results.Ok(new SecretUsagesResponse(usages, usages.Length));
        }));
        secrets.MapPost("/", async (CreateSecretRequest body, HttpResponse response, SecretManagementService service, CancellationToken token) => await Execute(async () => Resource(await service.CreateSecretAsync(new SecretResource { ApiVersion = ManagementApiVersions.CoreV1, Kind = ResourceKinds.Secret, Metadata = new() { Name = body.Name }, Definition = body.Properties }, token), response, 201)));
        secrets.MapPut("/{name}", async (string name, PutSecretRequest body, HttpRequest request, HttpResponse response, SecretManagementService service, CancellationToken token) => await Execute(async () => Resource(await service.PutSecretAsync(name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token), response, 200)));
        secrets.MapPut("/{name}/value", async (string name, SetSecretValueRequest body, SecretManagementService service, CancellationToken token) => await Execute(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(body.Value); using var value = new SecretValue(Encoding.UTF8.GetBytes(body.Value)); await service.SetValueAsync(name, value, token); return Results.NoContent();
        }));
        secrets.MapDelete("/{name}/value", async (string name, SecretManagementService service, CancellationToken token) => await Execute(async () => { await service.DeleteValueAsync(name, token); return Results.NoContent(); }));
        secrets.MapDelete("/{name}", async (string name, HttpRequest request, SecretManagementService service, CancellationToken token) => await Execute(async () => { await service.DeleteSecretAsync(name, request.Headers.IfMatch.FirstOrDefault(), token); return Results.NoContent(); }));
    }

    private static SecretResponse Response(SecretView value) => new(value.Resource, value.ValueStatus.ToString(), value.ValueStatus == SecretValueStatus.Configured);
    private static VaultResponse Response(VaultView value) => new(value.Resource, value.Status);
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
        catch (Exception exception) when (exception is SecretManagementException or ArgumentException or InvalidOperationException) { return Results.Problem(statusCode: 422, title: "Invalid secret operation", detail: exception.Message); }
    }
}
