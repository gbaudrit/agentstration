# ADR-0307: AEP ToolSets project governed drafts without enabling their Tools

## Status

Proposed — implementation for [#1435](https://github.com/gbaudrit/agentstration/issues/1435).

## Context

Git and GitHub advertise independently governed MCP Tools. Operators need dedicated
collections before deciding their access. Data Source Profile bundles require a
DataSourceProfile or specific operation Flows, and automatically enable required
bundle Tools; they do not represent standalone grouping of disabled contributions.
ADR-0150 retains immutable publications and governed Tool execution. The access
and activation changes in #1436 have a separate owner.

## Decision

AEP adds the optional `contributions.toolSets` collection and `aep.tool-sets` version
`1.0`. Each declaration contains an id, display name, version, optional description
and bounded members selecting advertised Tool contribution ids, capabilities and
routes. It carries no local resource identities, ownership scopes, credentials,
consumer grants or activation instructions. The SDK rejects invalid identifiers,
unknown Tools, duplicate members/routes, unsupported capability versions and bounds
exceeding 32 sets or 128 members per set.

The existing AEP Tool enrollment provisioner projects each declaration after Tool
discovery at the exact registration scope and namespace. A local name combines the
registration name and contribution id, with deterministic truncation for long names.
Members reference only Tools materialized by that provider. Governance identifies
the exact registration and extension. A conflicting owner fails closed; a same-name
resource at another scope is untouched. Equivalent provisioning preserves the UID
and ETag. Replaying provisioning reconciles a changed draft without changing earlier
immutable publications or consumer pins.

Projection creates ordinary ToolSet drafts, without publication, Tool activation,
Agent assignment or consumer access-policy writes. Git declares one group. GitHub
explicitly separates seven read-only ATC/AFC Tools from four write-capable Tools.
The mixed `github.issue` and `github.coding` Tools belong only to the write group.
A newly advertised GitHub Tool requires an explicit classification; names are not
used to infer its effects.

Draft creation/update may reference disabled or unavailable discovered Tools while
retaining reference authorization, scope, provider and schema validation. Publication
and execution retain their availability, activation and immutable-pin checks. This
allows inspection before activation without manufacturing executable authority.
An exact-scope ToolSet update entry point shares the existing application use case
and prevents ambient-scope selection during provisioning.

The membership and activation/access policy are distinct boundaries. #1436 owns
ToolSet-derived member grants, denials, descendant inheritance and operational
activation. This decision does not predefine those authorization rules.

## Consequences

- Existing ToolSet collection and detail screens display the extension-owned drafts;
  no new UI or provisioning engine is introduced.
- Fresh installations create the three groups directly during enrollment. No
  old-group conversion, compatibility alias or historical resource migration is
  implemented. Ordinary process startup/inspection is not a provisioning replay.
- The current server, SDK and extension binaries use this declaration together.
  The change introduces no relational schema migration.
- Publication and execution continue to reject disabled members. Actual provider
  credentials and Connected Service authorization remain independent of grouping.
