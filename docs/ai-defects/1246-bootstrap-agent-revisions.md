# CAR-1246: Bootstrap saves Agents without preparing executable revisions

## Status

Prevented — 2026-11-20

## References

- Issue: [#1246](https://github.com/gbaudrit/agentstration/issues/1246)
- Introducing integration: [c8e26ec1](https://github.com/gbaudrit/agentstration/commit/c8e26ec1) added the Ollama bootstrap example and its resource/policy regression test. The original introducing change for the generic Agent bootstrap handler is Unknown.
- Corrective pull request: Not opened; correction is published on `feat/bootstrapping`.
- Related ADRs: ADR-0250 and ADR-0242 for phased startup/readiness; no new runtime architecture is introduced.

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
- [CI validation run](https://github.com/gbaudrit/agentstration/actions/runs/44715734782)
  compiled the correction and its regression on an isolated validation branch.
  The proposed product code and tests were checked against that branch and match
  after Git text normalization. Its temporary CI push trigger is excluded from
  the feature branch.
- Release solution and AEP builds, changed-file formatting, Linux build transfer,
  Windows host lifecycle and Docker image build passed.
- Full Fast lane: **1,561 passed**, zero failures.
- Full Integration lane: **1,583 passed, 28 optional integrations skipped**, zero
  failures (**1,611 total**). The Bootstrap module passed in both the ordinary
  Integration run and the instrumented coverage run.
- Both functional coverage shards and consolidated coverage publication passed.
  They cover the new first-revision, repeat, interrupted-preparation,
  changed-manifest and manual-draft assertions.
- Complete AEP SDK suite: **59 passed**. Dedicated PostgreSQL storage validation
  passed.
- The supplementary [hosted-module diagnostics job](https://github.com/gbaudrit/agentstration/actions/runs/44715734782/job/136220724625)
  later completed with one Data Sources test failure (79 passed, 5 skipped).
  Its captured summary does not identify that test, so no causal attribution is
  made here. The other diagnostic group passed. The failed job was not rerun
  during the milestone audit and is not reported as green.
- The local environment was unavailable for the initial correction. Later local
  validation at published product baseline 90ed3960 passed the clean Release
  build, all 1,561 Fast tests, and 1,588 Integration tests with 28 optional skips
  and zero failures (1,616 total), including the complete Data Sources module.
- The later real-process Ollama-fixture Bootstrap/restart smoke verifies exact-scope
  AgentRevision existence and retained UID/ETag. Existing desktop/mobile public
  waiting/login smoke and documentation/ADR/LikeC4 builds also passed. No new
  Console component or user journey was added by this Agent revision correction.
