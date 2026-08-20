using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public interface IAgentDefinitionCompiler
{
    ResolvedAgentDefinition Compile(AgentResource agent, AgentDeploymentSpec deployment);
}

public sealed class AgentDefinitionValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class AgentDefinitionCompiler : IAgentDefinitionCompiler
{
    private static readonly HashSet<string> SupportedHandlers = new(StringComparer.Ordinal)
    {
        "prompt-agent", "router-agent", "remote-agent", "custom-agent"
    };

    public ResolvedAgentDefinition Compile(AgentResource resource, AgentDeploymentSpec deployment)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(deployment);
        var agent = resource.Definition;
        if (!SupportedHandlers.Contains(agent.Handler))
            throw new AgentDefinitionValidationException("handler_not_supported", $"Handler '{agent.Handler}' is not supported.");

        var instructions = NormalizeInstructions(agent.Instructions);
        var tools = agent.Tools.Select(reference => reference.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var middleware = agent.Middleware.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var contextProviders = agent.ContextProviders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var capabilities = agent.Behaviors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var canonical = new
        {
            resource.Uid,
            resource.Metadata.Name,
            agent.DisplayName,
            agent.Description,
            AgentVersion = resource.Generation,
            agent.Handler,
            Instructions = instructions,
            ModelProfile = agent.ModelProfile,
            deployment.RuntimeProfileName,
            deployment.RuntimeProfileNamespace,
            deployment.HostingMode,
            Tools = tools,
            Middleware = middleware,
            ContextProviders = contextProviders,
            Capabilities = capabilities,
            Settings = agent.Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()
        };
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical))).ToLowerInvariant();

        return new ResolvedAgentDefinition
        {
            AgentId = resource.Uid,
            AgentKey = resource.Metadata.Name,
            DisplayName = agent.DisplayName,
            Description = agent.Description ?? string.Empty,
            AgentVersion = resource.Generation,
            EffectiveInstructions = instructions,
            ModelProfileName = agent.ModelProfile.Name,
            RuntimeProfileName = deployment.RuntimeProfileName,
            RuntimeProfileNamespace = deployment.RuntimeProfileNamespace,
            EffectiveToolNames = tools,
            MiddlewareIds = middleware,
            ContextProviderIds = contextProviders,
            Capabilities = capabilities,
            Handler = agent.Handler,
            DefinitionHash = hash
        };
    }

    private static string NormalizeInstructions(string instructions) =>
        instructions.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
}
