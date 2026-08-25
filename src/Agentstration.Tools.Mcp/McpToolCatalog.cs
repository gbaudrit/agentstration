using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Agentstration.Tools.Mcp;

public interface IAepExtensionEndpointResolver { Uri Resolve(string extensionId); }

public sealed class ConfigurationAepExtensionEndpointResolver(IConfiguration configuration) : IAepExtensionEndpointResolver
{
    public Uri Resolve(string extensionId)
    {
        var value = configuration[$"Agentstration:Extensions:{extensionId}:Endpoint"];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
            throw new ToolResolutionException("extension_unavailable", $"AEP extension '{extensionId}' has no valid HTTP(S) endpoint configuration.");
        return endpoint;
    }
}

public interface IToolProviderEnvironmentResolver
{
    IReadOnlyDictionary<string, string?> Resolve(IReadOnlyDictionary<string, string> references);
}

public sealed class ConfigurationToolProviderEnvironmentResolver(IConfiguration configuration) : IToolProviderEnvironmentResolver
{
    public IReadOnlyDictionary<string, string?> Resolve(IReadOnlyDictionary<string, string> references)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in references)
            values[pair.Key] = configuration[pair.Value] ?? throw new ToolResolutionException("secret_reference_unresolved", $"Configuration reference '{pair.Value}' for environment variable '{pair.Key}' is unavailable.");
        return values;
    }
}

public sealed class ToolProviderAdapter(
    IAepExtensionEndpointResolver extensionEndpoints,
    IToolProviderEnvironmentResolver environments,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IToolProviderDiscovery
{
    public bool Supports(ToolProviderType providerType) => providerType is ToolProviderType.Aep or ToolProviderType.Mcp;

    public async Task<ToolProviderDiscoveryResult> DiscoverAsync(ToolProviderResource provider, CancellationToken cancellationToken)
    {
        if (provider.Definition.ProviderType == ToolProviderType.Mcp)
        {
            await using var client = await ConnectMcpAsync(provider, cancellationToken);
            return Result(await client.ListToolsAsync(cancellationToken: cancellationToken), client);
        }

        var (descriptor, extensionEndpoint) = await DiscoverAepAsync(provider, cancellationToken);
        var discovered = new List<DiscoveredToolDescriptor>();
        IReadOnlyDictionary<string, bool> capabilities = new Dictionary<string, bool>();
        IReadOnlyDictionary<string, string> serverMetadata = new Dictionary<string, string>();
        foreach (var server in descriptor.Mcp?.Servers ?? [])
        {
            await using var client = await ConnectHttpAsync(AepDescriptorValidator.ResolveMcpEndpoint(extensionEndpoint, server), server.Id, cancellationToken);
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            capabilities = Capabilities(client);
            serverMetadata = ServerMetadata(client);
            foreach (var contribution in (descriptor.Contributions.Tools ?? []).Where(value => value.Mcp.Server == server.Id))
            {
                var native = tools.FirstOrDefault(value => value.ProtocolTool.Name == contribution.Mcp.Tool)
                    ?? throw new ToolResolutionException("mcp_tool_not_found", $"AEP contribution '{contribution.Id}' maps to missing MCP tool '{contribution.Mcp.Tool}'.");
                discovered.Add(ToDescriptor(contribution.Id, contribution.DisplayName, contribution.Description ?? native.Description, native, contribution.Metadata));
            }
        }
        return new ToolProviderDiscoveryResult(discovered, capabilities, serverMetadata);
    }

    public async Task<IReadOnlyCollection<IAgentTool>> ResolveAsync(ToolProviderResource provider, IReadOnlyCollection<ToolResource> tools, CancellationToken cancellationToken)
    {
        if (provider.Definition.ProviderType == ToolProviderType.Mcp)
        {
            await using var client = await ConnectMcpAsync(provider, cancellationToken);
            var native = await client.ListToolsAsync(cancellationToken: cancellationToken);
            return tools.Select(tool => Wrap(tool, native.FirstOrDefault(value => value.ProtocolTool.Name == tool.Definition.ExternalId)
                ?? throw new ToolResolutionException("mcp_tool_not_found", $"Provider '{provider.Metadata.Name}' no longer exposes tool '{tool.Definition.ExternalId}'."))).ToArray();
        }

        var (descriptor, extensionEndpoint) = await DiscoverAepAsync(provider, cancellationToken);
        var result = new List<IAgentTool>();
        foreach (var group in tools.GroupBy(tool => (descriptor.Contributions.Tools ?? []).First(value => value.Id == tool.Definition.ExternalId).Mcp.Server, StringComparer.Ordinal))
        {
            var server = descriptor.Mcp!.Servers.First(value => value.Id == group.Key);
            await using var client = await ConnectHttpAsync(AepDescriptorValidator.ResolveMcpEndpoint(extensionEndpoint, server), server.Id, cancellationToken);
            var native = await client.ListToolsAsync(cancellationToken: cancellationToken);
            foreach (var tool in group)
            {
                var mapping = descriptor.Contributions.Tools!.First(value => value.Id == tool.Definition.ExternalId);
                result.Add(Wrap(tool, native.FirstOrDefault(value => value.ProtocolTool.Name == mapping.Mcp.Tool)
                    ?? throw new ToolResolutionException("mcp_tool_not_found", $"AEP contribution '{mapping.Id}' maps to missing MCP tool '{mapping.Mcp.Tool}'.")));
            }
        }
        return result;
    }

    private async Task<(AepManifest Descriptor, Uri Endpoint)> DiscoverAepAsync(ToolProviderResource provider, CancellationToken cancellationToken)
    {
        var extensionId = provider.Definition.Aep!.ExtensionId;
        var endpoint = extensionEndpoints.Resolve(extensionId);
        var http = httpClientFactory.CreateClient(McpToolServiceCollectionExtensions.AepClientName);
        http.BaseAddress = endpoint;
        var descriptor = await new AepClient(http).DiscoverAsync(cancellationToken);
        var errors = AepDescriptorValidator.Validate(descriptor);
        if (errors.Count > 0) throw new ToolResolutionException("aep_descriptor_invalid", string.Join(" ", errors));
        if (descriptor.Extension.Id != extensionId) throw new ToolResolutionException("extension_identity_mismatch", $"Expected extension '{extensionId}' but discovered '{descriptor.Extension.Id}'.");
        return (descriptor, endpoint);
    }

    private Task<McpClient> ConnectMcpAsync(ToolProviderResource provider, CancellationToken cancellationToken)
    {
        var mcp = provider.Definition.Mcp!;
        if (mcp.Transport == McpToolProviderTransport.StreamableHttp)
            return ConnectHttpAsync(mcp.Endpoint!, provider.Metadata.Name, cancellationToken);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = provider.Metadata.Name,
            Command = mcp.Command!,
            Arguments = [.. mcp.Arguments],
            WorkingDirectory = mcp.WorkingDirectory,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = environments.Resolve(mcp.EnvironmentReferences).ToDictionary()
        }, loggerFactory);
        return McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: cancellationToken);
    }

    private Task<McpClient> ConnectHttpAsync(Uri endpoint, string name, CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = endpoint, Name = name }, httpClientFactory.CreateClient(McpToolServiceCollectionExtensions.McpClientName), loggerFactory, ownsHttpClient: false);
        return McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: cancellationToken);
    }

    private static ToolProviderDiscoveryResult Result(IList<McpClientTool> tools, McpClient client) =>
        new(tools.Select(tool => ToDescriptor(tool.ProtocolTool.Name, tool.Title ?? tool.Name, tool.Description, tool, null)).ToArray(), Capabilities(client), ServerMetadata(client));

    private static DiscoveredToolDescriptor ToDescriptor(string id, string displayName, string? description, McpClientTool tool, IReadOnlyDictionary<string, JsonElement>? metadata) =>
        new(id, displayName, description, tool.JsonSchema.Clone(), tool.ReturnJsonSchema?.Clone(), metadata ?? new Dictionary<string, JsonElement>());

    private static IReadOnlyDictionary<string, bool> Capabilities(McpClient client) => new Dictionary<string, bool>
    {
        ["tools"] = client.ServerCapabilities.Tools is not null,
        ["resources"] = client.ServerCapabilities.Resources is not null,
        ["prompts"] = client.ServerCapabilities.Prompts is not null
    };

    private static IReadOnlyDictionary<string, string> ServerMetadata(McpClient client) => new Dictionary<string, string>
    {
        ["name"] = client.ServerInfo.Name,
        ["version"] = client.ServerInfo.Version
    };

    private static IAgentTool Wrap(ToolResource resource, McpClientTool native)
    {
        if (!string.IsNullOrWhiteSpace(resource.Definition.Description)) native = native.WithDescription(resource.Definition.Description);
        return new McpAgentTool(
            resource.Metadata.Name,
            native.Name,
            resource.Definition.Description ?? native.Description,
            resource.Definition.Provider?.Name,
            resource.Definition.ExternalId,
            native.JsonSchema.Clone(),
            native.ReturnJsonSchema?.Clone(),
            resource.Definition.RequiresApproval);
    }

    public async ValueTask<JsonElement?> InvokeAsync(
        ToolProviderResource provider,
        ToolResource tool,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpClient client;
        string externalId;
        if (provider.Definition.ProviderType == ToolProviderType.Mcp)
        {
            client = await ConnectMcpAsync(provider, cancellationToken);
            externalId = tool.Definition.ExternalId
                ?? throw new ToolResolutionException("tool_mapping_invalid", $"Tool resource '{tool.Metadata.Name}' has no external Tool identity.");
        }
        else
        {
            var (descriptor, extensionEndpoint) = await DiscoverAepAsync(provider, cancellationToken);
            var mapping = (descriptor.Contributions.Tools ?? []).FirstOrDefault(value => value.Id == tool.Definition.ExternalId)
                ?? throw new ToolResolutionException("aep_tool_not_found", $"AEP contribution '{tool.Definition.ExternalId}' was not found.");
            var server = descriptor.Mcp?.Servers.FirstOrDefault(value => value.Id == mapping.Mcp.Server)
                ?? throw new ToolResolutionException("aep_mcp_server_not_found", $"AEP MCP server '{mapping.Mcp.Server}' was not found.");
            client = await ConnectHttpAsync(AepDescriptorValidator.ResolveMcpEndpoint(extensionEndpoint, server), server.Id, cancellationToken);
            externalId = mapping.Mcp.Tool;
        }

        await using (client)
        {
            var native = (await client.ListToolsAsync(cancellationToken: cancellationToken))
                .FirstOrDefault(value => value.ProtocolTool.Name == externalId)
                ?? throw new ToolResolutionException("mcp_tool_not_found", $"Provider '{provider.Metadata.Name}' no longer exposes tool '{externalId}'.");
            var values = arguments is { ValueKind: JsonValueKind.Object }
                ? arguments.Value.EnumerateObject().ToDictionary(value => value.Name, value => (object?)value.Value.Clone(), StringComparer.Ordinal)
                : new Dictionary<string, object?>();
            return JsonSerializer.SerializeToElement(await native.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(values), cancellationToken));
        }
    }
}

public sealed class McpToolCatalog(IControlPlaneStore store, ToolProviderAdapter providers) : IToolCatalog
{
    public async ValueTask<IReadOnlyCollection<IAgentTool>> ResolveAsync(IEnumerable<string> toolIds, CancellationToken cancellationToken = default)
    {
        var resources = new List<ToolResource>();
        foreach (var id in toolIds.Distinct(StringComparer.Ordinal))
        {
            var tool = await store.GetAsync<ToolResource>(new ResourceKey(ResourceKinds.Tool, id), cancellationToken) ?? throw new ToolResolutionException("tool_not_found", $"Tool resource '{id}' was not found.");
            if (!tool.Value.Definition.Enabled) throw new ToolResolutionException("tool_disabled", $"Tool resource '{id}' is disabled.");
            if (tool.Value.Definition.Discovery?.Available != true) throw new ToolResolutionException("tool_unavailable", $"Tool resource '{id}' is no longer available from its provider.");
            if (tool.Value.Definition.Provider is null) throw new ToolResolutionException("tool_mapping_invalid", $"Tool resource '{id}' has no ToolProvider mapping.");
            resources.Add(tool.Value);
        }

        var resolved = new List<IAgentTool>();
        foreach (var group in resources.GroupBy(value => value.Definition.Provider!.Name, StringComparer.Ordinal))
        {
            var provider = await store.GetAsync<ToolProviderResource>(new ResourceKey(ResourceKinds.ToolProvider, group.Key), cancellationToken) ?? throw new ToolResolutionException("tool_provider_not_found", $"ToolProvider '{group.Key}' was not found.");
            if (!provider.Value.Definition.Enabled) throw new ToolResolutionException("tool_provider_disabled", $"ToolProvider '{provider.Value.Metadata.Name}' is disabled.");
            resolved.AddRange(await providers.ResolveAsync(provider.Value, group.ToArray(), cancellationToken));
        }
        return resolved;
    }
}

internal sealed record McpAgentTool(
    string Id,
    string Name,
    string? Description,
    string? ProviderId,
    string? ExternalId,
    JsonElement InputSchema,
    JsonElement? OutputSchema,
    bool RequiresApproval) : IAgentTool;

public sealed class McpToolInvoker(IControlPlaneStore store, ToolProviderAdapter providers) : IToolInvoker
{
    public async ValueTask<JsonElement?> InvokeAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        var tool = await store.GetAsync<ToolResource>(new ResourceKey(ResourceKinds.Tool, context.ToolId), cancellationToken)
            ?? throw new ToolResolutionException("tool_not_found", $"Tool resource '{context.ToolId}' was not found.");
        if (!tool.Value.Definition.Enabled) throw new ToolResolutionException("tool_disabled", $"Tool resource '{context.ToolId}' is disabled.");
        if (tool.Value.Definition.Discovery?.Available != true) throw new ToolResolutionException("tool_unavailable", $"Tool resource '{context.ToolId}' is no longer available from its provider.");
        var providerId = tool.Value.Definition.Provider?.Name
            ?? throw new ToolResolutionException("tool_mapping_invalid", $"Tool resource '{context.ToolId}' has no ToolProvider mapping.");
        if (context.ToolProviderId is not null && !string.Equals(context.ToolProviderId, providerId, StringComparison.Ordinal))
            throw new ToolResolutionException("tool_provider_mismatch", $"Tool resource '{context.ToolId}' no longer maps to provider '{context.ToolProviderId}'.");
        if (context.ExternalToolId is not null && !string.Equals(context.ExternalToolId, tool.Value.Definition.ExternalId, StringComparison.Ordinal))
            throw new ToolResolutionException("external_tool_mismatch", $"Tool resource '{context.ToolId}' no longer maps to external Tool '{context.ExternalToolId}'.");
        var provider = await store.GetAsync<ToolProviderResource>(new ResourceKey(ResourceKinds.ToolProvider, providerId), cancellationToken)
            ?? throw new ToolResolutionException("tool_provider_not_found", $"ToolProvider '{providerId}' was not found.");
        if (!provider.Value.Definition.Enabled) throw new ToolResolutionException("tool_provider_disabled", $"ToolProvider '{providerId}' is disabled.");
        return await providers.InvokeAsync(provider.Value, tool.Value, context.Arguments, cancellationToken);
    }
}

public sealed class ToolResolutionException(string code, string message, Exception? innerException = null) : Exception(message, innerException) { public string Code { get; } = code; }

public static class McpToolServiceCollectionExtensions
{
    internal const string AepClientName = "agentstration-aep-tools";
    internal const string McpClientName = "agentstration-mcp-tools";
    public static IServiceCollection AddAgentstrationMcpTools(this IServiceCollection services)
    {
        services.TryAddSingleton<IConfiguration>(_ => new ConfigurationBuilder().Build());
        services.AddHttpClient(AepClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient(McpClientName, client => client.Timeout = TimeSpan.FromSeconds(90));
        services.AddSingleton<IAepExtensionEndpointResolver, ConfigurationAepExtensionEndpointResolver>();
        services.AddSingleton<IToolProviderEnvironmentResolver, ConfigurationToolProviderEnvironmentResolver>();
        services.AddSingleton<ToolProviderAdapter>();
        services.AddSingleton<IToolProviderDiscovery>(provider => provider.GetRequiredService<ToolProviderAdapter>());
        services.AddSingleton<IToolCatalog, McpToolCatalog>();
        services.AddSingleton<IToolInvoker, McpToolInvoker>();
        return services;
    }
}
