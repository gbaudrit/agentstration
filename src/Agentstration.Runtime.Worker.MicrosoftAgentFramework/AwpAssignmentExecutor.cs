using System.Text;
using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.MicrosoftAgentFramework;

namespace Agentstration.Runtime.Worker.MicrosoftAgentFramework;

internal sealed class AwpAssignmentExecutor(ILoggerFactory loggerFactory)
{
    public async Task<JsonElement?> ExecuteAsync(AwpAssignmentSession session, AwpExecutionMaterial material,
        CancellationToken cancellationToken) => material switch
    {
        AwpDirectAgentExecutionMaterial direct => await ExecuteDirectAsync(session, direct, cancellationToken),
        AwpRootFlowExecutionMaterial => throw new AwpExecutionNotSupportedException(
            "flow_execution_pending", "External Flow orchestration is delivered by issue #662."),
        _ => throw new AwpExecutionNotSupportedException("material_kind_unsupported", "The execution material kind is unsupported.")
    };

    private async Task<JsonElement?> ExecuteDirectAsync(AwpAssignmentSession session,
        AwpDirectAgentExecutionMaterial material, CancellationToken cancellationToken)
    {
        var turn = (await session.Client.OpenTurnAsync(new(session.Context, material.RunId, ParticipantId: material.Agent.ParticipantId),
            Guid.NewGuid(), cancellationToken)).Turn;
        var location = new AwpExecutionLocation(material.RunId, AgentTurn: turn);
        await session.AppendEventAsync(AwpExecutionEventKind.TurnStarted, location, null, null, cancellationToken);

        var chatClient = new AwpModelChatClient(session, material.Agent, turn);
        var toolPipeline = new AwpToolExecutionPipeline(session, material.Agent, turn);
        var factory = new AgentFrameworkRuntimeFactory(new AwpChatClientResolver(chatClient), loggerFactory,
            new GenAiObservabilityOptions { Enabled = false });
        var definition = new ExecutableAgentDefinition
        {
            AgentId = material.Agent.AgentId,
            AgentKey = material.Agent.AgentName,
            DisplayName = material.Agent.DisplayName,
            Description = material.Agent.Description,
            AgentVersion = material.Agent.Generation,
            EffectiveInstructions = material.Agent.Instructions,
            ModelProfileName = material.Agent.ModelProfileName,
            ModelProfileNamespace = ResourceNamespace.Parse(material.Agent.ModelProfileNamespace),
            RuntimeProfileName = AwpRuntimeKinds.MicrosoftAgentFramework,
            EffectiveToolNames = material.Agent.Tools.Select(tool => tool.Id).ToArray(),
            MiddlewareIds = [],
            ContextProviderIds = [],
            Capabilities = [],
            Handler = material.Agent.Handler,
            DefinitionHash = material.Agent.DefinitionHash
        };
        var runtime = await factory.CreateAsync(definition, material.Agent.RevisionId,
            new AgentRuntimeContext(new AwpToolCatalog(material.Agent.Tools), toolPipeline), cancellationToken);
        var result = await runtime.ExecuteAsync(new AgentExecutionRequest(
            FormatInput(material),
            material.RunId,
            Execution: new AgentExecutionOptions { Streaming = RuntimeStreamingMode.Disabled },
            ToolExecution: new ToolExecutionScope
            {
                OwnerKind = ToolExecutionOwnerKind.RuntimeRun,
                TenantId = session.Assignment.Scope.TenantId,
                WorkspaceId = ParseWorkspace(session.Assignment.Scope.WorkspaceId),
                ExecutionId = material.RunId,
                AgentGeneration = material.Agent.Generation,
                PersistArguments = material.Execution.PersistToolArguments
            }), cancellationToken);
        if (!string.IsNullOrEmpty(result.Output))
            await session.AppendEventAsync(AwpExecutionEventKind.ResponseDelta, location,
                JsonSerializer.SerializeToElement(new { content = result.Output }), null, cancellationToken);
        await session.AppendEventAsync(AwpExecutionEventKind.TurnCompleted, location, null, null, cancellationToken);
        return JsonSerializer.SerializeToElement(new
        {
            response = result.Output,
            model = result.ModelName,
            inputTokens = result.Usage?.InputTokens,
            outputTokens = result.Usage?.OutputTokens
        });
    }

    private static string FormatInput(AwpDirectAgentExecutionMaterial material)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(material.Context)) builder.AppendLine(material.Context);
        foreach (var message in material.Messages)
            builder.Append(message.Role).Append(": ").AppendLine(message.Content);
        return builder.ToString().Trim();
    }

    private static WorkspaceId ParseWorkspace(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? new WorkspaceId(id)
        : throw new InvalidOperationException("The assignment Workspace identity is invalid.");
}

internal sealed class AwpExecutionNotSupportedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
