using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Models;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Agents;

public interface IAgentDefinitionCompiler
{
    ResolvedAgentDefinition Compile(
        AgentResource agent,
        AgentDeploymentSpec deployment,
        IReadOnlyCollection<ResourceReference>? toolSetTools = null,
        IReadOnlyCollection<ResolvedAgentToolSetAssignment>? toolSetAssignments = null,
        IReadOnlyCollection<ResolvedResourceDependency>? toolPins = null,
        ResolvedResourceDependency? modelProfilePin = null,
        ResolvedResourceDependency? runtimeProfilePin = null);
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

    public ResolvedAgentDefinition Compile(
        AgentResource resource,
        AgentDeploymentSpec deployment,
        IReadOnlyCollection<ResourceReference>? toolSetTools = null,
        IReadOnlyCollection<ResolvedAgentToolSetAssignment>? toolSetAssignments = null,
        IReadOnlyCollection<ResolvedResourceDependency>? toolPins = null,
        ResolvedResourceDependency? modelProfilePin = null,
        ResolvedResourceDependency? runtimeProfilePin = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(deployment);
        var agent = resource.Definition;
        if (!SupportedHandlers.Contains(agent.Handler))
            throw new AgentDefinitionValidationException("handler_not_supported", $"Handler '{agent.Handler}' is not supported.");

        var instructions = NormalizeInstructions(agent.Instructions);
        var tools = agent.Tools.Concat(toolSetTools ?? [])
            .Select(reference => reference.Resolve(resource.Namespace, ToolResourceKinds.Tool))
            .Select(address => ToolResourceIdentity.CatalogId(address.Namespace, address.Name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var middleware = agent.Middleware.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var contextProviders = agent.ContextProviders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var capabilities = agent.Behaviors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var modelProfile = agent.ModelProfile.Resolve(resource.Namespace, ModelResourceKinds.ModelProfile);
        var canonical = new
        {
            resource.Uid,
            resource.Metadata.Name,
            agent.DisplayName,
            agent.Description,
            AgentVersion = resource.Generation,
            agent.Handler,
            Instructions = instructions,
            ModelProfile = modelProfile,
            ModelProfilePin = modelProfilePin,
            deployment.RuntimeProfileName,
            deployment.RuntimeProfileNamespace,
            RuntimeProfilePin = runtimeProfilePin,
            deployment.HostingMode,
            Tools = tools,
            ToolPins = toolPins ?? [],
            ToolSetAssignments = toolSetAssignments ?? [],
            Middleware = middleware,
            ContextProviders = contextProviders,
            Capabilities = capabilities,
            Settings = agent.Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            agent.InputSchema,
            agent.OutputSchema
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
            ModelProfileName = modelProfile.Name,
            ModelProfileNamespace = modelProfile.Namespace,
            ModelProfilePin = modelProfilePin,
            RuntimeProfileName = deployment.RuntimeProfileName,
            RuntimeProfileNamespace = deployment.RuntimeProfileNamespace,
            RuntimeProfilePin = runtimeProfilePin,
            EffectiveToolNames = tools,
            ToolPins = toolPins ?? [],
            ToolSetAssignments = toolSetAssignments ?? [],
            MiddlewareIds = middleware,
            ContextProviderIds = contextProviders,
            Capabilities = capabilities,
            Handler = agent.Handler,
            DefinitionHash = hash,
            InputSchema = agent.InputSchema?.Clone(),
            OutputSchema = agent.OutputSchema?.Clone()
        };
    }

    private static string NormalizeInstructions(string instructions) =>
        instructions.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
}
