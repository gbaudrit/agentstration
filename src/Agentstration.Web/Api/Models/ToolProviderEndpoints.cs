using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;

namespace Agentstration.Web.Api.Models;

internal static class ToolProviderEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var providers = endpoints.MapGroup("/api/toolproviders");
        providers.MapGet("/", ListProvidersAsync);
        providers.MapGet("/{providerName}", GetProviderAsync);
        providers.MapPost("/", CreateProviderAsync);
        providers.MapPut("/{providerName}", PutProviderAsync);
        providers.MapPost("/{providerName}/test", TestAsync);
        providers.MapPost("/{providerName}/refresh", RefreshAsync);
        providers.MapGet("/{providerName}/tools", ListProviderToolsAsync);

        var tools = endpoints.MapGroup("/api/tools");
        tools.MapGet("/", ListToolsAsync);
        tools.MapGet("/{toolName}", GetToolAsync);
        tools.MapPut("/{toolName}/enabled", SetEnabledAsync);
    }

    private static Task<IResult> ListProvidersAsync(ToolManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => Results.Ok(new ValueResponse<ToolProviderResource>((await service.ListProvidersAsync(cancellationToken)).Select(value => value.Value).ToArray())));

    private static Task<IResult> GetProviderAsync(string providerName, HttpResponse response, ToolManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var id = ToolManagementService.ToolProviderId(providerName);
            var stored = await service.GetProviderAsync(id, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ToolProvider, providerName));
            return ModelManagementHttp.ResourceResult(stored, response, 200);
        });

    private static Task<IResult> CreateProviderAsync(CreateToolProviderRequest body, HttpResponse response, ToolManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var stored = await service.PutProviderAsync(Resource(body.Name, body.Properties), null, true, cancellationToken);
            try { _ = await service.RefreshDiscoveryAsync(stored.Value.Id, cancellationToken); } catch (Exception exception) when (exception is not OperationCanceledException) { }
            stored = await service.GetProviderAsync(stored.Value.Id, cancellationToken) ?? stored;
            response.Headers.Location = $"/api/toolproviders/{Uri.EscapeDataString(body.Name)}";
            return ModelManagementHttp.ResourceResult(stored, response, 201);
        });

    private static Task<IResult> PutProviderAsync(string providerName, PutToolProviderRequest body, HttpRequest request, HttpResponse response, ToolManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var existing = await service.GetProviderAsync(ToolManagementService.ToolProviderId(providerName), cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ToolProvider, providerName));
            var stored = await service.PutProviderAsync(existing.Value with { Properties = body.Properties }, ModelManagementHttp.IfMatch(request), false, cancellationToken);
            try { _ = await service.RefreshDiscoveryAsync(stored.Value.Id, cancellationToken); } catch (Exception exception) when (exception is not OperationCanceledException) { }
            stored = await service.GetProviderAsync(stored.Value.Id, cancellationToken) ?? stored;
            return ModelManagementHttp.ResourceResult(stored, response, 200);
        });

    private static Task<IResult> TestAsync(string providerName, ToolManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var id = ToolManagementService.ToolProviderId(providerName);
            var stored = await service.GetProviderAsync(id, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ToolProvider, providerName));
            var result = await service.TestConnectionAsync(stored.Value, cancellationToken);
            return Results.Ok(new ToolConnectionTestResponse("connected", result.Tools.Count, result.Capabilities, result.ServerMetadata));
        });

    private static Task<IResult> RefreshAsync(string providerName, ToolManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var diff = await service.RefreshDiscoveryAsync(ToolManagementService.ToolProviderId(providerName), cancellationToken);
            return Results.Ok(new ToolDiscoveryDiffResponse(diff.New, diff.Changed, diff.Unchanged, diff.Unavailable, diff.Total));
        });

    private static Task<IResult> ListProviderToolsAsync(string providerName, ToolManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var providerId = ToolManagementService.ToolProviderId(providerName);
            var tools = await service.ListToolsAsync(cancellationToken);
            return Results.Ok(new ValueResponse<ToolResource>(tools.Where(value => value.Value.Properties.Provider?.ResourceId == providerId).Select(value => value.Value).ToArray()));
        });

    private static Task<IResult> ListToolsAsync(bool? enabled, bool? available, ToolManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var values = (await service.ListToolsAsync(cancellationToken)).Select(value => value.Value);
            if (enabled.HasValue) values = values.Where(value => value.Properties.Enabled == enabled.Value);
            if (available.HasValue) values = values.Where(value => value.Properties.Discovery?.Available == available.Value);
            return Results.Ok(new ValueResponse<ToolResource>(values.ToArray()));
        });

    private static Task<IResult> GetToolAsync(string toolName, HttpResponse response, ToolManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () =>
        {
            var id = ToolManagementService.ToolId(toolName);
            var stored = await service.GetToolAsync(id, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Tool, toolName));
            return ModelManagementHttp.ResourceResult(stored, response, 200);
        });

    private static Task<IResult> SetEnabledAsync(string toolName, SetToolEnabledRequest body, HttpRequest request, HttpResponse response, ToolManagementService service, CancellationToken cancellationToken) =>
        ModelManagementHttp.ExecuteAsync(async () => ModelManagementHttp.ResourceResult(
            await service.SetToolEnabledAsync(ToolManagementService.ToolId(toolName), body.Enabled, ModelManagementHttp.IfMatch(request), cancellationToken), response, 200));

    private static ToolProviderResource Resource(string name, ToolProviderProperties properties) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.ToolProvider,
        Metadata = new ResourceMetadata { Name = name },
        Definition = properties
    };
}
