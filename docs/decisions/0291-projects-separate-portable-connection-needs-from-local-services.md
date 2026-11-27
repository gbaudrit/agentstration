# ADR-0291: Projects separate portable connection needs from local services

## Status

Proposed — implementation for [#951](https://github.com/gbaudrit/agentstration/issues/951).

## Context

ADR-0289 established exact contract, target, schema and route evidence. A Project
also needs several independently named tracking and forge connections. A resource UID
or public extension ID cannot identify that business need across installations.
ADR-0196 and #1047 already provide governed ConnectedService types, typed exports,
Parameter/Secret bindings and protected resolution; their lifecycle is not recreated.

## Decision

A Project owns up to 32 unique `connectionRequirements`: logical name, functional
contract/revision, required operations and optional expected provider-qualified target.
These declarations contain no local resource, account or credential references.
A separate local `connectionAssignments` collection selects an exact ConnectedService
and an explicit Describe adapter/export mapping for each configured name. Several
requirements can share a contract, type or service, after independent validation.
An unassigned requirement is valid state. Selection is explicit; discovery never
chooses the first service. Authored Tracking/Forge entries in the older `bindings`
collection are rejected; ordinary context and existing native consumer bindings remain.

Reuse the existing service export inspector for current access, readiness, UID,
generation, exact type revision/schema and input governance. The initial adapter maps
four standard text exports to ATC/AFC `providerId`, `targetId`, `bindingId` and `revision`.
It does not infer compatibility from a vendor/type name: the derived AEP route and
canonical typed schemas, required operations and actual Describe response must match.
ToolProvider and ExtensionRegistration references are derived evidence. The explicit
Describe Tool reference in the assignment is a local adapter selection, not account
configuration. Business options and Parameter/Secret references stay on the service;
registration configuration remains extension transport configuration.

A Workspace service may use an ancestor-owned type or adapter under the existing
descendant-use grants. Metadata inspection does not read Secret values. Standard
Parameter inputs are resolved in the authorized service owner's context and contribute
to an opaque configuration digest. Only digests and exact references are captured in
Work. The existing governed pipeline continues to resolve protected transport inputs
late; no service credential is copied into Describe arguments or snapshots.

Each execution target explicitly lists the requirement names it consumes. Admission
captures only those named connections, alongside ordinary context bindings. Unrelated
unconfigured requirements do not block no-provider work. Immutable attempt and durable
operation records retain the selected declaration, assignment, service/type and
contract/route/capability evidence. Revalidation uses the retained selection, checks
current authorization and compares evidence. A transient, nonserialized invocation
guard repeats service inspection immediately before the native MCP call, after native
schema validation. Parameter retargeting or revocation fails without selecting a new
service. Uncertain operations keep their original recovery coordinates.

## Consequences

Independent stores can use different UIDs and owner scopes for the same portable
requirement and external identity. Existing Work JSON persistence stores the new
bounded records without a relational schema change or release migration. REST,
Bootstrap and agent-facing execution configuration use the same application services.
No new Console journey or generic connection provisioning engine is introduced.

The executable fixture negotiates configured ATC/AFC target tuples and exercises ATC
operations through real MCP. It does not provision vendor accounts or define how a
GitHub/GitLab implementation exchanges a service's secured configuration for its own
binding/revision. That provider integration remains #958/#959 and must use the existing
protected service resolver. A service with compatible exports alone is insufficient:
its configured target must actually be recognized by the selected contract adapter.
