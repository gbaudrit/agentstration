# ADR-0200: Storage execution pins protected bindings and local Workspace defaults

## Status

Proposed

## Context

FR #1040 integrates the resource/API foundation of #1045 with ASP over MCP,
extension contributions and the existing Artifact Storage Flows. ADR-0193 keeps
content Tools generic and target selection separate from authority. ADR-0199
requires exact Storage references, manually confirmed Connected Service exports
and opaque configuration layers. ADR-0151 preserves Artifact staging, receipts,
leases and Flow composition; ADR-0185 separates administrative ownership from
operational Workspace placement. These accepted boundaries remain in effect.

The metadata-only immutable builtin `local@1.0.0` has no content capabilities.
The maintainer requires a functional local default, separate physical directories
and a persistent operator-owned root suitable for Docker mounting. #1064 owns
complete access administration and must be able to preserve deliberate revocations.

## Decision

Expose provider-neutral content ports from `Storage.Contracts`. Infrastructure
adapts these ports to local content and the existing ASP client. Each call resolves
the exact Storage scope, UID, generation and binding digest again, including type,
schema, provider, service, exports, access policies and descendant UsePolicy. Actual
content operations also require the existing `runs/execute` contextual permission;
catalogue/definition inspection does not execute content operations. Administrative
execution retains its trusted operational Workspace and logical scope.

AEP gains the optional `aep.storage-types/1.0` capability. Its immutable version
descriptors declare schemas, typed requirements, actual capabilities/limits and one
known ASP MCP operation group. Enrollment synchronizes only the contributing
registration's namespace and ownership, retaining exact registration UID/generation
and the contribution digest. Disappearance preserves resources and evidence. A
changed immutable contribution requires a new version and explicit consumer adoption.

For remote execution, recheck the authenticated descriptor and issue only the
manually selected connection bindings. Standard values retain their JSON types;
secured values use the existing AEP Secret capability service. Exchange these
values, configuration, envelope and dataset restrictions as distinct layers through
an authenticated configuration endpoint, never content Tool business arguments.
Revoke Secret capabilities when the invocation ends.

The optional product-independent `Asp.Aep` adapter configures at most 128 leases
lasting at most 30 seconds. A lease is selected through a protected HTTP header,
bound to its actual authenticated transport subject and exact logical StoreRef.
The extension explicitly authorizes configuration and independently authorizes
every content operation. Product authorization evidence partitions opaque content
references; it is not end-user delegation. A backend requiring verifiable end-user
delegation must refuse a service identity that does not satisfy that requirement.
Content arguments cannot create or replace configured bindings. The existing
static ASP binding mode remains available to independently authorized consumers.

Publish `local@2.0.0` on the same Instance-owned type UID. Retain `local@1.0.0`
unchanged and leave existing Storage pins unchanged. Version 2 implements bounded
list, stat, read and conditional/idempotent write. It advertises no deletion,
stable historical object versions or Snapshot retention. The native Artifact
receipt/retention contract remains a separate established guarantee.

Local placement is `<configured-root>/<scope-hash>/<Storage-UID>`. Names, logical
keys and consumer rules cannot select physical directories. Reject traversal and
reparse/symbolic links along the configured path. Newly allocated Unix Storage
directories are private to their owner. The root must be operator-controlled;
containment checks do not make an attacker-writable filesystem safe against an
administrator replacing directories while the process is running.

Local content and write receipts commit together by replacing one bounded state
file under an exclusive per-Storage file lock. This is a content backend, not a
new control-plane persistence framework. Reads verify stored content integrity.
Opaque references/cursors bind exact authority, restrictions, query and lifetime.

Initialization version 3 reconciles existing Workspaces; new Workspaces use the
same provisioner. Business defaults are Workspace-owned. Each Instance/Tenant
operational Workspace has a separate default owned by its logical administrative
scope, with explicit access from that operational Workspace only. One Instance
type is shared through recorded, exact grants; no descendant type copies exist.

Grant initialization is recorded once. Subsequent synchronization/provisioning
does not restore missing rights, reset disablement or overwrite an existing
default. Interrupted access initialization fails closed and requires explicit
administrative repair; a name/ownership collision fails rather than replacing a
user resource. #1064 administers policy and must retain these decisions and markers.

New builtin Artifact writes pin the selected default Storage in their receipt and
use its separate directory. Existing unprefixed Artifact receipts retain the
original durable adapter and location. Do not copy, relocate or retarget existing
content. Dependency checks include exact Storage UIDs retained by Artifact receipts.
The existing staging, lease, provenance and read/write Flow contracts remain intact.

## Consequences

- Persist control-plane data and Data Protection keys as well as the content root.
  Keeping only content bytes cannot reconstruct exact resource identities.
- A provider or policy change cannot silently rebind an acquisition or Snapshot.
  Consumers must handle explicit stale/unavailable references.
- #1044 remains the only generic fixed-parameter relay mechanism. This increment
  adds no relay engine or automatic publication of Agent Tools.
- #1041/#1042 own datasets, projection and Snapshot publication. The current ASP
  content profile is insufficient for durable Snapshots requiring stable versions
  and retention; those capabilities must actually exist before publication.
- #1064/#1065 own administration refinements and their deferred UX scenario.
  #1033 retains its integrated scenario and approval/merge gate.
- No EF migration, legacy Storage contract, additional product process or broker
  is introduced. Historical Artifact support preserves an already delivered
  vertical rather than retaining an intermediate Storage model.
