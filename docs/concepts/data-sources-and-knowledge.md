# Data Sources and Knowledge Sources

Data Sources and Knowledge Sources solve different problems:

- a **Data Source** governs how Agentstration acquires content from an origin;
- a **Knowledge Source** defines the view that Agents and users consult after one or more acquisitions have been projected into an immutable Snapshot;
- the existing **Source** resource family distributes versioned configuration and Pack content. It does not acquire business content and is not an alias for Data Source.

```text
origin
  -> Data Source Profile
  -> Data Source acquisition
  -> durable governed Artifacts
  -> optional per-binding transformation
  -> Knowledge projection
  -> immutable Snapshot
  -> read, search_regex, or get_status
```

## Acquire once, project many times

A historical Data Source owns the origin identity, configuration, enabled state, and profile reference. Its Data Source Profile owns the reusable configuration schema, acquisition Flow, governed Tool bindings, limits, and policies. Profiles have editable drafts, immutable published revisions, and one active revision. Native datasets instead pin an Operation, saved parameters and Storage; they do not require a historical Profile.

Data Sources and profiles may be owned at instance, tenant, or Workspace scope. References follow ancestor-only visibility and explicit descendant-use grants: a Workspace may consume an authorized Workspace, tenant, or instance resource, while a tenant resource cannot depend on a Workspace resource. Profile publication resolves dependencies from the profile owner scope and pins the exact Flow version and integrity hash plus Tool and provider identities and generations. The acquisition Flow currently has to share the profile's exact scope because #751 has not yet exposed Flow identities to the shared access-policy overlay; ancestor Flows fail closed instead of inheriting visibility as authorization. The consuming Workspace validates those immutable pins without rebinding names, then records the exact source, active profile revision, limits, policies, and returned Artifact manifest in its isolated acquisition state.

The acquisition Flow declares:

```yaml
definition:
  metadata:
    flow.contract: datasource.acquisition/v1
```

The Flow returns a bounded Artifact manifest. Acquisition Tools remain generic: an HTTP or crawler Tool does not need to know that its output may later feed Knowledge.

Data Source discovery and acquisition Flow inputs use camelCase for typed contract properties, including nested profiles and promoted targets (`profile.name`, `target.selector`). Normal dispatch and recovery reconstruct the same contract from the persisted snapshots. Extension-owned JSON values, including configuration, parameters, schemas, and selectors, retain their original property names and values at every depth.

In the Console, **Discovery** opens on **Results**, which contains discovered choices and promoted acquisition targets. **History** shows discovery runs, their states, and scoped Flow Run links. Switching between these views keeps automatic updates active while a discovery is running.

The choice button shows **Promote** for a new target, **Already promoted** (disabled) for a current target, and **Reconcile** for an obsolete matching target after a new successful discovery. Reconciliation adopts the eligible choice while retaining the target identity. A name used by another choice remains unavailable. Updates use the target's observed ETag: after a concurrent change, refresh and review the target before retrying. Existing acquisition snapshots retain their original discovery evidence.

## Project one or more Data Sources

An Instance-, Tenant-, or Workspace-owned Knowledge Source binds between one and thirty-two visible Data Sources. The definition pins the exact Data Source and its native dataset definition or historical active profile identity plus the exact projection and optional custom retrieval and transformation Flow versions and integrity hashes from its owner scope. Protected ancestor dependencies require explicit descendant-use authorization; ambiguous or stale references fail closed. Each named binding can use its latest successful Workspace acquisition or an explicitly selected successful acquisition. Omitting the transformation Flow is an identity transformation: the acquired content passes through unchanged.

The Knowledge Source invokes one `knowledge.projection/v1` Flow with all prepared inputs. The projection decides which durable, publishable Artifacts become the new Snapshot. Inputs remain provenance unless the projection emits them.

An authorized descendant Workspace can use an inherited Knowledge Source, but its acquisitions, projections, Artifacts, Snapshots, active state, retrieval runs, indexes, and sessions stay in that Workspace. Another Workspace using the same definition has a separate operational history.

The built-in `knowledge-projection-builtin` Flow performs identity aggregation. It deliberately preserves heterogeneous media such as JSON, HTML, text, or spreadsheets. If a Snapshot must be homogeneous, select a transformation on a binding or a specialized projection Flow that normalizes the prepared artifacts. There is no built-in transformation Flow.

Every successful projection creates and activates a distinct immutable Snapshot. A failed projection leaves the previous active Snapshot unchanged. Provenance pins the exact Data Source acquisitions, optional transformation Runs, projection Flow and Run, Artifact receipts, storage lineage, and integrity evidence.

## Native datasets and independent Snapshot output

A native dataset binding uses its saved acquisition parameters and exact Storage
revision. Select an **output Storage** on the Knowledge Source to enable native
projection. Input and output Storages are independent. The output prefix narrows
the selected Storage envelope; Knowledge does not configure the connection or
manual export mapping. A disabled native preparation can still be saved before
an output Storage is selected.

The initial retained-publication implementation supports builtin local Storages.
It stores sealed content separately from mutable ASP objects and never overwrites
a sealed publication. A provider supporting ordinary object reads and writes is
not automatically compatible. The bounded initial inline Flow profile supports
64 KiB per content object, subject to lower Storage limits.

Each binding also selects **Refresh acquisitions if necessary**:

| Refresh | Maximum age | Projection input selection |
| --- | --- | --- |
| Off | Empty or zero | Latest compatible success, without an age limit. |
| Off | Positive | Latest compatible success within that age; otherwise fail. |
| On | Empty or zero | Acquire for each new projection attempt. |
| On | Positive | Reuse a recent compatible success, otherwise acquire. |

Successful completion determines age; the exact age boundary is accepted.
Explicit acquisition selection must match the saved configuration and freshness
policy. Required failures stop publication; optional failures appear in provenance.
A failed refresh never falls back to an expired success.

Output content retains its exact Storage revision, rule, key, version and digest.
Changing or revoking a Storage cannot redirect an old Snapshot. Stale revisions
are explicitly unavailable; retained bytes are preserved. Keep both control-plane
data and the configured local content root on persistent storage. Existing
Artifact-only Snapshots retain their original receipts and read path.

Editing a definition preserves unchanged dependency pins. To adopt revised Data
Sources or active Flow versions, use **Update dependencies**, review the confirmation, then save. Existing Snapshots retain their original evidence.

Projection requests accept an `idempotencyKey`. Retrying the same configuration
returns or resumes the same attempt. Cancellation stops the caller's wait; accepted
work remains resumable. After a restart, recovery uses recorded dependencies and
authorization, without selecting a newer configuration. Sealing, Snapshot creation,
activation and completion are distinct steps, with no distributed transaction.

## Native Snapshot access for Agents

Publish the source-specific `read`, `search_regex` and `get_status` relay Tools.
The source identity is fixed in each immutable ToolDefinition. The ordinary Tool
pipeline retains assignment, approval, hook, authorization and audit ownership.
Publication creates a `<source>.access` ToolSet in the selected logical Instance,
Tenant or Workspace scope; content and read sessions stay in the corresponding
operational Workspace.

Both reading and regex search may come first. Without `snapshotId` or `cursor`,
they select the active usable Snapshot or prepare one through the existing
projection and acquisition pipeline. Refresh restrictions in the table above
still apply: a binding with refresh disabled cannot silently reacquire. Save
**Automatic projection parameters** on the Source for implicit preparation.
A pending result has `state: preparing`, `preparationId` and
`retryAfterMilliseconds`; retry with that ID and the original request to continue
the same attempt. Failure preserves the previous active Snapshot.

| Tool | Request | Result |
| --- | --- | --- |
| `read` | Optional `snapshotId`, `documentId`, `range`, `maxCharacters`; or `cursor` only with an optional page size | Bounded chunks, exact Snapshot/document identities and `nextCursor` |
| `search_regex` | `pattern`, optional `ignoreCase`, `snapshotId`, `limit`, `contextCharacters`; or `cursor` with optional limit | Matches with `matchRange`, `readRange`, excerpts and `nextCursor` |
| `get_status` | Optional `snapshotId` to compare | Active versus latest published Snapshot, comparison flags, recorded input freshness, processing/failure evidence and document formats/sizes |

Optional Snapshot, document and continuation selectors accept `null` as absent.
For current status, call the relay with `{"request":{}}` or
`{"request":{"snapshotId":null}}`. A supplied Snapshot ID must be non-empty;
`""` is not a current-Snapshot shortcut. Invalid Tool arguments return a governed
HTTP 422 with their schema path. The AWP `errorCode` alias remains readable in
HTTP diagnostics while generic authentication `code` fields stay redacted.

Initialization version 12 reconciles the native nullable selector contracts.
Previously published relay versions keep their accepted schemas and pins.
Republish the Knowledge Tool exposure under a new version, such as `1.0.1`, and
select that ToolSet version for the Agent to use the updated contract.
In the Console, open the Knowledge Source's **Tools** tab. The publication form
remains available after the first publication, shows the current version and
suggests the next patch version. Edit the proposed version and approval choice,
then select **Publish a new Tool version**. Existing publications remain available;
a failed publication keeps the previous exposure visible. Inherited Sources show
their published Tools without owner-only publication controls.

To read a search hit, pass its result `snapshotId`, match `documentId` and
`readRange` as the `range` of `read`. Offsets count Unicode scalar characters,
start inclusive and end exclusive. This is independent of UTF-8 byte offsets and
UTF-16 string positions. A read or regex cursor pins its original Snapshot, even
if a new Snapshot becomes active. Continue using only the cursor and page size;
combining it with a new selection is rejected. An expired or unavailable historical
selection fails explicitly without switching to another Snapshot.

Cursors are opaque, authenticated, caller/source/scope-bound and valid for one
hour. Every call rechecks authorization. Durable leases protect in-progress
Snapshot selections, with 256 conditional slots per Workspace. Reusing a lease
does not extend expiry. Text reads are limited to 32,768 Unicode characters per
page and 1 MiB per document. Regex scans are limited to 8 MiB, a 512-character
pattern, 100 matches per page, a 100 ms engine timeout and 32,768 excerpt characters.
Regex uses .NET non-backtracking syntax: lookarounds and backreferences are
unsupported; scalar-splitting matches fail explicitly. Binary formats need an
explicit transformation into supported UTF-8 text during preparation.

`get_status` never triggers acquisition or projection. Its freshness evidence
comes from the active Snapshot's recorded acquisitions; it does not verify remote
origin changes. The active Snapshot can be older than the latest published one
when a user intentionally reselects an older publication.

The Console **Consultation** tab uses the same three operations. The editor's
**Update dependencies** action saves the configuration and adopts changed
Data Source and Flow dependencies after confirmation. Ordinary saving preserves
unchanged pins.

An optional custom `knowledge.retrieval/v1` Flow can still serve the historical
`/search`, `/query` and `/read` REST endpoints. Native endpoints under
`/api/knowledgesources/{name}/content/` are `read`, `search-regex` and `status`
(all POST; preparation returns HTTP 202). Native access invokes no retrieval Flow
and creates no synthetic Flow Run. See [ADR-0210](../decisions/0210-knowledge-exposes-native-snapshot-access-tools.md).

## Flow contract catalog

Classified Flows use the single Flow-owned `flow.contract` metadata key:

| Contract | Owner | Purpose |
| --- | --- | --- |
| `datasource.acquisition/v1` | Data Sources | Acquire an origin and return a bounded Artifact manifest. |
| `artifact.transform/v1` | Artifacts | Optionally transform one Knowledge binding before projection. |
| `knowledge.projection/v1` | Knowledge | Select or reshape prepared Artifacts for a new Snapshot. |
| `knowledge.retrieval/v1` | Knowledge | Search, query, or read within one selected Snapshot. |
| `artifact.storage.write/v1` | Artifacts | Persist staged content and return a durable Artifact receipt. |
| `artifact.storage.read/v1` | Artifacts | Materialize durable content as governed local content. |

The contract value is a classification and schema boundary, not a new deployment unit. REST, Console, MCP, and background work delegate to the same family services inside the authoritative Agentstration process.

See [ADR-0164](../decisions/0164-data-sources-own-governed-acquisition.md), [ADR-0166](../decisions/0166-flow-contracts-use-one-flow-owned-metadata-key.md), and [ADR-0167](../decisions/0167-knowledge-sources-project-data-source-artifacts.md).

See [ADR-0207](../decisions/0207-knowledge-publishes-retained-storage-content.md) for publication limits and recovery semantics.

## Artifact identity and Storage materialization

Every successfully produced native acquisition and retained Knowledge output is
registered as an Artifact. Its materialization identifies the exact Storage
revision, content key, available version, length and integrity digest; its producer
identifies the Data Source or Knowledge Source revision, acquisition/projection
and actual Flow Run. Selecting a Storage does not remove Artifact identity.

Open **Artifacts** to follow the producer and Storage links, then use the existing
**Content** tab to create a governed local copy for preview or download. Acquisition
history and Snapshot details also link to these Artifacts. A direct Storage write
does not invent a source local copy or a Storage Flow Run.

Existing native histories are reconciled in bounded background batches under the
original caller's current permissions. This only restores missing Artifact metadata
and acquisition manifests: content, origin executions and immutable Snapshots are
preserved. Revoked or changed dependencies remain explicitly unavailable.

Artifact identity does not add retention guarantees to a provider. Retained
Snapshot output still supports the builtin local implementation; Azure/OpenDAL
requires its own qualified publication adapter. See
[ADR-0208](../decisions/0208-produced-content-remains-an-artifact-materialized-in-storage.md).

Implementation evidence: [native Knowledge access validation](../architecture/implementation/knowledge-native-access-validation-2026-11-10.md).

The Knowledge Source **Projections** tab polls the existing projection history
while a projection runs. It shows an indeterminate activity indicator, elapsed
time and acquisition/preparation/publication phases; once the root Flow exists,
it also links its execution and reports completed root steps. The history and
Snapshots refresh after success or failure, without retrying the start request.
Reloading the page resumes observation of an existing active projection. A
refresh failure leaves processing in progress and offers an explicit refresh.

## Querying structured acquisition content

For a Data Source backed by structured document Storage, open **Content** and
choose a successful retained acquisition. Its query panel reads that acquisition's
immutable publication. The acquired JSON preview remains available. Save the
query under a display name and technical name to create an independent draft.

Open **Queries** to edit and publish that draft, inspect its versions and execute
one exact publication or the latest published version. Editing a draft changes no
published version. Published queries live in Agentstration's configuration store,
separately from acquired documents. The Query access panel controls sharing with
descendant scopes; Source and Storage Use remain necessary at execution.

Open **Tools** to find the Data Source's persisted publications after navigation
or refresh. **Edit** opens the existing Tool definition editor to prepare and
publish a new Tool version; the published invocation link remains available.
The generic Query Tool belongs to the shared catalog and is omitted from this
Data Source-specific list. It accepts the scoped Query reference; its invocation
JSON is available on the selected Query. A named relay fixes the Query identity and
optionally its version. Without a version, each initial call uses the most recent
publication, including when its version number is lower. Publishing a compatible
filter/order change requires no Tool or Flow republication. An incompatible
contract fails explicitly instead of falling back; publish a new Tool version to
adopt it. Acquisition selection is independent: latest successful retained
acquisition or one fixed acquisition when publishing the relay.

New Query relays hide the internal acquisition UID/pin from their caller schema. The initial call accepts `{}` and selects the latest retained acquisition automatically; platform authorization supplies the execution identity. In the Tool draft, **Hide** / **Masquer** can additionally omit optional inputs such as `request.acquisitionId` or the whole `request` object. Publish a new Tool version and explicitly adopt it to change an existing model-facing contract.

Continue with the returned Query cursor only. The Query version and acquisition
remain fixed during pagination even after new publications. Current permissions
are checked on each page; retention and bounded cursor expiry still apply. Results
record the resolved Query identity/version, digests and acquisition.

Existing direct acquisition Tools are retained and listed as existing direct
exposures. They still take query JSON at invocation; free-form input uses raw JSON
editing. They are not rewritten into named queries automatically.

For an acquired object containing `results`, the collection schema must describe
the whole stored object. Selecting `/properties/results/items` does not split it.
An OData payload such as `{"$select":"results"}` selects the envelope property;
`{"$filter":"results/any(r:r/status eq 'EN COURS')"}` selects envelopes containing
a matching item without filtering items inside the returned array. Per-item
queries require an explicit transformation before publication.

Storage administration configures collections and their contracts/capabilities;
its query/Tool-publication block is no longer displayed. Underlying mutable
document APIs remain available and do not address acquisition publications.

See [ADR-0227](../decisions/0227-data-source-document-tools-select-latest-or-exact-acquisitions.md)
and [ADR-0228](../decisions/0228-data-source-queries-evolve-independently-from-tool-exposures.md).
