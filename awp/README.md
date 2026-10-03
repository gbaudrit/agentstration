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
