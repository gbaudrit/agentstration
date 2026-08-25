using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;

namespace Agentstration.Web.Hosting;

public static class ManagementDemoData
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var agents = services.GetRequiredService<AgentManagementService>();
        var providers = services.GetRequiredService<ModelProviderManagementService>();
        var extensions = services.GetRequiredService<ExtensionRegistrationManagementService>();
        var extensionDiscovery = services.GetRequiredService<ExtensionSourceDiscoveryService>();
        var profiles = services.GetRequiredService<ModelProfileManagementService>();
        var store = services.GetRequiredService<IControlPlaneStore>();
        var configuration = services.GetRequiredService<IConfiguration>();
        _ = await extensionDiscovery.DiscoverAsync(cancellationToken);

        if (await extensions.GetAsync(ResourceNamespace.Default, "ollama-extension", cancellationToken) is not null
            && await providers.GetAsync("ollama-local", cancellationToken) is null)
        {
            await providers.CreateAsync(new ModelProviderResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.ModelProvider,
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
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.ModelProvider,
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
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.ModelProvider,
                Metadata = new ResourceMetadata { Name = "localai-local", Tags = new Dictionary<string, string> { ["sample"] = "standalone" } },
                Definition = new ModelProviderProperties
                {
                    DisplayName = "LocalAI via AEP",
                    Extension = new ResourceReference("localai-extension"),
                    ContributionId = "localai"
                }
            }, cancellationToken);
        }

        if (await profiles.GetAsync("reasoning-default", cancellationToken) is null)
        {
            await profiles.CreateAsync(new ModelProfileResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.ModelProfile,
                Metadata = new ResourceMetadata { Name = "reasoning-default" },
                Definition = new ModelProfileProperties
                {
                    DisplayName = "Default reasoning",
                    Description = "General-purpose local reasoning profile.",
                    Provider = new ResourceReference("ollama-local"),
                    Model = new ModelSelection { Name = configuration["Agentstration:Seed:OllamaModel"] ?? configuration["AI:Model"] ?? "qwen3:1.7b" },
                    Generation = new ModelGenerationOptions { Temperature = 0.2 }
                }
            }, cancellationToken);
        }

        await EnsureAgentAsync(agents, store, "dotnet-expert", "Specialized agent for .NET, C#, ASP.NET Core, and runtime diagnostics.", "Focus on .NET and C#. Provide safe, practical guidance.", ["dotnet", "csharp", "aspnet"], [], cancellationToken);
        await EnsureAgentAsync(agents, store, "sql-expert", "Specialized agent for SQL query performance and database diagnostics.", "Focus on SQL performance and read-only diagnostics.", ["sql", "database", "query-performance"], [], cancellationToken);
    }

    internal static async Task EnsureAgentAsync(
        AgentManagementService management,
        IControlPlaneStore store,
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
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.Agent,
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

        var revisions = await store.ListAllAsync<AgentRevision>(ResourceKinds.AgentRevision, cancellationToken);
        var revision = revisions.Where(value => value.Value.AgentUid == agent.Value.Uid).OrderByDescending(value => value.Value.CreatedAt).FirstOrDefault();
        var spec = new AgentDeploymentSpec { Environment = "local", RuntimeProfileName = "maf-builtin", HostingMode = AgentHostingMode.InProcess };
        revision ??= await management.CreateRevisionAsync(name, spec, cancellationToken);
        var deployment = await management.GetDeploymentAsync(name, cancellationToken)
            ?? await management.CreateDeploymentAsync(name, revision.Value.Metadata.Name, spec, cancellationToken);
        if (deployment.Value.OperationalState != OperationalState.Ready) await management.ReconcileAsync(deployment, cancellationToken);
    }

}
