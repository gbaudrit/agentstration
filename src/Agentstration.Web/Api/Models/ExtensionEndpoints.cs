using Agentstration.Management.Contracts;
using Agentstration.Management.Core;

namespace Agentstration.Web.Api.Models;

public static class ExtensionEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/extensions", ListAsync);

    private static async Task<IResult> ListAsync(
        ExtensionManagementService service,
        CancellationToken cancellationToken) =>
        await ModelManagementHttp.ExecuteAsync(async () =>
        {
            var views = await service.ListAsync(cancellationToken);
            return Results.Ok(new ValueResponse<ExtensionResponse>(views.Select(Map).ToArray()));
        });

    private static ExtensionResponse Map(ExtensionView view) => new(
        view.ProviderName,
        view.ProviderNamespace,
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
                version.Deprecated)).ToArray())).ToArray(),
        view.Usages.Select(usage => new ExtensionOptionUsageResponse(
            usage.ProfileName,
            usage.ProfileNamespace,
            usage.OptionSet,
            usage.Version,
            usage.SchemaDigest,
            usage.Status,
            usage.Issues)).ToArray(),
        view.Details,
        view.Configured,
        view.DiscoverySource);
}
