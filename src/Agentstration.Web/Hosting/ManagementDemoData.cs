using Agentstration.Agents;
using Agentstration.Extensions;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Web.Hosting;

public static class ManagementDemoData
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var agents = services.GetRequiredService<AgentManagementService>();
        var providers = services.GetRequiredService<ModelProviderManagementService>();
        var extensions = services.GetRequiredService<ExtensionRegistrationManagementService>();
        var profiles = services.GetRequiredService<ModelProfileManagementService>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var accessPolicies = services.GetRequiredService<IResourceAccessPolicyService>();
        var resources = services.GetRequiredService<IResourceStore>();
        var requestContext = services.GetRequiredService<ICurrentRequestContext>();
        var workspaceId = requestContext.Current.WorkspaceId;
        var scopeFactory = services.GetRequiredService<IRequestContextScopeFactory>();
        using (scopeFactory.PushSystem())
        {
            if (await extensions.GetAsync(ResourceNamespace.Default, "ollama-extension", cancellationToken) is not null
                && await providers.GetAsync("ollama-local", cancellationToken) is null)
            {
                await providers.CreateAsync(new ModelProviderResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ModelResourceKinds.ModelProvider,
                    Metadata = new ResourceMetadata { Name = "ollama-local", Tags = new Dictionary<string, string> { ["sample"] = "standalone" } },
                    Definition = new ModelProviderProperties
                    {
                        DisplayName = "Ollama via AEP",
                        Extension = new ResourceReference("ollama-extension"),
                        ContributionId = "ollama"
                    }
                }, cancellationToken);
            }

            if (await extensions.GetAsync(ResourceNamespace.Default, "llama-cpp-extension", cancellationToken) is not null
                && await providers.GetAsync("llama-cpp-local", cancellationToken) is null)
            {
                await providers.CreateAsync(new ModelProviderResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ModelResourceKinds.ModelProvider,
                    Metadata = new ResourceMetadata { Name = "llama-cpp-local", Tags = new Dictionary<string, string> { ["sample"] = "standalone" } },
                    Definition = new ModelProviderProperties
                    {
                        DisplayName = "llama.cpp via AEP",
                        Extension = new ResourceReference("llama-cpp-extension"),
                        ContributionId = "llamacpp"
                    }
                }, cancellationToken);
            }

            if (await extensions.GetAsync(ResourceNamespace.Default, "localai-extension", cancellationToken) is not null
                && await providers.GetAsync("localai-local", cancellationToken) is null)
            {
                await providers.CreateAsync(new ModelProviderResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ModelResourceKinds.ModelProvider,
                    Metadata = new ResourceMetadata { Name = "localai-local", Tags = new Dictionary<string, string> { ["sample"] = "standalone" } },
                    Definition = new ModelProviderProperties
                    {
                        DisplayName = "LocalAI via AEP",
                        Extension = new ResourceReference("localai-extension"),
                        ContributionId = "localai"
                    }
                }, cancellationToken);
            }

            foreach (var extension in await extensions.ListAsync(cancellationToken))
            {
                if (extension.Value.ScopeRef != ResourceScopeRef.Instance) continue;
                await EnsureWorkspaceAccessAsync(accessPolicies, new ResourceAccessIdentity(
                    extension.Value.Uid, ResourceScopeRef.Instance, extension.Value.Namespace,
                    ExtensionKinds.ExtensionRegistration, extension.Value.Generation), workspaceId, cancellationToken);
            }

            foreach (var providerName in new[] { "ollama-local", "llama-cpp-local", "localai-local" })
            {
                var provider = await providers.GetExactAsync(ResourceScopeRef.Instance,
                    ResourceNamespace.Default, providerName, cancellationToken);
                if (provider is null) continue;
                await EnsureWorkspaceAccessAsync(accessPolicies, new ResourceAccessIdentity(
                    provider.Value.Uid, ResourceScopeRef.Instance, provider.Value.Namespace,
                    ModelResourceKinds.ModelProvider, provider.Value.Generation), workspaceId, cancellationToken);
            }

            var ollamaProvider = await providers.GetAsync("ollama-local", cancellationToken);
            if (ollamaProvider is not null && await profiles.GetAsync("reasoning-default", cancellationToken) is null)
            {
                await profiles.CreateAsync(new ModelProfileResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ModelResourceKinds.ModelProfile,
                    Metadata = new ResourceMetadata { Name = "reasoning-default" },
                    Definition = new ModelProfileProperties
                    {
                        DisplayName = "Default reasoning",
                        Description = "General-purpose local reasoning profile.",
                        Provider = new ResourceReference("ollama-local", ResourceScopeRef.Instance),
                        Model = new ModelSelection { Name = configuration["Agentstration:Seed:OllamaModel"] ?? configuration["AI:Model"] ?? "qwen3:1.7b" },
                        Generation = new ModelGenerationOptions { Temperature = 0.2 }
                    },
                    ScopeRef = ResourceScopeRef.Instance
                }, cancellationToken);
            }

            var reasoningProfile = await profiles.GetExactAsync(ResourceScopeRef.Instance,
                ResourceNamespace.Default, "reasoning-default", cancellationToken);
            if (reasoningProfile is null) return;
            await EnsureWorkspaceAccessAsync(accessPolicies, new ResourceAccessIdentity(
                reasoningProfile.Value.Uid, ResourceScopeRef.Instance, reasoningProfile.Value.Namespace,
                ModelResourceKinds.ModelProfile, reasoningProfile.Value.Generation), workspaceId, cancellationToken);

            var runtimeProfile = await resources.GetExactAsync<RuntimeProfileResource>(
                ScopedResourceAddress.Create(ResourceScopeRef.Instance, ResourceNamespace.Default,
                    RuntimeProfileResourceKinds.RuntimeProfile, "maf-builtin"), cancellationToken);
            if (runtimeProfile is not null)
            {
                await EnsureWorkspaceAccessAsync(accessPolicies, new ResourceAccessIdentity(
                    runtimeProfile.Value.Uid, ResourceScopeRef.Instance, runtimeProfile.Value.Namespace,
                    RuntimeProfileResourceKinds.RuntimeProfile, runtimeProfile.Value.Generation), workspaceId, cancellationToken);
            }
        }

        await EnsureAgentAsync(agents, "dotnet-expert", "Specialized agent for .NET, C#, ASP.NET Core, and runtime diagnostics.", "Focus on .NET and C#. Provide safe, practical guidance.", ["dotnet", "csharp", "aspnet"], [], cancellationToken);
        await EnsureAgentAsync(agents, "sql-expert", "Specialized agent for SQL query performance and database diagnostics.", "Focus on SQL performance and read-only diagnostics.", ["sql", "database", "query-performance"], [], cancellationToken);
    }

    private static async Task EnsureWorkspaceAccessAsync(
        IResourceAccessPolicyService accessPolicies,
        ResourceAccessIdentity identity,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var existing = await accessPolicies.GetAsync(identity.Uid, cancellationToken);
        var workspaceScope = ResourceScopeRef.Workspace(workspaceId);
        var grants = (existing?.Policy.Grants ?? [])
            .Where(grant => grant.TargetScopeRef != workspaceScope)
            .Append(new ResourceAccessGrant(workspaceScope,
                [ResourceAccessAction.Discover, ResourceAccessAction.Use]))
            .ToArray();
        if (existing?.Policy.Grants.Any(grant => grant.TargetScopeRef == workspaceScope
                && grant.Actions.Contains(ResourceAccessAction.Discover)
                && grant.Actions.Contains(ResourceAccessAction.Use)) == true)
            return;
        _ = await accessPolicies.PutAsync(new ResourceAccessPolicy(identity, grants), existing?.ETag, cancellationToken);
    }

    internal static async Task EnsureAgentAsync(
        AgentManagementService management,
        string name,
        string description,
        string instructions,
        IReadOnlyCollection<string> capabilities,
        IReadOnlyCollection<string> toolNames,
        CancellationToken cancellationToken)
    {
        var agent = await management.GetAgentAsync(name, cancellationToken);
        if (agent is null)
        {
            agent = await management.PutAgentAsync(new AgentResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = AgentResourceKinds.Agent,
                Metadata = new ResourceMetadata
                {
                    Name = name,
                    Tags = capabilities.ToDictionary(value => "capability-" + value, _ => "true", StringComparer.Ordinal),
                    Annotations = new Dictionary<string, string> { ["agentstration.io/sample"] = "standalone" }
                },
                Definition = new AgentProperties
                {
                    DisplayName = name,
                    Description = description,
                    Instructions = instructions,
                    ModelProfile = new ResourceReference("reasoning-default"),
                    Tools = toolNames.Select(toolName => new ResourceReference(toolName)).ToArray(),
                    Behaviors = capabilities.ToArray()
                }
            }, null, true, cancellationToken);
        }

        _ = await management.EnsureExecutionRevisionAsync(ResourceNamespace.Default, name, agent.Value.Generation, cancellationToken);
    }

}
