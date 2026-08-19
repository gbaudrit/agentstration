# ADR-0059: Tool arguments require explicit bounded retention

## Status

Accepted

## Context

ADR-0058 deliberately excluded Tool payloads from durable governance facts to minimize the sensitive-data surface. Operators nevertheless need to diagnose the concrete invocation selected by an Agent, including the arguments sent to the provider. Tool arguments can contain credentials, personal data or large values, and retaining them implicitly would be unsafe.

## Decision

Tool argument retention is a host-level, explicit opt-in. The standalone host reads:

```json
{
  "Agentstration": {
    "ToolExecution": {
      "PersistArguments": false,
      "MaximumArgumentsLength": 16384
    }
  }
}
```

`PersistArguments` defaults to `false`. When disabled, Runtime and Flow lifecycle projections retain invocation identities, governance decisions and outcomes without arguments. When enabled, the provider-neutral JSON arguments from `ToolExecutionContext` are copied into the existing durable Runtime Run and Flow Run journals. Captured text is bounded by `MaximumArgumentsLength`; an oversized value is marked as truncated.

A manual Runtime Run may persist a nullable `PersistToolArguments` override in its immutable execution options. `null` inherits the host default, `true` retains arguments for that Run, and `false` suppresses retention for that Run. The Console exposes these three choices under Advanced Run. The host's maximum length remains a hard bound. Retries copy the original execution options and therefore preserve the original choice.

The Tool Governance read model exposes captured arguments for the matching physical invocation and otherwise states that they were not retained. Provider results remain excluded. Changing the setting affects only future lifecycle facts and never reconstructs payloads for existing Runs.

This decision amends only ADR-0058's unconditional exclusion of arguments. Its per-attempt identity, fail-closed governance projection and exclusion of provider results and human-readable denial messages remain unchanged.

## Consequences

- Safe local and production defaults do not retain Tool arguments.
- Operators can explicitly trade a larger sensitive-data surface for invocation diagnostics globally or for one manual Runtime Run.
- Runtime Run and Flow Run paths use the same option and bounded capture behavior.
- The option does not mutate arguments, change provider invocation or provide DLP/PII redaction.
- Fine-grained per-Workspace or per-Tool retention, access tiers, retention periods and payload encryption policies remain future work.
