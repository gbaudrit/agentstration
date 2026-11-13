using Agentstration.Flows;

namespace Agentstration.Flows.Storage.Abstractions;

/// <summary>Separates the ephemeral direct-input payload from durable execution metadata.</summary>
public interface IFlowPayloadPersistence
{
    FlowRun Store(FlowRun run);
    FlowRun Restore(FlowRun run);
    FlowRunEvent Store(FlowRunEvent value);
    FlowRunEvent Restore(FlowRunEvent value);
    InputRequest Store(InputRequest value);
    InputRequest Restore(InputRequest value);
}
