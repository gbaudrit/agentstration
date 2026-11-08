# ADR-0201: Data Sources compose operation acquisitions with logical datasets

## Status

Proposed

## Context

FR [#1041](https://github.com/gbaudrit/agentstration/issues/1041), under #1032,
composes the Connected Service and Storage families delivered by ADR-0195,
ADR-0198, ADR-0199 and ADR-0200. The ownership and route proposal below was
reviewed by the maintainer in the implementation chat on 2026-11-08. This is
architecture approval, not approval of the implemented browser UX.

ADR-0164 gave Data Sources origin configuration and acquisition Profiles.
ADR-0182 added discovery and promoted targets. ADR-0190 describes fixed
acquisition Tools. Their resources and execution histories already exist.
Replacing or deleting them would invalidate retained evidence and consumers.

## Decision

A Data Source has exactly one composition mode:

- Existing `profile` plus `configuration` retains the ADR-0164/0182 lifecycle.
- New `dataset` selects an exact Storage revision, a narrowing logical rule,
  and one saved acquisition binding: an exact Connected Service Operation
  revision and validated JSON parameters. There is no mandatory intermediate
  call-configuration resource. One operation can feed several named datasets.

This refines the ownership in ADR-0164 and ADR-0182 for new dataset compositions:
Connected Service owns the connection; its typed Operation owns the discovered
capability and separate explicit enablement; Storage owns implementation,
manual mappings and the authorized envelope. A dataset cannot carry connection
configuration or replace any of those authorities. The composition mode is
immutable; migration creates a new dataset explicitly, preserving the old one.

The immutable Operation type version may supply an acquisition descriptor
referencing an exact published Flow UID/version/hash with contract
`connected-service-operation-acquisition/v1`. The descriptor names the fields
holding the effective input/output schemas and the explicit read-only flag.
Only request-response, explicitly read-only operations are eligible. Missing
adapters are reported as unavailable. No HTTP method, schema annotation or
operation name implicitly authorizes an action for acquisition.

The Flow receives the acquisition identity, exact Operation reference, saved
parameters and caller identity. It returns the operation's JSON result. The
type-owned adapter implements the operation; Data Sources never copy resolved
connection values or Secrets into Flow inputs. Existing governed Flow/Tool/AWP
execution remains authoritative. Worker materialization validates the accepted
acquisition and its dependencies again at root and step boundaries. Direct or
forged calls to the operation Flow without the matching accepted acquisition
are refused. Activation performs no invocation and creates no Tool.

Acquisition records retain separate exact Operation, service and type evidence,
Flow binding, parameters, Storage binding and logical rule. Execution lives in
the selected context's operational Workspace. Idempotent acceptance identifies
the same durable acquisition; conflicting configuration or concurrent starts
are refused. Pending acceptance is recovered without changing its pins. A lost
authorization or dependency ends recovery explicitly rather than selecting a
newest implementation.

Publication writes bounded JSON through `IStorageContentGateway`, never a local
implementation. The effective prefix appends `datasets/{DataSourceUid}/` to the
saved relative prefix, followed by the operational Workspace and acquisition
identity. Shared placement cannot grant another dataset's reference authority.
Every acquisition has its own immutable object key and persisted write deadline.
Recovery confirms an existing key and digest before retrying a conditional,
idempotent write. Unknown or expired outcomes are explicit failures; business
execution is not repeated to repair publication.

Content reads reauthorize the Data Source and exact Storage and renew short-lived,
caller-bound references from the recorded key/version/digest. Changed Storage
revisions, missing versions and revocation fail closed. Disabling an Operation
prevents future acquisition but does not execute it to read previously acquired
content. Storage capability declarations remain truthful; this decision adds
no Snapshot retention guarantee.

The readiness response projects `CanAcquire` from the existing caller permission
in the operational Workspace. This avoids interpreting the Console's filtered
administrative permission list as execution authority. The projection is not
persisted and creates no permission or grant; admission rechecks authorization.

ADR-0190 and #1028/#1035 remain executable for historical acquisitions. Generic
parameterized and relay Tool exposure is owned by #1044. Dataset acquisitions
do not fake a legacy Profile/Flow target to enter the historical Tool factory.
There is no second relay engine. Knowledge Snapshot composition remains #1042.

## Consequences

- Console routes remain `/data-sources`, `/data-sources/new` and
  `/data-sources/{Name}`. The catalog opens creation with `?composition=dataset`;
  the historical profile journey remains available explicitly.
- Existing acquisition API routes are reused; bounded native content is read
  through `/api/datasourceacquisitions/{id}/content`. Saved parameters and
  Storage selection cannot be overridden by acquisition callers.
- The existing generic resource document stores persist the additive fields;
  no table replacement, data deletion or rewriting of historical records occurs.
- The initial schema profile reuses the bounded governed Tool validator and
  rejects unsupported validation keywords. Empty-input-valid operations use
  an empty saved object and display No configuration / Sans configuration.
- OpenAPI #1058/#1059 and OpenDAL #1039 are optional adapters. They must publish
  compatible executable type versions before their operations become eligible;
  historical schemas alone do not make an old implementation executable.
- Storage authorization uses the actually merged ADR-0200 generic Use contract.
  No automatic grants, permission bypass, or #1077/#1078 policy engine is added.
- #1033 retains its explicit implemented-UX approval and merge gates. This
  change maintains existing regression coverage and performs implementation
  smoke checks without starting that reusable integrated journey.
