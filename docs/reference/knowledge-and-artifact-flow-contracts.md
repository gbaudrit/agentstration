# Data Source, Knowledge, and Artifact Flow contracts

Published Flows advertise one primary contract through `definition.metadata.flow.contract`. The metadata key belongs to Flow; the value and schemas belong to the resource family that invokes the Flow.

```yaml
apiVersion: agentstration.io/v1
kind: Flow
metadata:
  name: example-acquisition
definition:
  metadata:
    flow.contract: datasource.acquisition/v1
  # graph, publish, and activation fields omitted
```

The classification does not grant Tool authority. The published Flow still pins its Tools or exact ToolSet routes, and every invocation crosses the governed Tool pipeline.

## Artifact manifest

Acquisition, transformation, and projection Flows return a bounded object with an `artifacts` array. Each element requires the durable Artifact identity and declares how the caller may use it:

```yaml
artifacts:
  - artifactId: "<durable-artifact-id>"
    kind: durable
    disposition: publishable
    name: documentation.html
    mediaType: text/html
    digest: "<sha256>"
```

Knowledge transformation and projection accept only durable, publishable outputs for Snapshot publication. The services resolve each identifier through the Artifact boundary and verify Workspace, storage lineage, length, and integrity; the Flow cannot establish provenance by returning arbitrary metadata.

## Contract roles

### `datasource.acquisition/v1`

The Data Source service supplies trusted source identity and generation, exact profile evidence, source configuration, caller parameters and identity, correlation identity, and acquisition identity. The Flow returns an Artifact manifest. Origin configuration is data, not authority: executable Tools remain fixed by the published Flow and resolved profile.

### `artifact.transform/v1`

An optional Knowledge binding transformation receives the binding name, exact Data Source acquisition evidence, acquired Artifacts, binding configuration, caller, correlation, and transformation identity. It returns a new Artifact manifest. Omitting the transformation passes the acquired references through unchanged.

### `knowledge.projection/v1`

The projection receives the Knowledge Source identity and generation, the complete per-binding evidence, the flattened prepared Artifact set, caller parameters and identity, correlation identity, and projection identity. It runs once for all bindings and returns the only Artifact manifest eligible for the new Snapshot.

### `knowledge.retrieval/v1`

The retrieval Flow receives the selected immutable Snapshot and its Artifact receipts, a trusted operation (`search`, `query`, or `read`), the bounded request, caller execution evidence, correlation identity, and retrieval identity. It returns bounded items, citations, an optional answer, and an optional continuation token. Every returned Artifact or citation must belong to the selected Snapshot.

Retrieval Flows also declare their supported operations through `definition.metadata.knowledge.capabilities`, using a comma-separated subset of `knowledge.search/v1`, `knowledge.query/v1`, and `knowledge.read/v1`.

### Artifact storage contracts

`artifact.storage.write/v1` receives a staged Artifact identity plus the authoritative producer Flow Run and step identities. It returns a normalized durable receipt used to complete a `FlowRunArtifact`. `artifact.storage.read/v1` performs the inverse operation and materializes durable content as a new governed local copy.

## Capture a step result as an Artifact

Agent, Tool, Tool-route, child-Flow, repeat, and Flow-output steps may capture a successful result without adding another graph node:

```yaml
- type: tool
  name: fetch
  displayName: Fetch document
  tool: { resourceId: http-get }
  artifactOutput:
    fileName: ${step.name}
    mediaType: application/json
    contentMapping: ${step.output}
    maximumBytes: 1048576
    clean: auto
    storageFlow:
      resourceId: artifact-storage-filesystem-write-builtin
      namespace: default
      versionStrategy: active
```

`contentMapping` may select the current raw result through `${step.output...}` or an earlier result through `${steps.<name>.output...}`. `${step.name}` and `${step.displayName}` are available for file metadata. When `fileName` is omitted, Agentstration uses the technical step name and infers an extension from `mediaType`; an explicit name such as `toto` remains extensionless.

`contentEncoding` accepts `auto`, `json`, `utf8`, or `base64`. In `auto`, JSON-compatible media types serialize JSON, a string result becomes UTF-8 text, and other binary content must be explicitly base64. MCP-aligned `content` and `structuredContent` normalization is not part of this contract and remains tracked by [#737](https://github.com/gbaudrit/agentstration/issues/737).

`clean` accepts `auto`, `true`, or `false`. `auto` is the default and removes the local staged copy when the containing Flow reaches any terminal state, whether or not a Storage Flow is configured. `true` fixes the same cleanup behavior explicitly; `false` retains the copy until ordinary retention expires it. A requested capture or storage failure fails the Flow instead of silently discarding the Artifact.

See [ADR-0150](../decisions/0150-artifacts-use-toolset-backed-staging-and-storage-flows.md), [ADR-0164](../decisions/0164-flow-steps-optionally-capture-results-as-artifacts.md), [ADR-0165](../decisions/0165-flow-contracts-use-one-flow-owned-metadata-key.md), and [ADR-0166](../decisions/0166-knowledge-sources-project-data-source-artifacts.md).
