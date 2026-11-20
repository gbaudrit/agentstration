# CAR-1246: Bootstrap saves Agents without preparing executable revisions

## Status

Open — 2026-11-20

## References

- Issue: [#1246](https://github.com/gbaudrit/agentstration/issues/1246)
- Introducing integration: [c8e26ec1](https://github.com/gbaudrit/agentstration/commit/c8e26ec1) added the Ollama bootstrap example and its resource/policy regression test. The original introducing change for the generic Agent bootstrap handler is Unknown.
- Corrective pull request: Not opened; correction is being prepared on `feat/bootstrapping`.
- Related ADRs: ADR-0240 and ADR-0242 for phased startup/readiness; no new runtime architecture is introduced.

## Defect

The unattended Ollama example persists Agent generation 1 without its immutable
executable revision. The Console displays an unapplied generation and requires a
manual Apply generation action after Bootstrap otherwise succeeds.

## Detection

- Stage: Maintainer validation of the published feature branch.
- Detection mechanism: The maintainer supplied an Agent overview screenshot after
  making the required Ollama model available.
- Why it was not detected earlier: The example regression asserted Agent existence,
  model references, scopes and access policies, but not executable revision presence.

## Causal analysis

### Faulty approach

The AI-assisted integration treated successful persistence of the desired Agent
definition as the completion of unattended Agent setup. The handler returned
Created after PutAgentAsync, and skipped an already saved Agent on retry.

### Contributing assumptions

Saving a valid definition was assumed to perform the preparation done by the
Console's Apply generation action. These are separate use cases.

### Missed signals

AgentRuntimePreparationService delegates preparation to
AgentManagementService.EnsureExecutionRevisionAsync. The Console derives its
active-generation display from executable readiness, rather than resource
existence alone. The example's complete-bootstrap claim needed to cover this step.

### Safeguard gap

The offline example test checked only the governed definition and access overlays.
The real-process enrollment smoke likewise checked resource presence; it did not
assert an immutable executable revision without first creating one as a side effect.

## Resolution

Use the existing Agent family preparation boundary immediately after Bootstrap
creates an Agent, passing its exact scope, namespace, identity and saved generation.
A preparation error fails that Bootstrap resource application.

For a retained first generation whose saved definition exactly matches the
selected manifest, reuse or finish its initial revision without changing the Agent
or its ETag. Changed manifests and later manually authored generations remain
unapplied. No extension process, server-hosted deployment or inference is started.

## Prevention

Extend the offline Ollama example regression to read the exact-scope AgentRevision
directly from storage and verify the Agent UID and generation. Assert revision ETag
stability on reapplication, recovery of a missing initial revision, refusal to
activate a changed catalog definition, and preservation of a later manual draft.

## Validation

- Inspected the published handler, AgentManagementService,
  AgentRuntimePreparationService and Console Apply generation/readiness paths.
- Prepared the focused regression and reviewed its assertions; no test has been
  executed for this correction yet.
- The local execution environment failed to start because its configuration was
  canceled. Compilation and functional validation must use an available runner.
- Status remains Open until correction validation completes.
