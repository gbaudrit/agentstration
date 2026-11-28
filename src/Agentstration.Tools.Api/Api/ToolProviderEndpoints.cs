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
        providers.MapGet("/{providerName}/administration", ProviderAdministrationAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        providers.MapGet("/{providerName}/tools", ListProviderToolsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);

        var tools = endpoints.MapGroup("/api/tools");
        tools.MapGet("/", ListToolsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        tools.MapGet("/{toolName}/activation", GetActivationAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        tools.MapPut("/administration/{parentUid:guid}/enabled", SetCollectionEnabledAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
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

    private static Task<IResult> GetProviderAsync(string providerName, string? scopeRef, string? resourceNamespace,
        HttpResponse response, ToolManagementService service, ICurrentRequestContext current, IRequestContextScopeFactory scopes,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var id = ToolManagementService.ToolProviderId(providerName);
            var target = scopeRef is null ? (ResourceScopeRef?)null : ResourceScopeRef.Parse(scopeRef);
            using var operationalScope = ToolsApiHttp.EnterOperationalWorkspace(target, current, scopes);
            var stored = await (target is { } exact
                ? service.GetProviderExactAsync(exact, providerName, ResourceNamespace.Parse(resourceNamespace), cancellationToken)
                : service.GetProviderAsync(id, cancellationToken))
                ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolProvider, providerName));
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

    private static Task<IResult> PutProviderAsync(string providerName, string? scopeRef, string? resourceNamespace, PutToolProviderRequest body, HttpRequest request, HttpResponse response, ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var existing = await FindProviderAsync(providerName, scopeRef, resourceNamespace, service, cancellationToken);
            var stored = await service.PutProviderAsync(existing.Value with { Definition = body.Properties }, ToolsApiHttp.IfMatch(request), false, cancellationToken);
            // Discovery retains its best-effort contract, using the same saved coordinates.
            try { _ = await service.RefreshDiscoveryExactAsync(stored.Value.ScopeRef!.Value, stored.Value.Name, stored.Value.Namespace, cancellationToken); } catch (Exception exception) when (exception is not OperationCanceledException) { }
            stored = await service.GetProviderExactAsync(stored.Value.ScopeRef!.Value, stored.Value.Name, stored.Value.Namespace, cancellationToken) ?? stored;
            return ToolsApiHttp.ResourceResult(stored, response, 200);
        });

    private static Task<IResult> TestAsync(string providerName, string? scopeRef, string? resourceNamespace, ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var stored = await FindProviderAsync(providerName, scopeRef, resourceNamespace, service, cancellationToken);
            var result = await service.TestConnectionAsync(stored.Value, cancellationToken);
            return Results.Ok(new ToolConnectionTestResponse("connected", result.Tools.Count, result.Capabilities, result.ServerMetadata));
        });

    private static Task<IResult> RefreshAsync(string providerName, string? scopeRef, string? resourceNamespace, ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var stored = await FindProviderAsync(providerName, scopeRef, resourceNamespace, service, cancellationToken);
            var diff = await service.RefreshDiscoveryExactAsync(stored.Value.ScopeRef!.Value, stored.Value.Name, stored.Value.Namespace, cancellationToken);
            return Results.Ok(new ToolDiscoveryDiffResponse(diff.New, diff.Changed, diff.Unchanged, diff.Unavailable, diff.Total));
        });

    private static async Task<StoredResource<ToolProviderResource>> FindProviderAsync(string name, string? scopeRef, string? resourceNamespace, ToolManagementService service, CancellationToken token) =>
        await (string.IsNullOrWhiteSpace(scopeRef)
            ? service.GetProviderAsync(name, ResourceNamespace.Parse(resourceNamespace), token)
            : service.GetProviderExactAsync(ResourceScopeRef.Parse(scopeRef), name, ResourceNamespace.Parse(resourceNamespace), token))
        ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolProvider, name, ResourceNamespace.Parse(resourceNamespace)));

    private static Task<IResult> ListProviderToolsAsync(string providerName, string? scopeRef, ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var providerId = ToolManagementService.ToolProviderId(providerName);
            var tools = string.IsNullOrWhiteSpace(scopeRef)
                ? await service.ListToolsAsync(cancellationToken)
                : await service.ListVisibleToolsAsync(ResourceScopeRef.Parse(scopeRef), cancellationToken);
            return Results.Ok(new ValueResponse<ToolResource>(tools.Where(value => value.Value.Definition.Provider?.Name == providerId).Select(value => value.Value).ToArray()));
        });

    private static Task<IResult> ListToolsAsync(bool? enabled, bool? available, string? scopeRef, ToolManagementService service,
        ToolDefinitionService definitions, ICurrentRequestContext current, IRequestContextScopeFactory scopes, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var stored = string.IsNullOrWhiteSpace(scopeRef)
                ? await service.ListToolsAsync(cancellationToken)
                : await service.ListVisibleToolsAsync(ResourceScopeRef.Parse(scopeRef), cancellationToken);
            var values = stored.Select(value => value.Value);
            if (string.IsNullOrWhiteSpace(scopeRef))
            {
                var operational = await ToolsApiHttp.ListOperationalAcquisitionToolsAsync(definitions, current, scopes, cancellationToken);
                if (operational.Length > 0)
                {
                    var workspace = operational[0].ScopeRef!.Value;
                    using var access = ToolsApiHttp.EnterOperationalWorkspace(workspace, current, scopes);
                    var names = operational.Select(value => (value.Namespace, AgentstrationToolProvider.ToolResourceName(value.Name))).ToHashSet();
                    values = values.Concat((await service.ListToolsAsync(cancellationToken)).Select(value => value.Value)
                        .Where(value => value.ScopeRef == workspace && names.Contains((value.Namespace, value.Name)))).DistinctBy(value => (value.ScopeRef, value.Address));
                }
            }
            if (enabled.HasValue)
            {
                var matching = new List<ToolResource>();
                foreach (var tool in values)
                {
                    using var stateScope = ToolsApiHttp.EnterOperationalWorkspace(tool.ScopeRef, current, scopes);
                    var state = await service.GetToolOperationalStateAsync(tool, cancellationToken);
                    if ((state?.Enabled ?? tool.Definition.Enabled) == enabled.Value) matching.Add(tool);
                }
                values = matching;
            }
            if (available.HasValue) values = values.Where(value => value.Definition.Discovery?.Available == available.Value);
            return Results.Ok(new ValueResponse<ToolResource>(values.ToArray()));
        });

    private static Task<IResult> GetToolAsync(string toolName, string? @namespace, string? scopeRef,
        HttpResponse response, ToolManagementService service, ICurrentRequestContext current, IRequestContextScopeFactory scopes,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var id = ToolManagementService.ToolId(toolName);
            var ns = ResourceNamespace.Parse(@namespace);
            var target = string.IsNullOrWhiteSpace(scopeRef) ? (ResourceScopeRef?)null : ResourceScopeRef.Parse(scopeRef);
            using var operationalScope = ToolsApiHttp.EnterOperationalWorkspace(target, current, scopes);
            var stored = target is { } exact
                ? await service.GetToolExactAsync(exact, id, ns, cancellationToken)
                : await service.GetToolAsync(id, ns, cancellationToken);
            if (stored is null) throw new ResourceNotFoundException(new(ToolResourceKinds.Tool, toolName, ns));
            return ToolsApiHttp.ResourceResult(stored, response, 200);
        });

    private static Task<IResult> ProviderAdministrationAsync(string providerName, string? scopeRef, string? resourceNamespace,
        ToolManagementService service, ToolAdministrationService administration, CancellationToken token) =>
        ToolsApiHttp.ExecuteAsync(async () => Results.Ok(await administration.GetProviderAsync(
            (await FindProviderAsync(providerName, scopeRef, resourceNamespace, service, token)).Value, token)));

    private static Task<IResult> GetActivationAsync(string toolName, string? scopeRef, string? @namespace,
        ToolManagementService service, ToolAdministrationService administration, ICurrentRequestContext current, IRequestContextScopeFactory scopes, CancellationToken token) => ToolsApiHttp.ExecuteAsync(async () =>
    {
        using var operationalScope = ToolsApiHttp.EnterOperationalWorkspace(string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef), current, scopes);
        var tool = string.IsNullOrWhiteSpace(scopeRef) ? await service.GetToolAsync(toolName, ResourceNamespace.Parse(@namespace), token)
            : await service.GetToolExactAsync(ResourceScopeRef.Parse(scopeRef), toolName, ResourceNamespace.Parse(@namespace), token);
        if (tool is null) throw new ResourceNotFoundException(new(ToolResourceKinds.Tool, toolName));
        return Results.Ok(await administration.GetAsync(tool.Value, token));
    });

    private static Task<IResult> SetCollectionEnabledAsync(Guid parentUid, SetToolCollectionEnabledRequest body,
        ToolAdministrationService administration, CancellationToken token) => ToolsApiHttp.ExecuteAsync(async () =>
        Results.Ok(await administration.SetCollectionEnabledAsync(parentUid, body.MembershipToken, body.Enabled, body.StateETags, body.ToolUids, token)));

    private static Task<IResult> SetEnabledAsync(string toolName, string? @namespace, string? scopeRef, SetToolEnabledRequest body,
        HttpRequest request, HttpResponse response, ToolManagementService service, CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () => ToolsApiHttp.ResourceResult(
            await service.SetToolEnabledExactAsync(string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef),
                ToolManagementService.ToolId(toolName), ResourceNamespace.Parse(@namespace), body.Enabled, ToolsApiHttp.IfMatch(request), cancellationToken), response, 200));

    private static Task<IResult> RunToolAsync(
        string toolName,
        string? @namespace,
        string? scopeRef,
        RunToolRequest body,
        ToolRunnerService runner,
        ICurrentRequestContext context,
        CancellationToken cancellationToken) =>
        ToolsApiHttp.ExecuteAsync(async () =>
        {
            var target = string.IsNullOrWhiteSpace(scopeRef) ? (ResourceScopeRef?)null : ResourceScopeRef.Parse(scopeRef);
            return Results.Ok(await runner.RunAsync(ResourceNamespace.Parse(@namespace), toolName, body,
                context.Current, target, cancellationToken));
        });

    private static ToolProviderResource Resource(string name, ToolProviderProperties properties) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ToolResourceKinds.ToolProvider,
        Metadata = new ResourceMetadata { Name = name },
        Definition = properties
    };
}
