using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public interface IAgentDefinitionCompiler
{
    ResolvedAgentDefinition Compile(AgentTypeDefinition type, AgentResource agent, AgentDeploymentSpec deployment);
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

    public ResolvedAgentDefinition Compile(AgentTypeDefinition type, AgentResource resource, AgentDeploymentSpec deployment)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(deployment);
        var agent = resource.Properties;
        Validate(type, agent);

        var instructions = NormalizeInstructions(type.BaseInstructions);
        if (!string.IsNullOrWhiteSpace(agent.AdditionalInstructions))
        {
            instructions = $"{instructions}\n\n{NormalizeInstructions(agent.AdditionalInstructions)}";
        }

        var modelProfile = agent.ModelProfile.ResourceId;
        var tools = type.RequiredToolIds
            .Concat(agent.Tools.Select(tool => tool.ResourceId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var middleware = type.MiddlewareIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var contextProviders = type.ContextProviderIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var capabilities = type.BehaviorIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        var canonical = new
        {
            resource.Id,
            resource.Name,
            agent.DisplayName,
            agent.Description,
            AgentVersion = resource.Generation,
            TypeKey = type.Key,
            TypeVersion = type.Version,
            Instructions = instructions,
            ModelProfile = modelProfile,
            deployment.RuntimeProfileId,
            deployment.HostingMode,
            Tools = tools,
            Middleware = middleware,
            ContextProviders = contextProviders,
            Capabilities = capabilities,
            type.Handler,
            Settings = agent.Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        return new ResolvedAgentDefinition
        {
            AgentId = StableGuid(resource.Id),
            AgentKey = resource.Name,
            DisplayName = agent.DisplayName,
            Description = agent.Description ?? string.Empty,
            AgentVersion = resource.Generation,
            EffectiveInstructions = instructions,
            ModelProfileId = modelProfile,
            RuntimeProfileId = deployment.RuntimeProfileId,
            EffectiveToolIds = tools,
            MiddlewareIds = middleware,
            ContextProviderIds = contextProviders,
            Capabilities = capabilities,
            Handler = type.Handler,
            DefinitionHash = hash
        };
    }

    private static void Validate(AgentTypeDefinition type, AgentProperties agent)
    {
        if (!SupportedHandlers.Contains(type.Handler))
            throw new AgentDefinitionValidationException("handler_not_supported", $"Handler '{type.Handler}' is not supported.");
        if (agent.AgentType.Version is not null && type.Version != agent.AgentType.Version)
            throw new AgentDefinitionValidationException("type_version_mismatch", "The agent must reference the exact agent type version.");
        if (!string.IsNullOrWhiteSpace(agent.AdditionalInstructions))
        {
            if (!type.Policy.AllowAdditionalInstructions)
                throw new AgentDefinitionValidationException("instructions_override_forbidden", "The agent type does not allow additional instructions.");
            if (agent.AdditionalInstructions.Length > type.Policy.MaximumAdditionalInstructionsLength)
                throw new AgentDefinitionValidationException("instructions_too_long", $"Additional instructions exceed {type.Policy.MaximumAdditionalInstructionsLength} characters.");
        }
        var modelProfileName = ResourceIdentifier.Parse(agent.ModelProfile.ResourceId).Name;
        if (!string.Equals(modelProfileName, type.DefaultModelProfileId, StringComparison.Ordinal) && !type.Policy.AllowModelOverride)
            throw new AgentDefinitionValidationException("model_override_forbidden", "The agent type does not allow a model profile override.");
        if (agent.Tools.Count > 0 && !type.Policy.AllowAdditionalTools)
            throw new AgentDefinitionValidationException("additional_tools_forbidden", "The agent type does not allow additional tools.");

        var allowed = type.AllowedToolIds.Concat(type.RequiredToolIds).ToHashSet(StringComparer.Ordinal);
        var forbidden = agent.Tools.Select(tool => ResourceIdentifier.Parse(tool.ResourceId))
            .Where(tool => !allowed.Contains(tool.Value) && !allowed.Contains(tool.Name))
            .Select(tool => tool.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (forbidden.Length > 0)
            throw new AgentDefinitionValidationException("tool_not_allowed", $"Tools are not allowed by the agent type: {string.Join(", ", forbidden)}.");
    }

    private static string NormalizeInstructions(string instructions) =>
        instructions.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
