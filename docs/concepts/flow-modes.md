# Flow modes

Agentstration exposes five Flow kinds through the `flowKind` JSON discriminator. A Flow kind defines the execution model; an Orchestration strategy only defines how the agents inside an Orchestration cooperate.

## Choosing a mode

| Flow kind | Use it when | Execution owner | Current execution status |
| --- | --- | --- | --- |
| `direct` | Exactly one known Agent must handle the input. | Local Flow executor through the neutral agent port | Executable |
| `routing` | One Agent must be selected from several candidates. | Local Flow executor | Executable with deterministic local selection |
| `workflow` | The process is an explicit graph of typed steps and transitions. | Provider-neutral graph executor | Executable when the published version contains a `FlowGraphDefinition` |
| `orchestration` | Several Agents must cooperate dynamically. | Neutral orchestration port, implemented by the isolated MAF adapter | Executable for all five built-in strategies |
| `composite` | A higher-level Flow references versioned child Flows. | Future Flow composition executor | Declarative only; execution is not implemented yet |

Use Workflow when the application owns the path. Use Orchestration when agents own part of the collaboration decision. Routing chooses one Agent; Orchestration coordinates several Agents.

## Behavior shared by every Flow

- A mutable Flow definition is protected by an ETag.
- Publication creates an immutable semantic version.
- A `FlowReference` selects an exact version or the active version without embedding the definition.
- A FlowRun stores the exact resolved definition snapshot, input, steps, events, output, error, and correlation identifier.
- Draft Runs store the exact draft revision and graph snapshot used for execution.
- Cancellation and terminal states belong to Flow, independently of Runtime agent Runs.

## Console topology

The Console visualizer represents the declared execution topology rather than forcing every Flow into a sequence. Workflow transitions retain their event and condition labels. Routing displays candidate branches, Concurrent orchestration uses fan-out and fan-in, Handoff displays its directed routes, Group Chat uses a shared-conversation hub, and Magentic keeps its dedicated manager visually distinct from participants.

Definition diagrams and Run diagrams answer different questions:

- declared edges describe every route allowed by the immutable definition snapshot;
- observed edges highlight a transition or participant transfer that occurred in the Run;
- inactive edges identify alternatives that were available but not selected;
- node status and turn counts describe execution state without exposing MAF executor identifiers or internal manager traffic.

The visual projection remains provider-neutral. MAF-specific workflow events are normalized into the same Flow step and participant events used by the durable FlowRun contract before the Console sees them.

### Namespaced published definitions

Flows installed from a Pack remain owned by their namespace. Their active published version can be opened from the Flow details page in the same Designer and orchestration views as workspace Flows, but those views are read-only: navigation, zoom, node selection, topology, source, participants, and strategy remain available while Draft creation, autosave, Save, Publish, and Draft Runs are disabled. The Designer reads the immutable active version directly and renders its graph as canonical YAML without materializing a workspace Draft.

A legacy published Workflow version without a stored `FlowGraphDefinition` cannot be reconstructed safely. The Designer reports that case explicitly instead of creating or inferring a Draft. Workspace Flow authoring retains its existing editable Draft behavior.

## Direct

A Direct Flow invokes exactly one Agent target. It is appropriate for stable bindings such as “all SQL review requests go to `sql-expert`”. The target must be an Agent; Direct cannot target another Flow.

```json
{
  "flowKind": "direct",
  "target": {
    "kind": "agent",
    "id": "sql-expert"
  }
}
```

Execution creates the logical `Input`, `Agent`, and `Output` steps. The Agent step records the resolved agent version, model profile, provider, usage, tools, and output when available.

Agentstration also creates hidden system-managed Direct Flows for Workplace Entries bound directly to an Agent. They use the same publication, FlowRun, event, and output contracts as user-authored Direct Flows.

## Routing

A Routing Flow selects one destination from a non-empty list and can declare an optional fallback. It does not execute every candidate.

The contract defines `Deterministic`, `Capabilities`, `Semantic`, `Llm`, `Hybrid`, and `Custom` routing strategies. The current standalone executor implements deterministic local selection: it looks for a candidate identifier or a meaningful identifier fragment in the input, then uses the fallback, then the first destination. The other strategy names are reserved contract vocabulary and do not yet have distinct runtime algorithms.

Execution creates `Input`, `Router`, `Agent`, and `Output` steps. The Router step persists the selected destination so the decision is visible in the FlowRun.

Choose Routing instead of Orchestration when exactly one Agent should run.

## Workflow

A Workflow models an application-controlled graph. The executable representation is `FlowGraphDefinition`, containing one entry step, typed steps, transitions, optional input/output schemas, and designer metadata.

### Executable step types

| Step | Responsibility | Main emitted transition event |
| --- | --- | --- |
| `input` | Exposes and optionally validates the FlowRun input. | `completed` |
| `agent` | Invokes one governed Agent with optional input mapping and instructions. | `completed` or `failed` |
| `router` | Selects a declared candidate or fallback. | `selected` or `failed` |
| `condition` | Evaluates a constrained simple condition or expression. | `true` or `false` |
| `transform` | Produces a mapped or expression-derived value. | `completed` |
| `output` | Maps the terminal Flow output. | `completed` |
| `failure` | Terminates the Run with a declared code and message. | Terminal failure |

Transitions connect `fromStep`, event, and `toStep`; an optional condition and priority refine selection. Execution is bounded and rejects a step reached twice, so cycles are not currently supported. Business logic remains in application services and agents, not in the designer or transport endpoints.

The polymorphic `WorkflowFlowDefinition` contract also exposes generic nodes and edges. A directly-created Workflow definition without a stored `FlowGraphDefinition` is structurally valid but is not executable by the current FlowRun engine. The Console draft/publish path stores the executable graph and is the supported Workflow execution path.

Choose Workflow when ordering, branching, transformations, and failure paths must be deterministic and reviewable before execution.

## Orchestration

An Orchestration declares at least two distinct Agent participants and one typed strategy. Participants are provider-neutral references. Microsoft Agent Framework objects, executor identifiers, manager traffic, and internal handoff tools stay inside `Agentstration.Runtime.AgentFramework`.

Every successful orchestration returns a normalized result:

- `strategy`;
- `finalOutput`;
- ordered `participants`;
- for each participant: its turns, output, resolved agent/version/model metadata, user tools, and usage when supplied.

The FlowRun persists one step per declared participant. Participants that never run are marked `Skipped`. The whole orchestration has a server-side timeout and emits the explicit `TimedOut` state/event when it expires.

### Sequential

Participants run in declaration order. Each participant receives context produced before its turn.

- `includeFullHistory: true` passes the full accumulated conversation to the next Agent.
- `includeFullHistory: false` chains only the preceding Agent responses and makes the last Agent response the terminal workflow output.
- The normalized `finalOutput` is the last non-empty terminal message, with the last participant turn as fallback.

Use Sequential for pipelines in which each Agent refines, verifies, translates, or enriches the previous result.

### Concurrent

Every participant receives the same initial input and runs independently. No participant sees another participant's result during that Run.

- Execution can overlap between participants.
- Results are normalized back into declaration order, independently of completion order.
- `finalOutput` is an array containing each participant identifier and output; it is not an arbitrary concatenated string.

Use Concurrent for independent analyses, voting inputs, or multiple perspectives that will be consumed together.

### Handoff

Handoff starts with `initialParticipant`. An Agent may transfer control only through a declared directed route `{ from, to }`.

- Every participant must be reachable from the initial participant.
- Self-routes and duplicate routes are rejected.
- `autonomous: true` lets Agents continue transferring control without user input.
- `maximumTurnsPerParticipant` is validated between 1 and 50.
- An optional `terminationPhrase` can stop the autonomous conversation.
- MAF-generated `handoff_to_*` tools are internal mechanics and are never persisted as user tools.

Durable interactive response/resume is not implemented. An orchestration request for external information fails explicitly instead of leaving the FlowRun indefinitely active. Use autonomous Handoff for unattended execution today.

Use Handoff when the currently active specialist should decide which declared specialist owns the next turn.

### Group Chat

Group Chat maintains a shared conversation and selects speakers through a round-robin manager.

- Each Agent sees the accumulated conversation, including earlier participant responses.
- Turns are observable as participant started, delta, and completed events.
- `maximumIterations` is validated between 1 and 100 and bounds the conversation.
- The normalized `finalOutput` is the last non-empty terminal message.

Use Group Chat for bounded collaborative discussion where every participant should contribute in a predictable order.

### Magentic

Magentic uses a dedicated manager Agent to build a plan, select the next participant, assess progress, recover from stalls, and produce the final answer.

- The manager must be distinct from participants.
- Manager calls and output are orchestration internals; the manager is not exposed as a participant.
- Manager tools are rejected because the current MAF manager contract does not support them safely.
- `maximumRounds` is validated between 1 and 50.
- `maximumStalls` is validated between 1 and 10.
- `maximumResets` is validated between 0 and 5.
- Current execution is autonomous and disables plan sign-off.

A future interactive Magentic mode is intentionally reserved. It requires neutral persisted interaction requests, authorization, timeout policy, checkpoint identity, and resume semantics before plan review can be exposed through API or UI. Until then, an unexpected interaction request fails with `flow_orchestration_interaction_unsupported`.

Use Magentic for open-ended tasks where a manager must adapt the plan and decide dynamically which specialist acts next.

## Composite

A Composite Flow references immutable or active child `FlowReference` values and declares `Sequential`, `Concurrent`, or `Custom` composition intent. It never embeds child definitions. Direct self-reference is rejected.

Composite execution, indirect cycle detection, child-run correlation, failure aggregation, and retry semantics are not implemented yet. Composite should therefore be treated as a declarative contract, not as an executable Flow mode in the current release.

## Current limits

| Limit | Value |
| --- | ---: |
| Orchestration participants | 16 |
| Group Chat iterations | 100 |
| Handoff turns per participant | 50 |
| Handoff termination phrase | 256 characters |
| Magentic rounds | 50 |
| Magentic stalls | 10 |
| Magentic resets | 5 |

These are validation maxima. The FlowRun orchestration timeout provides an additional global execution boundary.

## Related documentation

- [Flow concepts](flows.md)
- [FlowRun concepts](flow-runs.md)
- [Flow execution architecture](../architecture/flow-execution.md)
- [Flow definitions and API boundary](../flow.md)
- [ADR-0034: seal MAF Flow orchestration behind the runtime adapter](../decisions/0034-seal-maf-flow-orchestration-behind-runtime-adapter.md)
