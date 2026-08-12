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

    private static Task<IResult> ListAsync(string? resourceGroup, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var profiles = await service.ListAsync(resourceGroup, cancellationToken);
            var values = new List<RuntimeProfileSummaryResponse>(profiles.Count);
            foreach (var profile in profiles)
            {
                var usages = await service.GetUsagesAsync(profile.Value.Id, cancellationToken);
                values.Add(new RuntimeProfileSummaryResponse(
                    profile.Value.Id,
                    profile.Value.Name,
                    profile.Value.ResourceGroup!,
                    profile.Value.Location ?? "local",
                    profile.Value.Properties,
                    usages.Count));
            }
            return Results.Ok(new ValueResponse<RuntimeProfileSummaryResponse>(values));
        });

    private static Task<IResult> GetAsync(string profileName, string? resourceGroup, HttpResponse response, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var group = ModelManagementHttp.ResourceGroup(resourceGroup);
            var stored = await service.GetAsync(group, profileName, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.RuntimeProfile, profileName));
            return ModelManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });

    private static Task<IResult> CreateAsync(CreateRuntimeProfileRequest body, HttpResponse response, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.CreateAsync(new RuntimeProfileResource
            {
                Id = RuntimeProfileManagementService.ProfileId(body.ResourceGroup, body.Name),
                Name = body.Name,
                Kind = ResourceKinds.RuntimeProfile,
                ApiVersion = ManagementApiVersions.CoreV1,
                ResourceGroup = body.ResourceGroup,
                Location = body.Location,
                Properties = body.Properties
            }, cancellationToken);
            response.Headers.Location = $"/api/runtimeprofiles/{Uri.EscapeDataString(stored.Value.Name)}";
            return ModelManagementHttp.ResourceResult(stored, response, StatusCodes.Status201Created);
        });

    private static Task<IResult> PutAsync(string profileName, string? resourceGroup, PutRuntimeProfileRequest body, HttpRequest request, HttpResponse response, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => ModelManagementHttp.ResourceResult(
            await service.PutAsync(ModelManagementHttp.ResourceGroup(resourceGroup), profileName, body.Properties, ModelManagementHttp.IfMatch(request), cancellationToken),
            response,
            StatusCodes.Status200OK));

    private static Task<IResult> DeleteAsync(string profileName, string? resourceGroup, HttpRequest request, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            await service.DeleteAsync(ModelManagementHttp.ResourceGroup(resourceGroup), profileName, ModelManagementHttp.IfMatch(request), cancellationToken);
            return Results.NoContent();
        });

    private static Task<IResult> UsagesAsync(string profileName, string? resourceGroup, RuntimeProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var group = ModelManagementHttp.ResourceGroup(resourceGroup);
            var id = RuntimeProfileManagementService.ProfileId(group, profileName);
            _ = await service.GetAsync(group, profileName, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.RuntimeProfile, profileName));
            var usages = await service.GetUsagesAsync(id, cancellationToken);
            var values = usages.Select(value => new RuntimeProfileUsageResponse(value.DeploymentUid.ToString("D"), value.Name, value.Environment, value.AgentName)).ToArray();
            return Results.Ok(new RuntimeProfileUsagesResponse(values, values.Length));
        });
}
