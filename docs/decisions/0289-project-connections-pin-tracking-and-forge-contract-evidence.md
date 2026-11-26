# ADR-0289: Project connections pin tracking and forge contract evidence

## Status

Proposed — implementation of #951 after the independent ATC/AFC foundations.

## Context

ADR-0270 captures authorized Project resource identities at task admission. ATC and
AFC now provide independent functional contracts. Selecting a Tool identity alone
does not prove its contract, effective operations, configured logical target or
current AEP/MCP mapping. Tracking may have no repository and may use a different
extension from the forge.

## Decision

Add explicit Tracking and Forge binding purposes. Each selects an exact Describe
Tool, ToolProvider and ExtensionRegistration by scope, namespace, UID and generation,
plus a precise contract revision, configured authority/target/binding/revision and
required operations. Existing Context, Sharing and current native consumer bindings
retain their existing meaning; no authority is inferred between connections.

For these explicit connections, Infrastructure extends the identity/access checks
of ADR-0270 with current AEP discovery and a bounded, governed Describe invocation.
The caller needs run-read and run-execute permissions, and current Discover/Use
grants for all mapped resources. Configuration and protected credentials are resolved
in the caller's context only after those checks. All canonical Tool schemas and AEP
declarations are validated using the contract SDKs. The returned target, revision,
implementation identity and actual capabilities must match the selection.

The ordinary Tool pipeline carries optional opaque route/schema expectations.
Immediately before invocation its MCP adapter rechecks the current authenticated
AEP descriptor/registration fingerprint and all native Tool schemas. Drift fails
before calls/tools. Fingerprints canonicalize JSON object properties, retaining
array order. The route fingerprint conservatively pins the whole AEP manifest,
registration configuration and selected mapping, including implementation versions.

Work stores only neutral selection records, scoped contribution references,
implementation/version evidence, supported operation names and opaque digests.
It does not reference the ATC/AFC SDKs, MCP schemas, native MCP names, endpoint
credentials or forge DTOs. The evidence accompanies the existing immutable
per-attempt Project snapshot and digest, including after SQLite restart.

Consumers reauthorize a captured resolved binding through the application context
service before using it. Revalidation compares retained evidence and never adopts
a different provider, contract revision, mapping or capabilities. New admissions
can capture current evidence against their explicit resource selections; edits,
revocation and remote drift never rewrite retained attempts. Management and remote
observations remain separate from the transactional Work admission guard.

## Consequences

- Tracking-only Projects and separately selected tracking/forge providers are executable
  with deterministic fixtures, without a repository, vendor account or LLM.
- Availability for explicit contracts now includes a remote, read-only negotiation;
  ordinary identity-only bindings retain their prior semantics.
- All mapped Tools must currently be enabled, discoverable and usable. Describe
  requiring human approval fails explicitly; background resolution cannot approve it.
- Contract snapshots extend existing JSON payloads in both Work stores; no relational
  model change or migration is required.
- #955, #958 and delivery consumers own adoption for their actual operations. This
  increment negotiates connections and enforces invocation pins; it does not implement
  vendor mutations, synchronization or remote acceptance.
- No new Console/Workplace journey is introduced; parent #929 retains its UX ownership.
