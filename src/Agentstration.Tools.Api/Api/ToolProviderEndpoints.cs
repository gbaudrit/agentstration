using Agentstration.Api.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Tools.Mcp;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

internal static class ToolProviderEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var providers = endpoints.MapGroup("/api/toolproviders");
        providers.MapGet("/", ListProvidersAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        providers.MapGet("/{providerName}", GetProviderAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        providers.MapPost("/", CreateProviderAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        providers.MapPut("/{providerName}", PutProviderAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        providers.MapPost("/{providerName}/test", TestAsync).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        providers.MapPost("/{providerName}/refresh", RefreshAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        providers.MapGet("/{providerName}/tools", ListProviderToolsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);

        var tools = endpoints.MapGroup("/api/tools");
        tools.MapGet("/", ListToolsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        tools.MapGet("/{toolName}", GetToolAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        tools.MapPut("/{toolName}/enabled", SetEnabledAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        tools.MapPost("/{toolName}/run", RunToolAsync)
            .Produces<RunToolResponse>()
            .WithSummary("Simulate or execute a governed Tool")
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
    }

    private static Task<IResult> ListProvidersAsync(ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            return Results.Ok(new ValueResponse<ToolProviderResource>((await service.ListProvidersAsync(cancellationToken)).Select(value => value.Value).ToArray()));
        });

    private static Task<IResult> GetProviderAsync(string providerName, HttpResponse response, ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var id = ToolManagementService.ToolProviderId(providerName);
            var stored = await service.GetProviderAsync(id, cancellationToken) ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolProvider, providerName));
            return ToolsApiHttp.ResourceResult(stored, response, 200);
        });

    private static Task<IResult> CreateProviderAsync(CreateToolProviderRequest body, HttpResponse response, ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var stored = await service.PutProviderAsync(Resource(body.Name, body.Properties) with { ScopeRef = body.ScopeRef }, null, true, cancellationToken);
            try { _ = await service.RefreshDiscoveryAsync(stored.Value.Metadata.Name, cancellationToken); } catch (Exception exception) when (exception is not OperationCanceledException) { }
            stored = await service.GetProviderAsync(stored.Value.Metadata.Name, cancellationToken) ?? stored;
            response.Headers.Location = $"/api/toolproviders/{Uri.EscapeDataString(body.Name)}";
            return ToolsApiHttp.ResourceResult(stored, response, 201);
        });

    private static Task<IResult> PutProviderAsync(string providerName, PutToolProviderRequest body, HttpRequest request, HttpResponse response, ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var existing = await service.GetProviderAsync(ToolManagementService.ToolProviderId(providerName), cancellationToken)
                ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolProvider, providerName));
            var stored = await service.PutProviderAsync(existing.Value with { Definition = body.Properties }, ToolsApiHttp.IfMatch(request), false, cancellationToken);
            try { _ = await service.RefreshDiscoveryAsync(stored.Value.Metadata.Name, cancellationToken); } catch (Exception exception) when (exception is not OperationCanceledException) { }
            stored = await service.GetProviderAsync(stored.Value.Metadata.Name, cancellationToken) ?? stored;
            return ToolsApiHttp.ResourceResult(stored, response, 200);
        });

    private static Task<IResult> TestAsync(string providerName, ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var id = ToolManagementService.ToolProviderId(providerName);
            var stored = await service.GetProviderAsync(id, cancellationToken) ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolProvider, providerName));
            var result = await service.TestConnectionAsync(stored.Value, cancellationToken);
            return Results.Ok(new ToolConnectionTestResponse("connected", result.Tools.Count, result.Capabilities, result.ServerMetadata));
        });

    private static Task<IResult> RefreshAsync(string providerName, ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var diff = await service.RefreshDiscoveryAsync(ToolManagementService.ToolProviderId(providerName), cancellationToken);
            return Results.Ok(new ToolDiscoveryDiffResponse(diff.New, diff.Changed, diff.Unchanged, diff.Unavailable, diff.Total));
        });

    private static Task<IResult> ListProviderToolsAsync(string providerName, ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var providerId = ToolManagementService.ToolProviderId(providerName);
            var tools = await service.ListToolsAsync(cancellationToken);
            return Results.Ok(new ValueResponse<ToolResource>(tools.Where(value => value.Value.Definition.Provider?.Name == providerId).Select(value => value.Value).ToArray()));
        });

    private static Task<IResult> ListToolsAsync(bool? enabled, bool? available, ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var values = (await service.ListToolsAsync(cancellationToken)).Select(value => value.Value);
            if (enabled.HasValue) values = values.Where(value => value.Definition.Enabled == enabled.Value);
            if (available.HasValue) values = values.Where(value => value.Definition.Discovery?.Available == available.Value);
            return Results.Ok(new ValueResponse<ToolResource>(values.ToArray()));
        });

    private static Task<IResult> GetToolAsync(string toolName, string? @namespace, HttpResponse response, ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var id = ToolManagementService.ToolId(toolName);
            var ns = ResourceNamespace.Parse(@namespace);
            var stored = await service.GetToolAsync(id, ns, cancellationToken) ?? throw new ResourceNotFoundException(new(ToolResourceKinds.Tool, toolName, ns));
            return ToolsApiHttp.ResourceResult(stored, response, 200);
        });

    private static Task<IResult> SetEnabledAsync(string toolName, string? @namespace, SetToolEnabledRequest body, HttpRequest request, HttpResponse response, ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () => ToolsApiHttp.ResourceResult(
            await service.SetToolEnabledAsync(ToolManagementService.ToolId(toolName), ResourceNamespace.Parse(@namespace), body.Enabled, ToolsApiHttp.IfMatch(request), cancellationToken), response, 200));

    private static Task<IResult> RunToolAsync(
        string toolName,
        string? @namespace,
        RunToolRequest body,
        ToolRunnerService runner,
        ICurrentRequestContext context,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () => Results.Ok(await runner.RunAsync(
            ResourceNamespace.Parse(@namespace),
            toolName,
            body,
            context.Current,
            cancellationToken)));

    private static ToolProviderResource Resource(string name, ToolProviderProperties properties) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ToolResourceKinds.ToolProvider,
        Metadata = new ResourceMetadata { Name = name },
        Definition = properties
    };
}
