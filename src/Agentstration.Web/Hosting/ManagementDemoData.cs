using System.Data.Common;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;

namespace Agentstration.Web.Hosting;

public static class ManagementDemoData
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var agents = services.GetRequiredService<AgentManagementService>();
        var providers = services.GetRequiredService<ModelProviderManagementService>();
        var profiles = services.GetRequiredService<ModelProfileManagementService>();
        var runtimes = services.GetRequiredService<RuntimeProfileManagementService>();
        var store = services.GetRequiredService<IControlPlaneStore>();
        var configuration = services.GetRequiredService<IConfiguration>();

        if (await providers.GetAsync("ollama-local", cancellationToken) is null)
        {
            var connectionString = configuration.GetConnectionString("ollama-extension");
            var configuredEndpoint = configuration["Agentstration:Extensions:Agentstration.Extensions.Ollama:Endpoint"];
            var endpoint = ResolveEndpoint(connectionString) ?? (Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var value) ? value : new Uri("http://localhost:5260"));
            await providers.CreateAsync(new ModelProviderResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.ModelProvider,
                Metadata = new ResourceMetadata { Name = "ollama-local", Tags = new Dictionary<string, string> { ["sample"] = "standalone" } },
                Definition = new ModelProviderProperties
                {
                    DisplayName = "Ollama via AEP",
                    ProviderType = "ollama",
                    Endpoint = endpoint,
                    ManagementMode = string.IsNullOrWhiteSpace(connectionString) ? ModelProviderManagementMode.External : ModelProviderManagementMode.Aspire
                }
            }, cancellationToken);
        }

        if (await runtimes.GetAsync("maf-default", cancellationToken) is null)
        {
            await runtimes.CreateAsync(new RuntimeProfileResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.RuntimeProfile,
                Metadata = new ResourceMetadata { Name = "maf-default" },
                Definition = new RuntimeProfileProperties
                {
                    DisplayName = "Microsoft Agent Framework",
                    RuntimeType = "microsoft-agent-framework",
                    Execution = new RuntimeExecutionDefaults
                    {
                        SessionMode = RuntimeSessionMode.Transient,
                        ToolInvocation = RuntimeToolInvocationMode.Automatic,
                        Streaming = StreamingMode.Automatic
                    }
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

        await EnsureAgentAsync(agents, store, "dotnet-expert", "Specialized agent for .NET, C#, ASP.NET Core, and runtime diagnostics.", "Focus on .NET and C#. Provide safe, practical guidance.", ["dotnet", "csharp", "aspnet"], cancellationToken);
        await EnsureAgentAsync(agents, store, "sql-expert", "Specialized agent for SQL query performance and database diagnostics.", "Focus on SQL performance and read-only diagnostics.", ["sql", "database", "query-performance"], cancellationToken);
    }

    private static async Task EnsureAgentAsync(AgentManagementService management, IControlPlaneStore store, string name, string description, string instructions, IReadOnlyCollection<string> capabilities, CancellationToken cancellationToken)
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
                    Behaviors = capabilities.ToArray()
                }
            }, null, true, cancellationToken);
        }

        var revisions = await store.ListAsync<AgentRevision>(ResourceKinds.AgentRevision, 0, 1000, cancellationToken);
        var revision = revisions.Where(value => value.Value.AgentUid == agent.Value.Uid).OrderByDescending(value => value.Value.CreatedAt).FirstOrDefault();
        var spec = new AgentDeploymentSpec { Environment = "local", RuntimeProfileName = "maf-default", HostingMode = AgentHostingMode.InProcess };
        revision ??= await management.CreateRevisionAsync(name, spec, cancellationToken);
        var deployment = await management.GetDeploymentAsync(name, cancellationToken)
            ?? await management.CreateDeploymentAsync(name, revision.Value.Metadata.Name, spec, cancellationToken);
        if (deployment.Value.OperationalState != OperationalState.Ready) await management.ReconcileAsync(deployment, cancellationToken);
    }

    private static Uri? ResolveEndpoint(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var endpoint)) return endpoint;
        try
        {
            var values = new DbConnectionStringBuilder { ConnectionString = connectionString };
            return values.TryGetValue("Endpoint", out var value) && Uri.TryCreate(value?.ToString(), UriKind.Absolute, out endpoint) ? endpoint : null;
        }
        catch (ArgumentException) { return null; }
    }
}
