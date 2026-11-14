# Agentstration Worker Protocol

AWP is the provider-neutral, pull-based protocol used by autonomous Runtime Workers to execute complete Runs while Agentstration remains authoritative for durable state and ownership.

`Agentstration.Awp.Abstractions` contains the AWP v1 wire contracts only. It deliberately has no reference to Microsoft Agent Framework, Agentstration Runtime or Flow implementations, storage, ASP.NET Core, Kubernetes, or another hosting technology.

## Version dimensions

AWP keeps four version dimensions independent:

- `AwpProtocol.Version` identifies the wire contract. Registration advertises supported protocol versions and Agentstration selects an exact common version.
- `AwpRuntimeCapability.CapabilityVersion` identifies the contract of a Runtime family such as `microsoft-agent-framework`.
- `ExecutionMaterialVersions` identifies the immutable material schemas a Worker can consume. An assignment is eligible only when its material version is advertised.
- `SoftwareVersion` and `RuntimeImplementationVersion` identify Worker and Runtime implementation builds for diagnostics. They do not require every compatible Worker to run the same binary or Microsoft Agent Framework version.

The v1 serializer accepts additive unknown JSON properties. Unknown enum values, target discriminators, invalid identifiers, and unsupported Turn attempt numbers are rejected.

## Execution identity

The v1 placement target is either a direct Agent Runtime Run or a root Flow Run. Flow events use server-authorized effective identities:

```text
Assignment
└── AssignmentAttempt
    └── StepExecution
        └── Turn
            └── TurnAttemptNumber = 1
                └── Events / ToolCalls
```

One Agent StepExecution may contain several Turns. Every opened Turn has an initial TurnAttempt numbered `1`; AWP v1 does not create or accept attempt `2`. AssignmentAttempt is the separate lease/fencing ownership scope and must not be confused with a TurnAttempt.

The ownership token is opaque. Every assignment-scoped command also carries the Worker, Worker session, Assignment, AssignmentAttempt, and fencing generation so the authoritative server can reject expired or superseded ownership.

## Dispatch transport

An authenticated process activates its fresh session, registers capacity and compatibility at `POST /api/awp/v1/workers/register`, then calls `POST /api/awp/v1/assignments/claim`. Claim performs an immediate durable eligibility check before any bounded wait and rechecks after every best-effort wake-up. `POST /api/awp/v1/assignments/heartbeat` renews only the exact assignment, attempt, Worker session, opaque ownership token and fencing generation returned by claim. Assignment command contexts include the Workspace scope required by the authoritative storage boundary.

## Assignment-scoped execution boundary

After claim, the Worker retrieves the immutable, versioned material referenced by the assignment through `/assignments/material`. Direct-Agent material contains the exact Agent revision, instructions, model-profile identity, declared Tool schemas, input and execution options. Root-Flow material additionally contains the pinned published Flow snapshot and its version/hash. It never contains a database connection, Secret value, provider credential, artifact storage key or filesystem path.

The remaining AWP v1 operations are deliberately narrow:

- `/assignments/steps/open` validates a `StepDefinitionId` against the pinned Flow snapshot and mints the effective `StepExecutionId`.
- `/assignments/turns/open` mints a Turn and its single v1 `TurnAttempt` under an optional StepExecution.
- `/assignments/model/invoke`, `/assignments/model/stream`, and `/assignments/tools/invoke` keep model credentials, Agent Tool authorization, governance hooks and audit inside Agentstration. Agent Tool calls must reference a server-authorized Turn/TurnAttempt and a Tool declared by that Agent revision. The streaming model operation uses authenticated `POST` plus `text/event-stream`, with one stable stream identity, contiguous sequence numbers, typed text/Tool/usage events, and an explicit completed or failed terminal event. Its transport mechanics intentionally follow the proven OpenAI Responses streaming shape, but the payload remains AWP-native: assignment ownership, Workspace scope, participant, Turn and TurnAttempt stay explicit while model and Tool resolution remain server-side. It is not an OpenAI-compatible `/v1/responses` facade.
- `/assignments/flow-tools/invoke` executes a direct `Tool` or resolves and executes a `ToolRoute` from the immutable Flow snapshot. Agentstration returns the effective Tool, provider and ToolSet-route identities for durable projection.
- `/assignments/events` accepts at most 256 ordered events per call. Attempt-local sequences are contiguous, EventIds are stable across replay, identical duplicates are accepted idempotently, and conflicting replays are rejected.
- `/assignments/checkpoints` persists only explicitly supplied checkpoints with a schema version and compatibility key equal to the assignment material digest. A checkpoint is not an automatic recovery promise after Worker loss.
- `/assignments/child-flows` accepts only a server-authorized `Flow`, bounded `Repeat`, Artifact-storage child, or an authority-extracted process region for the immutable StepDefinition. The server creates the deterministic durable child Run and returns its pinned v1 material; the owning Worker executes the complete child tree under the root assignment.
- `/assignments/artifact-processing` opens, prepares, finalizes or aborts one indexed input of an assigned `artifactTransform` step. It uses the ordinary assignment ownership proof and StepExecution coordinates. The authority owns exact input access, draft revisions and final Artifact publication; processing child snapshots and inherited content capabilities cannot be supplied by the Worker. See [Artifact transformations](../docs/reference/artifact-transformations.md).
- `/assignments/flow-artifacts/capture` resolves the step's declared `artifactOutput` and performs governed staging inside Agentstration. `/assignments/flow-artifacts/cleanup` applies the declared cleanup policy through the same authoritative Artifact service. Generic `/assignments/artifacts` operations continue to use opaque IDs and bound content to 1 MiB.
- `/assignments/complete` and `/assignments/fail` apply the terminal command idempotently.

Every operation revalidates the Workspace, Worker session, AssignmentAttempt, opaque token, lease and fencing generation at the authoritative boundary. New governed side effects are rejected during the configured lease safety margin; an operation already in progress is cancelled before lease expiry. Read-only and durable mutation operations never expose a generic Management or storage API.

The authority bounds streaming responses by event count, per-event size, total bytes, and Tool-argument size. The Worker validates UTF-8, envelope identity, stream identity, exact ordering, Tool-call lifecycle and terminal completion before accepting a stream. Lease renewal and revocation remain active for the full enumeration lifetime; a disconnected consumer cancels the in-flight provider call instead of transferring or extending ownership.


### Effective data-contract context

Agent execution material carries optional `inputSchema`, `outputSchema` and
`outputContract` metadata. Root/child Flow material can carry `contractContext`
with a concrete input contract, final output expectation, `artifactDraft` and
`finalResultMapped` flags. The authority derives this context from the accepted
Artifact processing session; workers cannot select another session's contract.
It is separate from business input and never changes the Agent revision.

`StepCompleted` can include concrete `contracts` evidence. Safe contract failures
include structured `errorDetails`; failed assignment requests can carry the same
structure in `failure.details`. The authority returns a bounded `contractFailure`
Problem Details extension for HTTP 422 contract errors. Preserve this cause across
child execution and root completion; do not replace it with only `FLOW_FAILED`.
The extension contains types, schema paths and execution coordinates, never data
values. Existing consumers can omit optional metadata; current workers enforce
contracts supplied for an invocation.

Problem Details includes the machine-readable `code` and its `errorCode` alias.
HTTP diagnostics can retain `errorCode` while redacting generic `code` fields
used by authentication protocols. Tool input validation failures return HTTP 422
with their schema path in `contractFailure`, rather than an unhandled HTTP 500.

### Implicit process admission

`/assignments/child-flows` additionally accepts purpose `process` for a step that
explicitly declares a region. `processUnit` supplies a stable `key` (1–128 characters),
zero-based `ordinal`, optional metadata-only `descriptor` (at most 256 characters)
and optional bounded `totalUnits`. `iteration` must equal the ordinal. The authority
derives definition origin, graph, execution scope and owning step attempt from the
accepted snapshot and authenticated StepExecution; no caller-supplied graph or
parent origin is accepted. Existing `artifactProcessing` admission remains supported.

`/assignments/process/close` accepts the assignment context and `stepExecutionId`.
It explicitly closes admission for that owner attempt, including empty work sets.
Before closure a missing total means still-discovering, not zero. New admissions
after closure fail; exact existing units can still be replayed. New Workers close
admission before reporting successful step completion. Legacy Artifact callers without
new unit metadata retain compatible completion behavior.

Child responses include optional terminal result/error metadata and the resolved Flow
identity, namespace and version, preserving artifact provenance during replay. A terminal
response has no executable material and must be reused without starting the child again.
Root/child material can include authority-derived `processOrigin`; named sub-Flows
inside a process do not inherit that origin. `StepCompleted` / `RunCompleted` may
include `errorOrigin` (run, step and unit). The projection validates references against
actual same-scope descendant failure evidence before exposing causal links.
`ProcessUnitChanged` notifications refresh owner/root inspection from persisted state;
reconnect also reloads authoritative state independently of notification replay.

Definitions and units reuse existing AWP ownership, cancellation, nesting and root
descendant limits. Optimistic root reservations prevent concurrent admission exceeding
the descendant budget. Failed admission may conservatively retain a reservation until
the root ends. Derived children remain on the owning Worker; they are not independently
placed assignments. See [ADR-0215](../docs/decisions/0215-process-regions-have-derived-flow-identities.md).

New root graphs containing process regions, named/repeated child calls or Artifact
capture use execution-material schema `1.1`. Current Workers advertise `1.0` and
`1.1`; a `1.0`-only Worker cannot claim these new assignments. Existing `1.0`
assignments keep their immutable references and legacy terminal-child response shape.
Resolving a new generic process or explicitly parallel Artifact process through an
old assignment fails explicitly instead of silently treating it as an ordinary
mapping. Start a new run with an updated Worker in that case. Wire protocol and
Runtime capability versions remain `1.0`; these are separate version dimensions.
