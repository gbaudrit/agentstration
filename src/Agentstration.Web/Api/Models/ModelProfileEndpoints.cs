using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;

namespace Agentstration.Web.Api.Models;

internal sealed class ListModelProfilesEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/", HandleAsync);
    private static Task<IResult> HandleAsync(
        string? provider,
        string? model,
        string? status,
        string? search,
        ModelProfileManagementService service,
        CancellationToken cancellationToken) => ModelManagementHttp.ExecuteAsync(async () =>
        {
            var profiles = await service.ListAsync(cancellationToken);
            var responses = new List<ModelProfileSummaryResponse>(profiles.Count);
            foreach (var profile in profiles)
            {
                var resolution = await service.ResolveAsync(profile.Value, cancellationToken);
                var usages = await service.GetUsagesAsync(profile.Value.Namespace, profile.Value.Metadata.Name, cancellationToken);
                responses.Add(new ModelProfileSummaryResponse(
                    profile.Value.Metadata.Name,
                    profile.Value.Metadata.Name,
                    new ModelProfileSummaryPropertiesResponse(
                        profile.Value.Definition.DisplayName,
                        profile.Value.Definition.Description,
                        new ModelProviderReferenceResponse(
                            profile.Value.Definition.Provider.Name,
                            profile.Value.Definition.Provider.Name,
                            resolution.Provider?.DisplayName),
                        new ModelReferenceResponse(profile.Value.Definition.Model.Name),
                        profile.Value.Definition.Generation,
                        profile.Value.Definition.Reasoning,
                        profile.Value.Definition.Output,
                        resolution.Status,
                        usages.Count),
                    profile.Value.Namespace.Value));
            }
            return Results.Ok(new ValueResponse<ModelProfileSummaryResponse>(responses));
        });
}

internal sealed class GetModelProfileEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{profileName}", HandleAsync);
    private static Task<IResult> HandleAsync(string profileName, string? resourceNamespace, HttpResponse response, ModelProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var @namespace = ModelManagementHttp.Namespace(resourceNamespace);
            var stored = await service.GetAsync(@namespace, profileName, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ModelProfile, profileName, @namespace));
            return ModelManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });
}

internal sealed class CreateModelProfileEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/", HandleAsync);
    private static Task<IResult> HandleAsync(CreateModelProfileRequest body, HttpResponse response, ModelProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.CreateAsync(new ModelProfileResource
            {
                Metadata = new ResourceMetadata { Name = body.Name, Namespace = ModelManagementHttp.Namespace(body.Namespace) },
                Kind = ResourceKinds.ModelProfile,
                ApiVersion = ManagementApiVersions.CoreV1,
                Definition = body.Properties
            }, cancellationToken);
            response.Headers.Location = $"/api/modelprofiles/{Uri.EscapeDataString(stored.Value.Metadata.Name)}?resourceNamespace={Uri.EscapeDataString(stored.Value.Namespace.Value)}";
            return ModelManagementHttp.ResourceResult(stored, response, StatusCodes.Status201Created);
        });
}

internal sealed class PutModelProfileEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPut("/{profileName}", HandleAsync);
    private static Task<IResult> HandleAsync(string profileName, string? resourceNamespace, PutModelProfileRequest body, HttpRequest request, HttpResponse response, ModelProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => ModelManagementHttp.ResourceResult(
            await service.PutAsync(ModelManagementHttp.Namespace(resourceNamespace), profileName, body.Properties, ModelManagementHttp.IfMatch(request), cancellationToken),
            response,
            StatusCodes.Status200OK));
}

internal sealed class DeleteModelProfileEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapDelete("/{profileName}", HandleAsync);
    private static Task<IResult> HandleAsync(string profileName, string? resourceNamespace, HttpRequest request, ModelProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            await service.DeleteAsync(ModelManagementHttp.Namespace(resourceNamespace), profileName, ModelManagementHttp.IfMatch(request), cancellationToken);
            return Results.NoContent();
        });
}

internal sealed class GetModelProfileUsagesEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{profileName}/usages", HandleAsync);
    private static Task<IResult> HandleAsync(string profileName, string? resourceNamespace, ModelProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var @namespace = ModelManagementHttp.Namespace(resourceNamespace);
            _ = await service.GetAsync(@namespace, profileName, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ModelProfile, profileName, @namespace));
            var usages = await service.GetUsagesAsync(@namespace, profileName, cancellationToken);
            var values = usages.Select(value => new ModelProfileUsageResponse(value.Kind, value.Name, value.Name, value.DisplayName)).ToArray();
            return Results.Ok(new ModelProfileUsagesResponse(values, values.Length));
        });
}

internal sealed class ResolveModelProfileEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{profileName}/resolution", HandleAsync);
    private static Task<IResult> HandleAsync(string profileName, string? resourceNamespace, ModelProfileManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var @namespace = ModelManagementHttp.Namespace(resourceNamespace);
            var profile = await service.GetAsync(@namespace, profileName, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ModelProfile, profileName, @namespace));
            return Results.Ok(ModelManagementHttp.Resolution(await service.ResolveAsync(profile.Value, cancellationToken)));
        });
}
