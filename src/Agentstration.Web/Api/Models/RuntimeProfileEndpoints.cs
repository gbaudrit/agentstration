using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;

namespace Agentstration.Web.Api.Models;

internal static class RuntimeProfileEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync);
        group.MapGet("/{profileName}/usages", UsagesAsync);
        group.MapGet("/{profileName}", GetAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{profileName}", PutAsync);
        group.MapDelete("/{profileName}", DeleteAsync);
    }

    private static Task<IResult> ListAsync(RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var profiles = await service.ListAsync(cancellationToken);
            var values = new List<RuntimeProfileSummaryResponse>(profiles.Count);
            foreach (var profile in profiles)
            {
                var usages = await service.GetUsagesAsync(profile.Value.Id, cancellationToken);
                values.Add(new RuntimeProfileSummaryResponse(
                    profile.Value.Id,
                    profile.Value.Name,
                    profile.Value.Properties,
                    usages.Count));
            }
            return Results.Ok(new ValueResponse<RuntimeProfileSummaryResponse>(values));
        });

    private static Task<IResult> GetAsync(string profileName, HttpResponse response, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.GetAsync(profileName, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.RuntimeProfile, profileName));
            return ModelManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });

    private static Task<IResult> CreateAsync(CreateRuntimeProfileRequest body, HttpResponse response, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.CreateAsync(new RuntimeProfileResource
            {
                Id = RuntimeProfileManagementService.ProfileId(body.Name),
                Name = body.Name,
                Kind = ResourceKinds.RuntimeProfile,
                ApiVersion = ManagementApiVersions.CoreV1,
                Properties = body.Properties
            }, cancellationToken);
            response.Headers.Location = $"/api/runtimeprofiles/{Uri.EscapeDataString(stored.Value.Name)}";
            return ModelManagementHttp.ResourceResult(stored, response, StatusCodes.Status201Created);
        });

    private static Task<IResult> PutAsync(string profileName, PutRuntimeProfileRequest body, HttpRequest request, HttpResponse response, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => ModelManagementHttp.ResourceResult(
            await service.PutAsync(profileName, body.Properties, ModelManagementHttp.IfMatch(request), cancellationToken),
            response,
            StatusCodes.Status200OK));

    private static Task<IResult> DeleteAsync(string profileName, HttpRequest request, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            await service.DeleteAsync(profileName, ModelManagementHttp.IfMatch(request), cancellationToken);
            return Results.NoContent();
        });

    private static Task<IResult> UsagesAsync(string profileName, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var id = RuntimeProfileManagementService.ProfileId(profileName);
            _ = await service.GetAsync(profileName, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.RuntimeProfile, profileName));
            var usages = await service.GetUsagesAsync(id, cancellationToken);
            var values = usages.Select(value => new RuntimeProfileUsageResponse(value.DeploymentUid.ToString("D"), value.Name, value.Environment, value.AgentName)).ToArray();
            return Results.Ok(new RuntimeProfileUsagesResponse(values, values.Length));
        });
}
