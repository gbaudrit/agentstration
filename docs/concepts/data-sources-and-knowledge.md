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
  -> search, query, or bounded read
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

An Instance-, Tenant-, or Workspace-owned Knowledge Source binds between one and thirty-two visible Data Sources. The definition pins the exact Data Source and its native dataset definition or historical active profile identity plus the exact projection, retrieval, and optional transformation Flow versions and integrity hashes from its owner scope. Protected ancestor dependencies require explicit descendant-use authorization; ambiguous or stale references fail closed. Each named binding can use its latest successful Workspace acquisition or an explicitly selected successful acquisition. Omitting the transformation Flow is an identity transformation: the acquired content passes through unchanged.

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
Sources or active Flow versions, explicitly select **Resolve current dependency
revisions** when saving. Existing Snapshots retain their original evidence.

Projection requests accept an `idempotencyKey`. Retrying the same configuration
returns or resumes the same attempt. Cancellation stops the caller's wait; accepted
work remains resumable. After a restart, recovery uses recorded dependencies and
authorization, without selecting a newer configuration. Sealing, Snapshot creation,
activation and completion are distinct steps, with no distributed transaction.

## Retrieve only from the selected Snapshot

A Knowledge Source also selects a `knowledge.retrieval/v1` Flow. Search, query, and bounded read resolve the active or explicitly selected Snapshot before execution. Every returned item, citation, byte range, and Artifact identifier must belong to that Snapshot.

The built-in `knowledge-retrieval-builtin` Flow provides deterministic local text search, query, and read over retained Storage publications or historical filesystem-backed Snapshot Artifacts. Search interleaves bounded matches across the Snapshot entries; it is not a model-backed semantic index. Binary interpretation, spreadsheet analysis, semantic search, or answer synthesis requires a different governed retrieval composition and any required Tools.

A Knowledge Source may publish source-specific `search`, `query`, and `read` Tools. Their source identity and operation are fixed by the Tool definition, and the existing Tool pipeline and internal MCP provider retain authorization, assignment, execution, and audit ownership.

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
