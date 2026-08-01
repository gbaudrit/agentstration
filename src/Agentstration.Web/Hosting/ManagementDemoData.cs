using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;

namespace Agentstration.Web.Hosting;

public static class ManagementDemoData
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var management = services.GetRequiredService<AgentManagementService>();
        var store = services.GetRequiredService<IControlPlaneStore>();
        const string resourceGroup = "default";
        var existingTypes = await store.ListAsync<AgentTypeResource>(AgentstrationResourceTypes.AgentTypes, null, 0, 1, cancellationToken);
        var existingAgents = await store.ListAsync<AgentResource>(AgentstrationResourceTypes.Agents, null, 0, 1, cancellationToken);
        if (existingTypes.Count > 0 || existingAgents.Count > 0) return;
        var typeId = ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.Agents, "agentTypes", "readonly-expert").Value;
        var type = await management.GetAgentTypeAsync(typeId, cancellationToken);
        if (type is null)
        {
            var definition = new AgentTypeDefinition
            {
                Key = "readonly-expert",
                Version = 1,
                Handler = "prompt-agent",
                BaseInstructions = "You are a specialized read-only expert. Never perform write operations. Clearly indicate when information is missing.",
                DefaultModelProfileId = "reasoning-default",
                Policy = new AgentTypePolicy
                {
                    AllowAdditionalInstructions = true,
                    AllowModelOverride = true,
                    MaximumAdditionalInstructionsLength = 10_000
                }
            };
            type = await management.PutAgentTypeAsync(new AgentTypeResource
            {
                Id = typeId,
                Name = definition.Key,
                Type = AgentstrationResourceTypes.AgentTypes,
                ApiVersion = ManagementApiVersions.V20260801,
                ResourceGroup = resourceGroup,
                Location = "local",
                Tags = new Dictionary<string, string> { ["sample"] = "standalone" },
                Properties = definition
            }, null, true, cancellationToken);
        }

        await EnsureAgentAsync(
            management,
            store,
            type.Value,
            "dotnet-expert",
            "Specialized agent for .NET, C#, ASP.NET Core, and runtime diagnostics.",
            "Focus on .NET and C#. Provide safe, practical guidance.",
            ["dotnet", "csharp", "aspnet"],
            cancellationToken);
        await EnsureAgentAsync(
            management,
            store,
            type.Value,
            "sql-expert",
            "Specialized agent for SQL query performance and database diagnostics.",
            "Focus on SQL performance and read-only diagnostics.",
            ["sql", "database", "query-performance"],
            cancellationToken);
    }

    private static async Task EnsureAgentAsync(
        AgentManagementService management,
        IControlPlaneStore store,
        AgentTypeResource type,
        string key,
        string description,
        string additionalInstructions,
        IReadOnlyCollection<string> capabilities,
        CancellationToken cancellationToken)
    {
        var resourceGroup = type.ResourceGroup!;
        var agentId = ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.Agents, "agents", key).Value;
        var agent = await management.GetAgentAsync(agentId, cancellationToken);
        if (agent is null)
        {
            agent = await management.PutAgentAsync(new AgentResource
            {
                Id = agentId,
                Name = key,
                Type = AgentstrationResourceTypes.Agents,
                ApiVersion = ManagementApiVersions.V20260801,
                ResourceGroup = resourceGroup,
                Location = "local",
                Tags = capabilities.ToDictionary(value => $"capability-{value}", _ => "true", StringComparer.Ordinal),
                Properties = new AgentProperties
                {
                    DisplayName = key,
                    Description = description,
                    AgentType = new AgentTypeReference(type.Id, type.Properties.Version),
                    AdditionalInstructions = additionalInstructions,
                    ModelProfile = new ResourceReference(ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.Models, "modelProfiles", type.Properties.DefaultModelProfileId).Value)
                }
            }, null, true, cancellationToken);
        }

        var revisions = await store.ListAsync<AgentRevision>(AgentstrationResourceTypes.AgentRevisions, resourceGroup, 0, 1000, cancellationToken);
        var revision = revisions.Where(value => string.Equals(value.Value.AgentResourceId, agentId, StringComparison.Ordinal)).OrderByDescending(value => value.Value.CreatedAt).FirstOrDefault();
        var spec = new AgentDeploymentSpec { Environment = "local", RuntimeProfileId = "standalone", HostingMode = AgentHostingMode.InProcess };
        revision ??= await management.CreateRevisionAsync(agentId, spec, cancellationToken);

        var deploymentId = ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.Agents, "deployments", key).Value;
        var deployment = await management.GetDeploymentAsync(deploymentId, cancellationToken)
            ?? await management.CreateDeploymentAsync(resourceGroup, key, "local", revision.Value.Id, spec, cancellationToken);
        if (deployment.Value.OperationalState != OperationalState.Ready)
            await management.ReconcileAsync(deployment, cancellationToken);
    }
}
