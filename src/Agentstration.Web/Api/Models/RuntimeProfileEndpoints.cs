using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

internal static class RuntimeProfileEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/{profileName}/usages", UsagesAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/{profileName}", GetAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/", CreateAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPut("/{profileName}", PutAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapDelete("/{profileName}", DeleteAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }

    private static Task<IResult> ListAsync(RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var profiles = await service.ListAsync(cancellationToken);
            var values = new List<RuntimeProfileSummaryResponse>(profiles.Count);
            foreach (var profile in profiles)
            {
                var usages = await service.GetUsagesAsync(profile.Value.Namespace, profile.Value.Metadata.Name, cancellationToken);
                values.Add(new RuntimeProfileSummaryResponse(
                    profile.Value.Metadata.Name,
                    profile.Value.Name,
                    profile.Value.Definition,
                    usages.Count,
                    profile.Value.Namespace.Value));
            }
            return Results.Ok(new ValueResponse<RuntimeProfileSummaryResponse>(values));
        });

    private static Task<IResult> GetAsync(string profileName, string? resourceNamespace, HttpResponse response, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var @namespace = ModelManagementHttp.Namespace(resourceNamespace);
            var stored = await service.GetAsync(@namespace, profileName, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.RuntimeProfile, profileName, @namespace));
            return ModelManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });

    private static Task<IResult> CreateAsync(CreateRuntimeProfileRequest body, HttpResponse response, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.CreateAsync(new RuntimeProfileResource
            {
                Metadata = new ResourceMetadata { Name = body.Name, Namespace = ModelManagementHttp.Namespace(body.Namespace) },
                Kind = ResourceKinds.RuntimeProfile,
                ApiVersion = ManagementApiVersions.CoreV1,
                Definition = body.Properties
            }, cancellationToken);
            response.Headers.Location = $"/api/runtimeprofiles/{Uri.EscapeDataString(stored.Value.Name)}?resourceNamespace={Uri.EscapeDataString(stored.Value.Namespace.Value)}";
            return ModelManagementHttp.ResourceResult(stored, response, StatusCodes.Status201Created);
        });

    private static Task<IResult> PutAsync(string profileName, string? resourceNamespace, PutRuntimeProfileRequest body, HttpRequest request, HttpResponse response, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => ModelManagementHttp.ResourceResult(
            await service.PutAsync(ModelManagementHttp.Namespace(resourceNamespace), profileName, body.Properties, ModelManagementHttp.IfMatch(request), cancellationToken),
            response,
            StatusCodes.Status200OK));

    private static Task<IResult> DeleteAsync(string profileName, string? resourceNamespace, HttpRequest request, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            await service.DeleteAsync(ModelManagementHttp.Namespace(resourceNamespace), profileName, ModelManagementHttp.IfMatch(request), cancellationToken);
            return Results.NoContent();
        });

    private static Task<IResult> UsagesAsync(string profileName, string? resourceNamespace, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var @namespace = ModelManagementHttp.Namespace(resourceNamespace);
            _ = await service.GetAsync(@namespace, profileName, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.RuntimeProfile, profileName, @namespace));
            var usages = await service.GetUsagesAsync(@namespace, profileName, cancellationToken);
            var values = usages.Select(value => new RuntimeProfileUsageResponse(value.DeploymentUid.ToString("D"), value.Name, value.Environment, value.AgentName)).ToArray();
            return Results.Ok(new RuntimeProfileUsagesResponse(values, values.Length));
        });
}
