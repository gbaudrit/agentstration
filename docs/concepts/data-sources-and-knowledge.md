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

A Data Source owns the origin identity, configuration, enabled state, and profile reference. Its Data Source Profile owns the reusable configuration schema, acquisition Flow, governed Tool bindings, limits, and policies. Profiles have editable drafts, immutable published revisions, and one active revision.

Data Sources and profiles may be owned at instance, tenant, or Workspace scope. References follow ancestor-only visibility and explicit descendant-use grants: a Workspace may consume an authorized Workspace, tenant, or instance resource, while a tenant resource cannot depend on a Workspace resource. Profile publication resolves dependencies from the profile owner scope and pins the exact Flow version and integrity hash plus Tool and provider identities and generations. The acquisition Flow currently has to share the profile's exact scope because #751 has not yet exposed Flow identities to the shared access-policy overlay; ancestor Flows fail closed instead of inheriting visibility as authorization. The consuming Workspace validates those immutable pins without rebinding names, then records the exact source, active profile revision, limits, policies, and returned Artifact manifest in its isolated acquisition state.

The acquisition Flow declares:

```yaml
definition:
  metadata:
    flow.contract: datasource.acquisition/v1
```

The Flow returns a bounded Artifact manifest. Acquisition Tools remain generic: an HTTP or crawler Tool does not need to know that its output may later feed Knowledge.

## Project one or more Data Sources

An Instance-, Tenant-, or Workspace-owned Knowledge Source binds between one and thirty-two visible Data Sources. The definition pins the exact Data Source and active profile identity plus the exact projection, retrieval, and optional transformation Flow versions and integrity hashes from its owner scope. Protected ancestor dependencies require explicit descendant-use authorization; ambiguous or stale references fail closed. Each named binding can use its latest successful Workspace acquisition or an explicitly selected successful acquisition. Omitting the transformation Flow is an identity transformation: the acquired Artifact references pass through unchanged.

The Knowledge Source invokes one `knowledge.projection/v1` Flow with all prepared inputs. The projection decides which durable, publishable Artifacts become the new Snapshot. Inputs remain provenance unless the projection emits them.

An authorized descendant Workspace can use an inherited Knowledge Source, but its acquisitions, projections, Artifacts, Snapshots, active state, retrieval runs, indexes, and sessions stay in that Workspace. Another Workspace using the same definition has a separate operational history.

The built-in `knowledge-projection-builtin` Flow performs identity aggregation. It deliberately preserves heterogeneous media such as JSON, HTML, text, or spreadsheets. If a Snapshot must be homogeneous, select a transformation on a binding or a specialized projection Flow that normalizes the prepared artifacts. There is no built-in transformation Flow.

Every successful projection creates and activates a distinct immutable Snapshot. A failed projection leaves the previous active Snapshot unchanged. Provenance pins the exact Data Source acquisitions, optional transformation Runs, projection Flow and Run, Artifact receipts, storage lineage, and integrity evidence.

## Retrieve only from the selected Snapshot

A Knowledge Source also selects a `knowledge.retrieval/v1` Flow. Search, query, and bounded read resolve the active or explicitly selected Snapshot before execution. Every returned item, citation, byte range, and Artifact identifier must belong to that Snapshot.

The built-in `knowledge-retrieval-builtin` Flow provides deterministic local text search, query, and read over filesystem-backed Snapshot Artifacts. Search interleaves bounded matches across the Snapshot artifacts; it is not a model-backed semantic index. Binary interpretation, spreadsheet analysis, semantic search, or answer synthesis requires a different governed retrieval composition and any required Tools.

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
