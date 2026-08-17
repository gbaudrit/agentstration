using Agentstration.Application.Work;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;

namespace Agentstration.Web.Hosting;

public static class InteractiveFlowDemoData
{
    public const string FlowName = "demo-interactive-input";
    private const string AgentName = "approval-gated-hash";
    private const string ProviderName = "utilities";
    private const string ToolName = "utilities.hash.compute";
    private const string Version = "1.3.0";
    private const string SampleInput = """
        {
          "payload": "Mon test réel"
        }
        """;
    private const string Instructions = """
        The input is JSON. Select the text to hash from its payload property when present, otherwise from its prompt property. If neither property exists, use the complete JSON input as text. You must call hash_compute exactly once with that text argument, even when the request is vague. Do not ask for clarification and do not compute the digest yourself. After the approved tool returns, explain that the digest came from the Utilities extension and return it.
        """;

    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var workspaceId = services.GetRequiredService<IWorkplaceContext>().WorkspaceId;
        if (!Uri.TryCreate(
                configuration["Agentstration:Extensions:Agentstration.Extensions.Utilities:Endpoint"],
                UriKind.Absolute,
                out _))
        {
            return;
        }

        var tools = services.GetRequiredService<ToolManagementService>();
        var provider = await tools.GetProviderAsync(ProviderName, cancellationToken);
        if (provider is null)
        {
            provider = await tools.PutProviderAsync(new ToolProviderResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.ToolProvider,
                Metadata = new ResourceMetadata
                {
                    Name = ProviderName,
                    Annotations = new Dictionary<string, string> { ["agentstration.io/sample"] = "standalone" }
                },
                Definition = new ToolProviderProperties
                {
                    DisplayName = "Agentstration deterministic utilities",
                    ProviderType = ToolProviderType.Aep,
                    Aep = new AepToolProviderConfiguration { ExtensionId = "Agentstration.Extensions.Utilities" }
                }
            }, null, true, cancellationToken);
        }

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(InteractiveFlowDemoData));
        var timeProvider = services.GetRequiredService<TimeProvider>();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await tools.RefreshDiscoveryAsync(provider.Value.Metadata.Name, cancellationToken);
                break;
            }
            catch (ToolProviderDiscoveryFailedException exception) when (attempt < 10)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        exception,
                        "Utilities discovery is not ready during sample initialization attempt {Attempt}; retrying.",
                        attempt);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
            }
            catch (ToolProviderDiscoveryFailedException exception)
            {
                logger.LogWarning(exception, "Utilities discovery remained unavailable; interactive sample data was not initialized.");
                return;
            }
        }
        var tool = await tools.GetToolAsync(ToolName, cancellationToken)
            ?? throw new InvalidOperationException($"The Utilities extension did not discover '{ToolName}'.");
        if (!tool.Value.Definition.Enabled || !tool.Value.Definition.RequiresApproval)
        {
            tool = await tools.PutToolAsync(tool.Value with
            {
                Generation = checked(tool.Value.Generation + 1),
                Definition = tool.Value.Definition with { Enabled = true, RequiresApproval = true }
            }, tool.ETag, false, cancellationToken);
        }

        var agents = services.GetRequiredService<AgentManagementService>();
        var store = services.GetRequiredService<IControlPlaneStore>();
        await ManagementDemoData.EnsureAgentAsync(
            agents,
            store,
            AgentName,
            "Computes a real SHA-256 digest through the Utilities AEP extension after explicit user approval.",
            Instructions,
            ["interactive", "approval", "sha256"],
            [ToolName],
            cancellationToken);
        var agent = await agents.GetAgentAsync(AgentName, cancellationToken)
            ?? throw new InvalidOperationException($"The sample Agent '{AgentName}' was not created.");
        var desiredTools = new[] { new ResourceReference(ToolName) };
        if (agent.Value.Definition.Instructions != Instructions
            || !agent.Value.Definition.Tools.SequenceEqual(desiredTools))
        {
            agent = await agents.PutAgentAsync(agent.Value with
            {
                Definition = agent.Value.Definition with
                {
                    Instructions = Instructions,
                    Tools = desiredTools
                }
            }, agent.ETag, false, cancellationToken);
        }
        await agents.PrepareLocalRuntimeAsync(AgentName, agent.Value.Generation, cancellationToken);

        var flows = services.GetRequiredService<FlowService>();
        var definition = new OrchestrationFlowDefinition(
            [
                new FlowTargetReference(FlowTargetKind.Agent, AgentName),
                new FlowTargetReference(FlowTargetKind.Agent, "dotnet-expert")
            ],
            new SequentialOrchestrationPattern());
        var flow = await flows.GetAsync(workspaceId, new FlowId(FlowName), cancellationToken);
        if (flow is null)
        {
            flow = await flows.CreateAsync(workspaceId, new CreateFlowCommand(
                FlowName,
                "Calls a real AEP tool, pauses for approval, then resumes from the persisted MAF checkpoint.",
                Version,
                true,
                definition,
                Metadata()), cancellationToken);
        }
        else if (await flows.GetVersionAsync(workspaceId, flow.Value.Id, Version, cancellationToken) is null)
        {
            flow = await flows.UpdateAsync(
                workspaceId,
                flow.Value.Id,
                new UpdateFlowCommand(
                    "Calls a real AEP tool, pauses for approval, then resumes from the persisted MAF checkpoint.",
                    Version,
                    true,
                    definition,
                    Metadata()),
                flow.ETag,
                cancellationToken);
        }

        if (await flows.GetVersionAsync(workspaceId, flow.Value.Id, Version, cancellationToken) is null)
            await flows.PublishVersionAsync(workspaceId, flow.Value.Id, Version, true, cancellationToken, "Provides a reproducible console input and requires real MAF tool approval through the Utilities AEP extension.");
    }

    private static IReadOnlyDictionary<string, string> Metadata() => new Dictionary<string, string>
    {
        ["agentstration.io/demo"] = "interactive-approval",
        ["agentstration.io/sample-input"] = SampleInput
    };
}
