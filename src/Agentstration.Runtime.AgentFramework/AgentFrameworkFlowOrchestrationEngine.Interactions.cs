using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Runtime.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agentstration.Runtime.AgentFramework;

public sealed partial class AgentFrameworkFlowOrchestrationEngine
{
    internal static ExternalResponse CreateResponse(ExternalRequest request, InputRequest input)
    {
        var value = input.Response!.Value;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        if (request.TryGetDataAs<IExternalRequestEnvelope>(out var envelope) && envelope is not null)
        {
            var inner = envelope.GetInnerRequestContent();
            AIContent? approvalResponse = value.ValueKind is JsonValueKind.True or JsonValueKind.False
                && inner is ToolApprovalRequestContent approval
                    ? approval.CreateResponse(value.GetBoolean())
                    : null;
            IList<ChatMessage> messages = approvalResponse is null
                ? [new ChatMessage(ChatRole.User, text)]
                : [new ChatMessage(ChatRole.User, [approvalResponse])];
            return request.CreateResponse(envelope.CreateResponse(messages));
        }
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && request.TryGetDataAs<ToolApprovalRequestContent>(out var directApproval)
            && directApproval is not null)
            return request.CreateResponse(directApproval.CreateResponse(value.GetBoolean()));
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return request.CreateResponse(value.GetBoolean());
        if (request.IsDataOfType<ChatMessage>())
            return request.CreateResponse(new ChatMessage(ChatRole.User, text));
        if (request.IsDataOfType<List<ChatMessage>>())
            return request.CreateResponse(new List<ChatMessage> { new(ChatRole.User, text) });
        return request.CreateResponse(text);
    }

    internal static InteractionDescription DescribeInteraction(ExternalRequest request, string? participantPrompt)
    {
        if (request.TryGetDataAs<ToolApprovalRequestContent>(out var approval) && approval is not null)
            return new(InputRequestType.Confirmation, "Approve the requested tool operation?");
        if (request.TryGetDataAs<IExternalRequestEnvelope>(out var envelope)
            && envelope?.GetInnerRequestContent() is ToolApprovalRequestContent)
            return new(InputRequestType.Confirmation, "Approve the requested tool operation?");
        return new(
            InputRequestType.Text,
            string.IsNullOrWhiteSpace(participantPrompt)
                ? "Additional input is required to continue this execution."
                : participantPrompt);
    }
}

