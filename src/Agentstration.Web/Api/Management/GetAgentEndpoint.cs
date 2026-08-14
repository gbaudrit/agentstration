using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;

namespace Agentstration.Web.Api.Management;

internal sealed class GetAgentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/agents/{name}", HandleAsync);
        group.MapGet("/namespaces/{namespace}/agents/{name}", HandleNamespacedAsync);
    }

    private static Task<IResult> HandleNamespacedAsync(string @namespace, string name, HttpRequest request, HttpResponse response, AgentManagementService service, CancellationToken cancellationToken) =>
        HandleCoreAsync(ResourceNamespace.Parse(@namespace), name, request, response, service, cancellationToken);

    private static Task<IResult> HandleAsync(
        string name,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        HandleCoreAsync(ResourceNamespace.Default, name, request, response, service, cancellationToken);

    private static Task<IResult> HandleCoreAsync(ResourceNamespace @namespace, string name, HttpRequest request, HttpResponse response, AgentManagementService service, CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var stored = await service.GetAgentAsync(@namespace, name, cancellationToken)
                ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Agent, name, @namespace));
            return ManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });
}
