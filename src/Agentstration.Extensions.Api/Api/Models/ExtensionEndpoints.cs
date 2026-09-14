using Agentstration.Identity.Contracts;
using Agentstration.Api.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Extensions;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

public static class ExtensionEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/extensions", ListAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        endpoints.MapGet("/api/extensions/inventory", ListInventoryAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        var registrations = endpoints.MapGroup("/api/extensionregistrations");
        registrations.MapGet("/", ListRegistrationsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        registrations.MapGet("/{registrationName}", GetRegistrationAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        registrations.MapPost("/", CreateRegistrationAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        registrations.MapPut("/{registrationName}", PutRegistrationAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        registrations.MapDelete("/{registrationName}", DeleteRegistrationAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }

    private static async Task<IResult> ListAsync(
        ExtensionManagementService service,
        CancellationToken cancellationToken) =>
        await ExtensionsApiHttp.ExecuteAsync(async () =>
        {
            var views = await service.ListAsync(cancellationToken);
            return Results.Ok(new ValueResponse<ExtensionResponse>(views.Select(Map).ToArray()));
        });

    private static async Task<IResult> ListInventoryAsync(
        ICurrentRequestContext current,
        ExtensionInventoryService service,
        CancellationToken cancellationToken) =>
        await ExtensionsApiHttp.ExecuteAsync(async () =>
        {
            var items = await service.ListAsync(current.Current, cancellationToken);
            return Results.Ok(new ValueResponse<ExtensionInventoryItemResponse>(items.Select(MapInventory).ToArray()));
        });

    private static Task<IResult> ListRegistrationsAsync(
        ExtensionRegistrationManagementService service,
        CancellationToken cancellationToken) =>
        ExtensionsApiHttp.ExecuteAsync(async () => Results.Ok(new ValueResponse<ExtensionRegistrationResource>(
            (await service.ListAsync(cancellationToken)).Select(value => value.Value).ToArray())));

    private static Task<IResult> GetRegistrationAsync(
        string registrationName,
        string? resourceNamespace,
        HttpResponse response,
        ExtensionRegistrationManagementService service,
        CancellationToken cancellationToken) =>
        ExtensionsApiHttp.ExecuteAsync(async () =>
        {
            var stored = await service.GetAsync(ExtensionsApiHttp.Namespace(resourceNamespace), registrationName, cancellationToken)
                ?? throw new ExtensionRegistrationNotFoundException(new(ExtensionsApiHttp.Namespace(resourceNamespace), ExtensionKinds.ExtensionRegistration, registrationName));
            return ExtensionsApiHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });

    private static Task<IResult> CreateRegistrationAsync(
        CreateExtensionRegistrationRequest body,
        HttpResponse response,
        ExtensionRegistrationManagementService service,
        CancellationToken cancellationToken) =>
        ExtensionsApiHttp.ExecuteAsync(async () =>
        {
            var stored = await service.CreateAsync(new ExtensionRegistrationResource
            {
                Metadata = new ResourceMetadata { Name = body.Name, Namespace = ExtensionsApiHttp.Namespace(body.Namespace) },
                Kind = ExtensionKinds.ExtensionRegistration,
                ApiVersion = ResourceApiVersions.CoreV1,
                Definition = body.Properties,
                ScopeRef = body.ScopeRef
            }, cancellationToken);
            response.Headers.Location = $"/api/extensionregistrations/{Uri.EscapeDataString(stored.Value.Name)}?resourceNamespace={Uri.EscapeDataString(stored.Value.Namespace.Value)}";
            return ExtensionsApiHttp.ResourceResult(stored, response, StatusCodes.Status201Created);
        });

    private static Task<IResult> PutRegistrationAsync(
        string registrationName,
        string? resourceNamespace,
        PutExtensionRegistrationRequest body,
        HttpRequest request,
        HttpResponse response,
        ExtensionRegistrationManagementService service,
        CancellationToken cancellationToken) =>
        ExtensionsApiHttp.ExecuteAsync(async () => ExtensionsApiHttp.ResourceResult(
            await service.PutAsync(ExtensionsApiHttp.Namespace(resourceNamespace), registrationName, body.Properties, ExtensionsApiHttp.IfMatch(request), cancellationToken),
            response,
            StatusCodes.Status200OK));

    private static Task<IResult> DeleteRegistrationAsync(
        string registrationName,
        string? resourceNamespace,
        HttpRequest request,
        ExtensionRegistrationManagementService service,
        CancellationToken cancellationToken) =>
        ExtensionsApiHttp.ExecuteAsync(async () =>
        {
            await service.DeleteAsync(ExtensionsApiHttp.Namespace(resourceNamespace), registrationName, ExtensionsApiHttp.IfMatch(request), cancellationToken);
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
        view.DiscoverySource,
        view.RegistrationEnabled,
        view.EnrollmentMode,
        view.RegistrationScopeRef);

    private static ExtensionInventoryItemResponse MapInventory(ExtensionInventoryItem item) => new(
        item.Key,
        item.RegistrationName,
        item.RegistrationNamespace,
        item.RegistrationScopeRef,
        item.EnrollmentInstanceId,
        item.DisplayName,
        item.ExtensionId,
        item.Version,
        item.Endpoint,
        item.RegistrationSource,
        item.RegistrationEnabled,
        item.AvailabilityStatus,
        item.EnrollmentStatus,
        item.AnnouncedAt,
        item.Extension is null ? null : Map(item.Extension),
        item.Connections.Select(value => new ExtensionInventoryConnectionResponse(
            value.RegistrationName,
            value.RegistrationNamespace,
            value.RegistrationScopeRef,
            value.DisplayName,
            value.Endpoint,
            value.Source,
            value.Enabled,
            value.EnrollmentMode,
            value.AvailabilityStatus)).ToArray());
}
