using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Web.Hosting;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

public static class ExtensionEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/extensions", ListAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        endpoints.MapPost("/api/extensions/discover", DiscoverAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        var registrations = endpoints.MapGroup("/api/extensionregistrations");
        registrations.MapGet("/", ListRegistrationsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        registrations.MapGet("/{registrationName}", GetRegistrationAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        registrations.MapPost("/", CreateRegistrationAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        registrations.MapPut("/{registrationName}", PutRegistrationAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        registrations.MapDelete("/{registrationName}", DeleteRegistrationAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }

    private static Task<IResult> DiscoverAsync(
        ExtensionSourceDiscoveryService discovery,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => Results.Ok(await discovery.DiscoverAsync(cancellationToken)));

    private static async Task<IResult> ListAsync(
        ExtensionManagementService service,
        CancellationToken cancellationToken) =>
        await ModelManagementHttp.ExecuteAsync(async () =>
        {
            var views = await service.ListAsync(cancellationToken);
            return Results.Ok(new ValueResponse<ExtensionResponse>(views.Select(Map).ToArray()));
        });

    private static Task<IResult> ListRegistrationsAsync(
        ExtensionRegistrationManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => Results.Ok(new ValueResponse<ExtensionRegistrationResource>(
            (await service.ListAsync(cancellationToken)).Select(value => value.Value).ToArray())));

    private static Task<IResult> GetRegistrationAsync(
        string registrationName,
        string? resourceNamespace,
        HttpResponse response,
        ExtensionRegistrationManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.GetAsync(ModelManagementHttp.Namespace(resourceNamespace), registrationName, cancellationToken)
                ?? throw new ExtensionRegistrationNotFoundException(new(ModelManagementHttp.Namespace(resourceNamespace), ResourceKinds.ExtensionRegistration, registrationName));
            return ModelManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });

    private static Task<IResult> CreateRegistrationAsync(
        CreateExtensionRegistrationRequest body,
        HttpResponse response,
        ExtensionRegistrationManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.CreateAsync(new ExtensionRegistrationResource
            {
                Metadata = new ResourceMetadata { Name = body.Name, Namespace = ModelManagementHttp.Namespace(body.Namespace) },
                Kind = ResourceKinds.ExtensionRegistration,
                ApiVersion = ManagementApiVersions.CoreV1,
                Definition = body.Properties
            }, cancellationToken);
            response.Headers.Location = $"/api/extensionregistrations/{Uri.EscapeDataString(stored.Value.Name)}?resourceNamespace={Uri.EscapeDataString(stored.Value.Namespace.Value)}";
            return ModelManagementHttp.ResourceResult(stored, response, StatusCodes.Status201Created);
        });

    private static Task<IResult> PutRegistrationAsync(
        string registrationName,
        string? resourceNamespace,
        PutExtensionRegistrationRequest body,
        HttpRequest request,
        HttpResponse response,
        ExtensionRegistrationManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => ModelManagementHttp.ResourceResult(
            await service.PutAsync(ModelManagementHttp.Namespace(resourceNamespace), registrationName, body.Properties, ModelManagementHttp.IfMatch(request), cancellationToken),
            response,
            StatusCodes.Status200OK));

    private static Task<IResult> DeleteRegistrationAsync(
        string registrationName,
        string? resourceNamespace,
        HttpRequest request,
        ExtensionRegistrationManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            await service.DeleteAsync(ModelManagementHttp.Namespace(resourceNamespace), registrationName, ModelManagementHttp.IfMatch(request), cancellationToken);
            return Results.NoContent();
        });

    private static ExtensionResponse Map(ExtensionView view) => new(
        view.RegistrationName,
        view.RegistrationNamespace,
        view.Endpoint,
        view.Status,
        view.Extension is null ? null : new ExtensionIdentityResponse(
            view.Extension.Id,
            view.Extension.Name,
            view.Extension.Version,
            view.Extension.Description),
        view.Contributions.Select(value => new ExtensionContributionResponse(value.Kind, value.Id)).ToArray(),
        view.OptionSets.Select(optionSet => new ExtensionOptionSetResponse(
            optionSet.Id,
            optionSet.ContributionKind,
            optionSet.ContributionId,
            optionSet.Scope,
            optionSet.PreferredVersion,
            optionSet.Versions.Select(version => new ExtensionOptionSetVersionResponse(
                version.Version,
                version.SchemaDigest,
                version.Schema,
                version.Deprecated)).ToArray(),
            (optionSet.Migrations ?? []).Select(migration => new ExtensionOptionMigrationDescriptorResponse(
                migration.FromVersion,
                migration.ToVersion)).ToArray())).ToArray(),
        view.Usages.Select(usage => new ExtensionOptionUsageResponse(
            usage.ProfileName,
            usage.ProfileNamespace,
            usage.OptionSet,
            usage.Version,
            usage.SchemaDigest,
            usage.Status,
            usage.Issues)).ToArray(),
        view.Providers.Select(provider => new ExtensionProviderBindingResponse(
            provider.Name,
            provider.Namespace,
            provider.ContributionId)).ToArray(),
        view.Details,
        view.DiscoverySource);
}
