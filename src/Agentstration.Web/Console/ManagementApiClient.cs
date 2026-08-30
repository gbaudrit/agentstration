using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components.Models;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Console;

public sealed class ManagementApiClient(HttpClient httpClient) : IManagementApiClient, IAgentRunnerManagementClient
{
    public async Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken)
    {
        var path = "api/agents?allNamespaces=true&top=1000";
        var page = await ApiResponse.ReadAsync<PagedResponse<AgentResource>>(httpClient, path, cancellationToken);
        return page.Value.Select(agent =>
        {
            var modelProfile = agent.Definition.ModelProfile.Resolve(agent.Namespace, ResourceKinds.ModelProfile);
            return new AgentSummary(agent.Metadata.Name, agent.Definition.DisplayName, agent.Definition.Handler, agent.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), agent.Status.ProvisioningState.ToString(), agent.Definition.Tools.Select(tool => tool.Name).ToArray(), "Not deployed", DateTimeOffset.MinValue, modelProfile.Name)
            {
                Namespace = agent.Namespace,
                ModelProfileNamespace = modelProfile.Namespace
            };
        }).ToArray();
    }

    public async Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsAsync(CancellationToken cancellationToken)
    {
        var page = await ApiResponse.ReadAsync<PagedResponse<AgentDeployment>>(httpClient, "api/deployments?top=1000", cancellationToken);
        return page.Value.Select(deployment => new DeploymentSummary(
            deployment.Metadata.Name,
            deployment.AgentName ?? "—",
            deployment.Namespace.Value,
            deployment.OperationalState.ToString(),
            deployment.DesiredState.ToString(),
            deployment.HostingMode.ToString(),
            deployment.Environment,
            deployment.RuntimeProfileName,
            deployment.RevisionName,
            deployment.ObservedRevisionName,
            deployment.UpdatedAt,
            deployment.LastError)).ToArray();
    }

    public async Task<IReadOnlyList<TriggerResource>> GetTriggersAsync(CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<TriggerResource[]>(httpClient, "api/triggers", cancellationToken);

    public async Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string name, CancellationToken cancellationToken)
        => await GetAgentAsync(ResourceNamespace.Default, name, cancellationToken);

    public async Task<ResourceSnapshot<AgentResource>> GetAgentAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        var path = AgentPath(@namespace, name);
        using var response = await httpClient.GetAsync(path, cancellationToken);
        return await ReadResourceAsync<AgentResource>(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<AgentResource>> PutAgentAsync(AgentResourceRequest request, string? etag, bool createOnly, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, AgentPath(request.Metadata.Name))
        {
            Content = JsonContent.Create(request)
        };
        if (createOnly) message.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        else if (!string.IsNullOrWhiteSpace(etag)) message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadResourceAsync<AgentResource>(response, cancellationToken);
    }

    public async Task DeleteAgentAsync(string name, string etag, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, AgentPath(name));
        message.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<ManagementSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var agentsTask = GetAgentsAsync(cancellationToken);
        var agents = await agentsTask;
        return new(0, agents.Count, agents.Sum(item => int.TryParse(item.Version, out var version) ? version : 0), 0, "Managed");
    }

    private static string AgentPath(string name) => AgentPath(ResourceNamespace.Default, name);

    private static string AgentPath(ResourceNamespace @namespace, string name) => @namespace.IsDefault
        ? $"api/agents/{Uri.EscapeDataString(name)}"
        : $"api/namespaces/{Uri.EscapeDataString(@namespace.Value)}/agents/{Uri.EscapeDataString(name)}";

    private static async Task<ResourceSnapshot<T>> ReadResourceAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty response.", Guid.NewGuid().ToString("N"));
        var etag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(etag))
            throw new AgentstrationApiException("Agentstration API did not return the resource ETag.", Guid.NewGuid().ToString("N"));
        return new ResourceSnapshot<T>(value, etag);
    }
}

