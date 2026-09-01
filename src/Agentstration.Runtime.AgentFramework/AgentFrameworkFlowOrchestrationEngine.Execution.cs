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
    public async IAsyncEnumerable<FlowExecutionEvent> ExecuteAsync(
        FlowOrchestrationExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var references = request.Definition.Pattern is MagenticOrchestrationPattern magentic
            ? request.Definition.Participants.Concat([magentic.Manager]).ToArray()
            : request.Definition.Participants;
        var participants = await ResolveParticipantsAsync(references, request.RuntimeBindings, ToolScope(request), cancellationToken);
        var bindings = references.Select(reference => participants[reference.Id].Binding).ToArray();
        yield return new FlowRuntimeBindingsResolved(bindings);
        var built = await BuildWorkflowAsync(request.Definition, participants, cancellationToken);
        var actorsByExecutorId = MapExecutors(built.Workflow, participants, built.Manager);
        var terminationPhrase = (request.Definition.Pattern as HandoffOrchestrationPattern)?.TerminationPhrase;
        var states = request.Definition.Participants.ToDictionary(
            participant => participant.Id,
            participant => new ParticipantState(participants[participant.Id], terminationPhrase),
            StringComparer.Ordinal);
        var nextTurn = 0;
        string? lastTurnParticipantId = null;
        var serializedTurns = request.Definition.Pattern is not ConcurrentOrchestrationPattern;
        List<ChatMessage>? finalMessages = null;
        var unsupportedOutputTypes = new HashSet<string>(StringComparer.Ordinal);

        CheckpointManager? checkpointManager = null;
        if (executionStates is not null)
            checkpointManager = CheckpointManager.CreateJson(new AgentFrameworkCheckpointStore(executionStates, request.WorkspaceId, timeProvider));
        await using var run = request.RuntimeState is null
            ? checkpointManager is null
                ? await InProcessExecution.RunStreamingAsync(
                    built.Workflow,
                    new List<ChatMessage> { new(ChatRole.User, Prompt(request.Input)) },
                    request.RunId,
                    cancellationToken)
                : await InProcessExecution.RunStreamingAsync(
                    built.Workflow,
                    new List<ChatMessage> { new(ChatRole.User, Prompt(request.Input)) },
                    checkpointManager,
                    request.RunId,
                    cancellationToken)
            : checkpointManager is null
                ? throw new FlowValidationException("flow_runtime_state_store_unavailable", "Durable runtime state storage is required to resume this orchestration.")
                : await InProcessExecution.ResumeStreamingAsync(
                    built.Workflow,
                    new CheckpointInfo(request.RunId, request.RuntimeState.StateId),
                    checkpointManager,
                    cancellationToken);
        if (request.RuntimeState is null)
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        await foreach (var workflowEvent in run.WatchStreamAsync(cancellationToken))
        {
            switch (workflowEvent)
            {
                case WorkflowErrorEvent error:
                    throw new FlowValidationException(
                        "flow_orchestration_framework_failed",
                        error.Exception?.GetBaseException().Message ?? "Microsoft Agent Framework reported an orchestration failure.");

                case RequestInfoEvent interaction:
                    if (request.AnsweredInput?.Response is not null
                        && string.Equals(request.AnsweredInput.RuntimeRequestId, interaction.Request.RequestId, StringComparison.Ordinal))
                    {
                        await run.SendResponseAsync(CreateResponse(interaction.Request, request.AnsweredInput));
                        break;
                    }
                    var sourceState = states.Values
                        .Where(value => value.Active is not null)
                        .OrderByDescending(value => value.Active!.Turn)
                        .FirstOrDefault();
                    var prompt = sourceState is not null
                        ? sourceState.Active?.Content.ToString()
                        : null;
                    for (var attempt = 0; checkpointManager is not null && run.LastCheckpoint is null && attempt < CheckpointAvailabilityPollAttempts; attempt++)
                        await Task.Delay(CheckpointAvailabilityPollInterval, timeProvider, cancellationToken);
                    if (checkpointManager is null || run.LastCheckpoint is null)
                        throw new FlowValidationException(
                            "flow_orchestration_interaction_not_durable",
                            "The orchestration requested external input before durable runtime state was available.");
                    var interactionDescription = DescribeInteraction(interaction.Request, prompt);
                    yield return new FlowExternalInputRequested(
                        interaction.Request.RequestId,
                        interactionDescription.Prompt,
                        interactionDescription.Type,
                        [],
                        sourceState?.Participant.Id,
                        new DurableRuntimeStateReference(
                            AgentFrameworkCheckpointStore.RuntimeType,
                            run.LastCheckpoint.CheckpointId,
                            timeProvider.GetUtcNow()));
                    yield break;

                case AgentResponseUpdateEvent update:
                    var actor = ResolveActor(update.Update.AgentId, update.ExecutorId, participants, built.Manager, actorsByExecutorId);
                    if (actor.IsManager) break;
                    if (serializedTurns)
                    {
                        foreach (var previousState in states.Values.Where(value => value.Active is not null && value.Participant.Id != actor.Id).OrderBy(value => value.Active!.Turn))
                        {
                            var previousTurn = previousState.CompleteActive();
                            if (!string.IsNullOrEmpty(previousTurn.Delta))
                                yield return new FlowParticipantDelta(previousState.Participant.Id, previousTurn.Delta);
                            yield return new FlowParticipantTurnCompleted(previousState.Participant.Id, previousTurn.Turn.Turn);
                        }
                    }
                    var state = states[actor.Id];
                    var responseId = ResponseKey(update.Update.ResponseId, update.Update.MessageId, state.Active?.ResponseId);
                    if (state.IsCompleted(responseId))
                    {
                        state.Capture(update.Update.Contents);
                        break;
                    }
                    if (state.Active is null)
                    {
                        if (request.Definition.Pattern is HandoffOrchestrationPattern
                            && lastTurnParticipantId is not null
                            && !string.Equals(lastTurnParticipantId, actor.Id, StringComparison.Ordinal))
                            yield return new FlowParticipantHandoff(lastTurnParticipantId, actor.Id);
                        lastTurnParticipantId = actor.Id;
                        state.Start(++nextTurn, responseId);
                        yield return new FlowParticipantTurnStarted(actor.Id, nextTurn);
                    }
                    else
                    {
                        state.Correlate(responseId);
                    }
                    state.Capture(update.Update.Contents);
                    if (!string.IsNullOrEmpty(update.Update.Text))
                    {
                        var delta = state.Append(update.Update.Text);
                        if (!string.IsNullOrEmpty(delta))
                            yield return new FlowParticipantDelta(actor.Id, delta);
                    }
                    if (update.Update.FinishReason is not null)
                    {
                        var completed = state.CompleteActive();
                        if (!string.IsNullOrEmpty(completed.Delta))
                            yield return new FlowParticipantDelta(actor.Id, completed.Delta);
                        yield return new FlowParticipantTurnCompleted(actor.Id, completed.Turn.Turn);
                    }
                    break;

                case AgentResponseEvent response:
                    actor = ResolveActor(response.Response.AgentId, response.ExecutorId, participants, built.Manager, actorsByExecutorId);
                    if (actor.IsManager) break;
                    if (serializedTurns)
                    {
                        foreach (var previousState in states.Values.Where(value => value.Active is not null && value.Participant.Id != actor.Id).OrderBy(value => value.Active!.Turn))
                        {
                            var previousTurn = previousState.CompleteActive();
                            if (!string.IsNullOrEmpty(previousTurn.Delta))
                                yield return new FlowParticipantDelta(previousState.Participant.Id, previousTurn.Delta);
                            yield return new FlowParticipantTurnCompleted(previousState.Participant.Id, previousTurn.Turn.Turn);
                        }
                    }
                    state = states[actor.Id];
                    responseId = ResponseKey(response.Response.ResponseId, null, state.Active?.ResponseId);
                    if (state.IsCompleted(responseId))
                    {
                        state.MergeUsage(response.Response.Usage);
                        break;
                    }
                    if (state.Active is null)
                    {
                        if (request.Definition.Pattern is HandoffOrchestrationPattern
                            && lastTurnParticipantId is not null
                            && !string.Equals(lastTurnParticipantId, actor.Id, StringComparison.Ordinal))
                            yield return new FlowParticipantHandoff(lastTurnParticipantId, actor.Id);
                        lastTurnParticipantId = actor.Id;
                        state.Start(++nextTurn, responseId);
                        yield return new FlowParticipantTurnStarted(actor.Id, nextTurn);
                    }
                    else
                    {
                        state.Correlate(responseId);
                    }
                    state.Capture(response.Response.Messages.SelectMany(message => message.Contents));
                    if (state.Active!.Content.Length == 0 && !string.IsNullOrEmpty(response.Response.Text))
                    {
                        var delta = state.Append(response.Response.Text);
                        if (!string.IsNullOrEmpty(delta))
                            yield return new FlowParticipantDelta(actor.Id, delta);
                    }
                    state.MergeUsage(response.Response.Usage);
                    var responseTurn = state.CompleteActive();
                    if (!string.IsNullOrEmpty(responseTurn.Delta))
                        yield return new FlowParticipantDelta(actor.Id, responseTurn.Delta);
                    yield return new FlowParticipantTurnCompleted(actor.Id, responseTurn.Turn.Turn);
                    break;

                case WorkflowOutputEvent completed when !completed.IsIntermediate() && completed.Is<List<ChatMessage>>():
                    finalMessages = completed.As<List<ChatMessage>>();
                    break;
                case WorkflowOutputEvent completed when !completed.IsIntermediate():
                    unsupportedOutputTypes.Add(completed.Data?.GetType().FullName ?? "<null>");
                    break;
            }
        }

        foreach (var state in states.Values.Where(value => value.Active is not null).OrderBy(value => value.Active!.Turn))
        {
            var completed = state.CompleteActive();
            if (!string.IsNullOrEmpty(completed.Delta))
                yield return new FlowParticipantDelta(state.Participant.Id, completed.Delta);
            yield return new FlowParticipantTurnCompleted(state.Participant.Id, completed.Turn.Turn);
        }

        var participantResults = request.Definition.Participants
            .Select(participant => states[participant.Id])
            .Where(state => state.Turns.Count > 0)
            .Select(state => state.ToResult())
            .ToArray();
        foreach (var participantResult in participantResults)
            yield return new FlowParticipantCompleted(participantResult);

        if (finalMessages is null && participantResults.Length == 0)
            throw new FlowValidationException(
                "flow_orchestration_output_invalid",
                unsupportedOutputTypes.Count == 0
                    ? "The orchestration returned no supported terminal output."
                    : $"The orchestration returned unsupported terminal output: {string.Join(", ", unsupportedOutputTypes)}.");

        var finalOutput = SelectFinalOutput(request.Definition.Strategy, finalMessages ?? [], participantResults, terminationPhrase);
        yield return new FlowExecutionCompleted(new FlowOrchestrationResult(
            request.Definition.Strategy,
            finalOutput,
            participantResults));
    }

    private static JsonElement SelectFinalOutput(
        FlowOrchestrationStrategy strategy,
        IReadOnlyList<ChatMessage> finalMessages,
        IReadOnlyList<FlowParticipantResult> participants,
        string? terminationPhrase)
    {
        if (strategy == FlowOrchestrationStrategy.Concurrent)
            return JsonSerializer.SerializeToElement(participants.Select(result => new
            {
                result.ParticipantId,
                Output = result.Output
            }).ToArray(), JsonOptions);
        var text = finalMessages
            .Where(message => message.Role == ChatRole.Assistant)
            .Select(message => VisibleHandoffText(message.Text, terminationPhrase))
            .LastOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? participants
                .SelectMany(participant => participant.Turns)
                .Select(turn => VisibleHandoffText(turn.Content, terminationPhrase))
                .LastOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;
        return JsonSerializer.SerializeToElement(text);
    }

    private static string VisibleHandoffText(string? text, string? terminationPhrase)
    {
        text ??= string.Empty;
        return string.IsNullOrWhiteSpace(terminationPhrase)
            ? text
            : text.Replace(terminationPhrase, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
    }

    private static bool IsInternalOrchestrationTool(string name) =>
        name.StartsWith("handoff_to_", StringComparison.Ordinal);

    private static string ResponseKey(string? responseId, string? messageId, string? activeResponseId) =>
        !string.IsNullOrWhiteSpace(responseId) ? responseId
        : !string.IsNullOrWhiteSpace(messageId) ? messageId
        : !string.IsNullOrWhiteSpace(activeResponseId) ? activeResponseId
        : $"response-{Guid.NewGuid():N}";

    private static string Prompt(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty("prompt", out var prompt)
        && prompt.ValueKind == JsonValueKind.String
            ? prompt.GetString()!
            : input.GetRawText();
}

