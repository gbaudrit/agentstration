using System.Net.Http.Headers;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Agentstration.Tools.Mcp;

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
    IAepExtensionRegistrationResolver extensionRegistrations,
    IToolProviderEnvironmentResolver environments,
    IHttpMessageHandlerFactory httpMessageHandlerFactory,
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

        var (descriptor, extension) = await DiscoverAepAsync(provider, cancellationToken);
        var discovered = new List<DiscoveredToolDescriptor>();
        IReadOnlyDictionary<string, bool> capabilities = new Dictionary<string, bool>();
        IReadOnlyDictionary<string, string> serverMetadata = new Dictionary<string, string>();
        foreach (var server in descriptor.Mcp?.Servers ?? [])
        {
            await using var client = await ConnectHttpAsync(
                AepDescriptorValidator.ResolveMcpEndpoint(extension.Registration.Definition.Endpoint, server),
                server.Id,
                extension.AccessTokenProvider,
                cancellationToken);
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

        var (descriptor, extension) = await DiscoverAepAsync(provider, cancellationToken);
        var result = new List<IAgentTool>();
        foreach (var group in tools.GroupBy(tool => (descriptor.Contributions.Tools ?? []).First(value => value.Id == tool.Definition.ExternalId).Mcp.Server, StringComparer.Ordinal))
        {
            var server = descriptor.Mcp!.Servers.First(value => value.Id == group.Key);
            await using var client = await ConnectHttpAsync(
                AepDescriptorValidator.ResolveMcpEndpoint(extension.Registration.Definition.Endpoint, server),
                server.Id,
                extension.AccessTokenProvider,
                cancellationToken);
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

    private async Task<(AepManifest Descriptor, ResolvedAepExtension Extension)> DiscoverAepAsync(ToolProviderResource provider, CancellationToken cancellationToken)
    {
        var extensionId = provider.Definition.Aep!.ExtensionId;
        var extension = await extensionRegistrations.ResolveAsync(provider, cancellationToken);
        using var http = CreateHttpClient(
            McpToolServiceCollectionExtensions.AepClientName,
            extension.Registration.Definition.Endpoint,
            extension.AccessTokenProvider,
            TimeSpan.FromSeconds(15));
        var descriptor = await new AepClient(
            http,
            extension.AccessTokenProvider,
            expectedExtensionId: extensionId).DiscoverAsync(cancellationToken);
        var errors = AepDescriptorValidator.Validate(descriptor);
        if (errors.Count > 0) throw new ToolResolutionException("aep_descriptor_invalid", string.Join(" ", errors));
        if (descriptor.Extension.Id != extensionId) throw new ToolResolutionException("extension_identity_mismatch", $"Expected extension '{extensionId}' but discovered '{descriptor.Extension.Id}'.");
        return (descriptor, extension);
    }

    private Task<McpClient> ConnectMcpAsync(ToolProviderResource provider, CancellationToken cancellationToken)
    {
        var mcp = provider.Definition.Mcp!;
        if (mcp.Transport == McpToolProviderTransport.StreamableHttp)
            return ConnectHttpAsync(mcp.Endpoint!, provider.Metadata.Name, null, cancellationToken);
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

    private Task<McpClient> ConnectHttpAsync(
        Uri endpoint,
        string name,
        IAepAccessTokenProvider? accessTokenProvider,
        CancellationToken cancellationToken)
    {
        var http = CreateHttpClient(
            accessTokenProvider is null
            ? McpToolServiceCollectionExtensions.McpClientName
            : McpToolServiceCollectionExtensions.AepMcpClientName,
            endpoint,
            accessTokenProvider,
            TimeSpan.FromSeconds(90));
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = endpoint, Name = name }, http, loggerFactory, ownsHttpClient: true);
        return McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: cancellationToken);
    }

    private HttpClient CreateHttpClient(
        string name,
        Uri endpoint,
        IAepAccessTokenProvider? accessTokenProvider,
        TimeSpan timeout)
    {
        HttpMessageHandler handler = httpMessageHandlerFactory.CreateHandler(name);
        if (accessTokenProvider is not null)
            handler = new AepBearerAuthenticationHandler(accessTokenProvider) { InnerHandler = handler };
        return new HttpClient(handler, disposeHandler: false) { BaseAddress = endpoint, Timeout = timeout };
    }

    private sealed class AepBearerAuthenticationHandler(IAepAccessTokenProvider accessTokenProvider) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try { return await base.SendAsync(request, cancellationToken); }
            finally { request.Headers.Authorization = null; }
        }
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
            resource.Namespace,
            resource.Definition.Provider?.Namespace ?? resource.Namespace,
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
            var (descriptor, extension) = await DiscoverAepAsync(provider, cancellationToken);
            var mapping = (descriptor.Contributions.Tools ?? []).FirstOrDefault(value => value.Id == tool.Definition.ExternalId)
                ?? throw new ToolResolutionException("aep_tool_not_found", $"AEP contribution '{tool.Definition.ExternalId}' was not found.");
            var server = descriptor.Mcp?.Servers.FirstOrDefault(value => value.Id == mapping.Mcp.Server)
                ?? throw new ToolResolutionException("aep_mcp_server_not_found", $"AEP MCP server '{mapping.Mcp.Server}' was not found.");
            client = await ConnectHttpAsync(
                AepDescriptorValidator.ResolveMcpEndpoint(extension.Registration.Definition.Endpoint, server),
                server.Id,
                extension.AccessTokenProvider,
                cancellationToken);
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
            var result = JsonSerializer.SerializeToElement(
                await native.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(values), cancellationToken));
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("isError", out var isError)
                && isError.ValueKind == JsonValueKind.True)
                throw new ToolResolutionException("mcp_tool_failed", McpErrorMessage(result));
            return result;
        }
    }

    private static string McpErrorMessage(JsonElement result)
    {
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String
                    && text.GetString() is { Length: > 0 } message)
                    return message[..Math.Min(message.Length, 512)];
            }
        }
        return "The MCP Tool reported an execution failure.";
    }
}

public sealed class McpToolCatalog(IResourceStore store, ToolProviderAdapter providers) : IToolCatalog
{
    public async ValueTask<IReadOnlyCollection<IAgentTool>> ResolveAsync(IEnumerable<string> toolIds, CancellationToken cancellationToken = default)
    {
        var resources = new List<ToolResource>();
        foreach (var id in toolIds.Distinct(StringComparer.Ordinal))
        {
            var identity = ToolResourceIdentity.ParseCatalogId(id);
            var tool = await store.GetAsync<ToolResource>(new ResourceKey(ToolResourceKinds.Tool, identity.Name, identity.Namespace), cancellationToken) ?? throw new ToolResolutionException("tool_not_found", $"Tool resource '{id}' was not found.");
            if (!tool.Value.Definition.Enabled) throw new ToolResolutionException("tool_disabled", $"Tool resource '{id}' is disabled.");
            if (tool.Value.Definition.Discovery?.Available != true) throw new ToolResolutionException("tool_unavailable", $"Tool resource '{id}' is no longer available from its provider.");
            if (tool.Value.Definition.Provider is null) throw new ToolResolutionException("tool_mapping_invalid", $"Tool resource '{id}' has no ToolProvider mapping.");
            resources.Add(tool.Value);
        }

        var resolved = new List<IAgentTool>();
        foreach (var group in resources.GroupBy(value => (
                     value.Definition.Provider!.Name,
                     Namespace: value.Definition.Provider.Namespace ?? value.Namespace)))
        {
            var provider = await store.GetAsync<ToolProviderResource>(new ResourceKey(ToolResourceKinds.ToolProvider, group.Key.Name, group.Key.Namespace), cancellationToken) ?? throw new ToolResolutionException("tool_provider_not_found", $"ToolProvider '{group.Key.Name}' was not found.");
            if (!provider.Value.Definition.Enabled) throw new ToolResolutionException("tool_provider_disabled", $"ToolProvider '{provider.Value.Metadata.Name}' is disabled.");
            if (provider.Value.Definition.Mcp?.Internal == true)
                resolved.AddRange(group.Select(Tool));
            else
                resolved.AddRange(await providers.ResolveAsync(provider.Value, group.ToArray(), cancellationToken));
        }
        return resolved;
    }

    private static IAgentTool Tool(ToolResource resource) => new McpAgentTool(
        resource.Name,
        resource.Definition.ExternalId ?? resource.Name,
        resource.Definition.Description,
        resource.Definition.Provider?.Name,
        resource.Namespace,
        resource.Definition.Provider?.Namespace ?? resource.Namespace,
        resource.Definition.ExternalId,
        resource.Definition.Schema?.Input ?? JsonSerializer.SerializeToElement(new { type = "object" }),
        resource.Definition.Schema?.Output,
        resource.Definition.RequiresApproval);
}

internal sealed record McpAgentTool(
    string Id,
    string Name,
    string? Description,
    string? ProviderId,
    ResourceNamespace? Namespace,
    ResourceNamespace? ProviderNamespace,
    string? ExternalId,
    JsonElement InputSchema,
    JsonElement? OutputSchema,
    bool RequiresApproval) : IAgentTool;

public sealed class McpToolInvoker(
    IResourceStore store,
    ToolProviderAdapter providers,
    Lazy<IToolDefinitionExecutor>? internalTools = null,
    Lazy<IEnumerable<IInternalMcpToolHandler>>? builtInTools = null) : IToolInvoker
{
    public async ValueTask<JsonElement?> InvokeAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        var tool = await store.GetAsync<ToolResource>(new ResourceKey(ToolResourceKinds.Tool, context.ToolId, context.ToolNamespace ?? default), cancellationToken)
            ?? throw new ToolResolutionException("tool_not_found", $"Tool resource '{context.ToolId}' was not found.");
        if (!tool.Value.Definition.Enabled) throw new ToolResolutionException("tool_disabled", $"Tool resource '{context.ToolId}' is disabled.");
        if (tool.Value.Definition.Discovery?.Available != true) throw new ToolResolutionException("tool_unavailable", $"Tool resource '{context.ToolId}' is no longer available from its provider.");
        var providerId = tool.Value.Definition.Provider?.Name
            ?? throw new ToolResolutionException("tool_mapping_invalid", $"Tool resource '{context.ToolId}' has no ToolProvider mapping.");
        if (context.ToolProviderId is not null && !string.Equals(context.ToolProviderId, providerId, StringComparison.Ordinal))
            throw new ToolResolutionException("tool_provider_mismatch", $"Tool resource '{context.ToolId}' no longer maps to provider '{context.ToolProviderId}'.");
        if (context.ExternalToolId is not null && !string.Equals(context.ExternalToolId, tool.Value.Definition.ExternalId, StringComparison.Ordinal))
            throw new ToolResolutionException("external_tool_mismatch", $"Tool resource '{context.ToolId}' no longer maps to external Tool '{context.ExternalToolId}'.");
        var provider = await store.GetAsync<ToolProviderResource>(new ResourceKey(ToolResourceKinds.ToolProvider, providerId, context.ToolProviderNamespace ?? default), cancellationToken)
            ?? throw new ToolResolutionException("tool_provider_not_found", $"ToolProvider '{providerId}' was not found.");
        if (!provider.Value.Definition.Enabled) throw new ToolResolutionException("tool_provider_disabled", $"ToolProvider '{providerId}' is disabled.");
        if (provider.Value.Definition.Mcp?.Internal == true)
        {
            if (context.TenantId is not { } tenantId || context.WorkspaceId is not { } workspaceId || context.PrincipalId is not { } principalId)
                throw new ToolResolutionException("tool_execution_scope_required", "An internal Tool invocation requires trusted Tenant, Workspace, and Principal scope.");
            var externalId = tool.Value.Definition.ExternalId ?? tool.Value.Name;
            var builtIn = builtInTools?.Value.SingleOrDefault(value => string.Equals(value.Definition.Name, externalId, StringComparison.Ordinal));
            if (builtIn is not null)
                return await builtIn.ExecuteAsync(new InternalMcpToolInvocation(
                    tenantId,
                    workspaceId,
                    principalId,
                    context.ToolCallId,
                    context.CorrelationId,
                    context.Arguments ?? JsonSerializer.SerializeToElement(new { }),
                    context.AgentId is not null ? ToolDefinitionCallerKind.Agent : context.OwnerKind == ToolExecutionOwnerKind.FlowRun ? ToolDefinitionCallerKind.Flow : ToolDefinitionCallerKind.Agent,
                    context.AgentId is not null ? $"agent:{context.AgentId}" : context.RunId is not null ? $"flow:{context.RunId}" : null,
                    context.RunId,
                    context.FlowStepId), cancellationToken);
            if (internalTools is null) throw new ToolResolutionException("internal_tool_executor_unavailable", "The Agentstration ToolDefinition executor is unavailable.");
            var result = await internalTools.Value.ExecuteAsync(new ToolDefinitionInvocation(
                tenantId,
                workspaceId,
                principalId,
                tool.Value.Namespace,
                externalId,
                context.ToolCallId,
                context.CorrelationId,
                context.Arguments ?? JsonSerializer.SerializeToElement(new { }),
                context.AgentId is not null ? ToolDefinitionCallerKind.Agent : context.OwnerKind == ToolExecutionOwnerKind.FlowRun ? ToolDefinitionCallerKind.Flow : ToolDefinitionCallerKind.Agent,
                context.AgentId is not null ? $"agent:{context.AgentId}" : context.RunId is not null ? $"flow:{context.RunId}" : null,
                new ToolDefinitionInvocationContext(
                    context.AgentId,
                    context.AgentRevisionId,
                    context.OwnerKind == ToolExecutionOwnerKind.RuntimeRun ? context.RunId : null,
                    context.OwnerKind == ToolExecutionOwnerKind.FlowRun ? context.RunId : null,
                    context.FlowStepId,
                    context.InvocationId)), cancellationToken);
            return result.Output?.Clone();
        }
        return await providers.InvokeAsync(provider.Value, tool.Value, context.Arguments, cancellationToken);
    }
}

public sealed class ToolResolutionException(string code, string message, Exception? innerException = null) : Exception(message, innerException) { public string Code { get; } = code; }

public static class McpToolServiceCollectionExtensions
{
    internal const string AepClientName = "agentstration-aep-tools";
    internal const string AepMcpClientName = "agentstration-aep-mcp-tools";
    internal const string McpClientName = "agentstration-mcp-tools";
    public static IServiceCollection AddAgentstrationMcpTools(this IServiceCollection services)
    {
        services.TryAddSingleton<IConfiguration>(_ => new ConfigurationBuilder().Build());
        services.TryAddSingleton(new AepTransportSecurityOptions());
        services.AddHttpClient(AepClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(provider =>
                AepSecureHttpMessageHandler.Create(provider.GetRequiredService<AepTransportSecurityOptions>()));
        services.AddHttpClient(AepMcpClientName, client => client.Timeout = TimeSpan.FromSeconds(90))
            .ConfigurePrimaryHttpMessageHandler(provider =>
                AepSecureHttpMessageHandler.Create(provider.GetRequiredService<AepTransportSecurityOptions>()));
        services.AddHttpClient(McpClientName, client => client.Timeout = TimeSpan.FromSeconds(90))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IAepExtensionRegistrationResolver, ResourceAepExtensionRegistrationResolver>();
        services.AddSingleton<IToolProviderEnvironmentResolver, ConfigurationToolProviderEnvironmentResolver>();
        services.AddSingleton<ToolProviderAdapter>();
        services.AddSingleton<IToolProviderDiscovery>(provider => provider.GetRequiredService<ToolProviderAdapter>());
        services.AddSingleton<IToolCatalog, McpToolCatalog>();
        services.AddSingleton<IToolInvoker, McpToolInvoker>();
        return services;
    }
}
