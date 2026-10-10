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
- `/assignments/model/invoke` and `/assignments/tools/invoke` keep model credentials, Agent Tool authorization, governance hooks and audit inside Agentstration. Agent Tool calls must reference a server-authorized Turn/TurnAttempt and a Tool declared by that Agent revision.
- `/assignments/flow-tools/invoke` executes a direct `Tool` or resolves and executes a `ToolRoute` from the immutable Flow snapshot. Agentstration returns the effective Tool, provider and ToolSet-route identities for durable projection.
- `/assignments/events` accepts at most 256 ordered events per call. Attempt-local sequences are contiguous, EventIds are stable across replay, identical duplicates are accepted idempotently, and conflicting replays are rejected.
- `/assignments/checkpoints` persists only explicitly supplied checkpoints with a schema version and compatibility key equal to the assignment material digest. A checkpoint is not an automatic recovery promise after Worker loss.
- `/assignments/child-flows` accepts only a server-authorized `Flow`, bounded `Repeat`, or Artifact-storage child for the immutable StepDefinition. The server creates the deterministic durable child Run and returns its pinned v1 material; the owning Worker executes the complete child tree under the root assignment.
- `/assignments/flow-artifacts/capture` resolves the step's declared `artifactOutput` and performs governed staging inside Agentstration. `/assignments/flow-artifacts/cleanup` applies the declared cleanup policy through the same authoritative Artifact service. Generic `/assignments/artifacts` operations continue to use opaque IDs and bound content to 1 MiB.
- `/assignments/complete` and `/assignments/fail` apply the terminal command idempotently.

Every operation revalidates the Workspace, Worker session, AssignmentAttempt, opaque token, lease and fencing generation at the authoritative boundary. New governed side effects are rejected during the configured lease safety margin; an operation already in progress is cancelled before lease expiry. Read-only and durable mutation operations never expose a generic Management or storage API.
