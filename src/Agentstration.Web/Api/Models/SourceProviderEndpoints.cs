using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

public static class SourceProviderEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/sourceproviders");
        group.MapGet("/", ListAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapGet("/{providerName}", GetAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapGet("/{providerName}/status", GetStatusAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapGet("/{providerName}/usages", GetUsagesAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapPost("/", CreateAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapPut("/{providerName}", PutAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapDelete("/{providerName}", DeleteAsync).RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
    }

    private static Task<IResult> ListAsync(SourceProviderManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var providers = await service.ListAsync(cancellationToken);
            var values = await Task.WhenAll(providers.Select(async provider =>
            {
                var status = await service.GetStatusAsync(provider.Value.Namespace, provider.Value.Name, cancellationToken);
                return Map(provider.Value, status);
            }));
            return Results.Ok(new ValueResponse<SourceProviderSummaryResponse>(values));
        });

    private static Task<IResult> GetAsync(
        string providerName,
        string? resourceNamespace,
        HttpResponse response,
        SourceProviderManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.GetAsync(ModelManagementHttp.Namespace(resourceNamespace), providerName, cancellationToken)
                ?? throw new SourceProviderNotFoundException(new(ModelManagementHttp.Namespace(resourceNamespace), ResourceKinds.SourceProvider, providerName));
            return ModelManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });

    private static Task<IResult> GetStatusAsync(
        string providerName,
        string? resourceNamespace,
        SourceProviderManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var status = await service.GetStatusAsync(ModelManagementHttp.Namespace(resourceNamespace), providerName, cancellationToken);
            return Results.Ok(new SourceProviderStatusResponse(status.Provider, status.Status, status.CheckedAt, status.Details));
        });

    private static Task<IResult> GetUsagesAsync(
        string providerName,
        string? resourceNamespace,
        SourceProviderManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var @namespace = ModelManagementHttp.Namespace(resourceNamespace);
            _ = await service.GetAsync(@namespace, providerName, cancellationToken)
                ?? throw new SourceProviderNotFoundException(new(@namespace, ResourceKinds.SourceProvider, providerName));
            var usages = (await service.GetUsagesAsync(@namespace, providerName, cancellationToken))
                .Select(value => new SourceProviderUsageResponse(value.SourceScopeRef, value.Publisher, value.SourceName, value.BindingName))
                .ToArray();
            return Results.Ok(new SourceProviderUsagesResponse(usages, usages.Length));
        });

    private static Task<IResult> CreateAsync(
        CreateSourceProviderRequest body,
        HttpResponse response,
        SourceProviderManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.CreateAsync(new SourceProviderResource
            {
                Metadata = new ResourceMetadata { Name = body.Name, Namespace = ModelManagementHttp.Namespace(body.Namespace) },
                Kind = ResourceKinds.SourceProvider,
                ApiVersion = ManagementApiVersions.CoreV1,
                Definition = body.Properties,
                ScopeRef = ResourceScopeRef.Instance
            }, cancellationToken);
            response.Headers.Location = $"/api/sourceproviders/{Uri.EscapeDataString(stored.Value.Name)}?resourceNamespace={Uri.EscapeDataString(stored.Value.Namespace.Value)}";
            return ModelManagementHttp.ResourceResult(stored, response, StatusCodes.Status201Created);
        });

    private static Task<IResult> PutAsync(
        string providerName,
        string? resourceNamespace,
        PutSourceProviderRequest body,
        HttpRequest request,
        HttpResponse response,
        SourceProviderManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => ModelManagementHttp.ResourceResult(
            await service.PutAsync(ModelManagementHttp.Namespace(resourceNamespace), providerName, body.Properties, ModelManagementHttp.IfMatch(request), cancellationToken),
            response,
            StatusCodes.Status200OK));

    private static Task<IResult> DeleteAsync(
        string providerName,
        string? resourceNamespace,
        HttpRequest request,
        SourceProviderManagementService service,
        CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            await service.DeleteAsync(ModelManagementHttp.Namespace(resourceNamespace), providerName, ModelManagementHttp.IfMatch(request), cancellationToken);
            return Results.NoContent();
        });

    private static SourceProviderSummaryResponse Map(SourceProviderResource provider, SourceProviderStatus status)
    {
        var extension = provider.Definition.Extension.Resolve(provider.Namespace, ResourceKinds.ExtensionRegistration);
        return new(
            provider.Uid.ToString("D"),
            provider.Name,
            provider.Definition.DisplayName,
            extension.Name,
            extension.Namespace.Value,
            provider.Definition.ContributionId,
            status.Status,
            status.Details,
            provider.Namespace.Value,
            provider.ScopeRef);
    }
}
