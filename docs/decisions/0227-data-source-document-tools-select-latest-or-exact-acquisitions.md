# ADR-0227: Data Source document Tools select latest or exact acquisitions

## Status

Accepted

## Context

Structured acquisitions publish immutable documents. The Storage runner addresses
current mutable collection documents, so its empty results do not describe an
acquisition's retained content. A reusable Tool usually needs the most recent
acquisition, while reproducible reads also need an exact historical acquisition.
The selected response sub-schema does not split an acquired JSON envelope.

## Decision

The Data Source Content tab selects a retained successful acquisition, queries its
immutable publication, and can publish a versioned relay Tool. Console, REST and
Tool execution share the Data Sources document access service and the existing
Storage document gateway. They never accept a caller-selected physical target.
The shared query editor also serves the existing mutable Storage runner.

Tool publication offers two explicit modes:

- `latest` (default): bind the Data Source identity/UID and logical acquisition
  stream; select its latest successful retained acquisition at each initial call.
  An optional `request.acquisitionId` selects a retained acquisition in that stream.
- `exact`: additionally bind the acquisition name and UID; callers cannot override it.

Both modes bind the query profile, model digest and retained shared Schema evidence,
including parent identity, exact published version, selected path and hashes. The
latest acquisition is chosen before contract comparison. An incompatible latest
contract fails explicitly; execution never falls back to older compatible content.
A new Tool version is required to adopt a changed contract. A Tool starts no
acquisition and adds no data transformation, implicit grant or retention guarantee.

The acquisition stream is its logical Instance, Tenant or Workspace scope, fixed
at publication. Operational Workspace lookup follows existing topology. Current
Source identity, enabled state and Use authorization are checked before any trusted
operational lookup. The stream must lie between the Source owner and the caller's
consumer scope. Every record must match Source UID, namespace, logical stream,
Tenant and operational Workspace. Current permissions and Storage/provider Use
remain mandatory for every page. Runs/read inspects metadata; runs/execute queries;
resources/write publishes a Tool using the existing immutable ToolDefinition path.

Pagination protects a four-minute, caller/context/selection-bound application
cursor. It fixes the first page's exact acquisition/publication and query arguments,
including when another acquisition becomes latest. Existing acquisition projection
leases prevent retirement during that bounded session; completion releases the
lease, expiry permits cleanup, and capacity is bounded. Provider cursors stay
inside the protected application envelope. Changing arguments, using another
context, tampering, expiry or unavailable content fails explicitly.

## Consequences

- Storage administration retains current-document operations. Acquisition browsing
  lives alongside its exact Data Source content and provenance.
- Current acquisition output remains one immutable JSON object. Filtering nested
  arrays requires supported OData expressions against that envelope; item-level
  queries require an explicit transformation before publication.
- Dynamic selection is bounded to 10,000 operational acquisition records and fails
  explicitly beyond that inventory limit. It neither scans an unbounded history
  nor changes resource store query interfaces in this increment.
- No new resource kind, provider protocol, relational migration or derived Schema
  lifecycle is introduced. Fixed acquisitions remain subject to existing retention.
- Component/API tests and existing browser regressions are required now. The reusable
  end-to-end browser journey remains deferred in #1173 until maintainer UX approval
  and implementation PR merge, as required by AGENTS.md.
