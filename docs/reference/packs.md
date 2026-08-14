# Pack format and lifecycle

> **Status:** implemented local V1 contract. Advanced dependencies, updates, signatures, Gallery, and Marketplace behavior remain planned.

## Definition

An Agentstration Pack is a versioned, distributable unit that groups a coherent set of zero or more Agentstration resources with the metadata needed to install, configure, update, and uninstall them. A one-resource Pack is valid; the format does not impose artificial granularity.

The Pack belongs to the Management/distribution plane and is not a Runtime primitive.

Every installation uses the namespace `publisher.name`. For example, `agentstration/who-am-i` installs into `agentstration.who-am-i`. Publisher and Pack names use lowercase ASCII letters, digits and `-`, which makes this mapping direct and collision-free. Resource `metadata.name` values are not prefixed or rewritten.

## Archive

```text
price-watch/
├── pack.yaml
├── README.md
├── flows/
│   └── price-watch.yaml
├── agents/
│   └── product-analyzer.yaml
└── entries/
    └── price-watch.yaml
```

The manifest shape is:

```yaml
apiVersion: agentstration.io/v1
kind: Pack
metadata:
  name: price-watch
  publisher: agentstration
  version: 1.0.0
  displayName: Price Watch
  description: Monitor product prices and notify at a configured threshold.
  categories: [shopping, automation]
  tags: [price, monitoring]
spec:
  resources:
    - flows/price-watch.yaml
    - agents/product-analyzer.yaml
    - entries/price-watch.yaml
```

`pack.yaml` is a distribution manifest, not a normal runtime-capable resource. Every path in `spec.resources` resolves inside the archive and points to an ordinary manifest with its own `apiVersion`, `kind`, metadata, and kind-specific body. Absolute paths, parent traversal, links escaping the archive, duplicate canonical resources, and unbounded archive expansion must be rejected.

## Versioning

The Pack version initially follows Semantic Versioning. It versions the bundle, not its contained resources. These values remain independent:

| Value | Meaning |
| --- | --- |
| Pack version | Version of the distributed set. |
| Resource `apiVersion` | Schema used to read one manifest. |
| Resource generation, ETag, or version | Lifecycle or concurrency value owned by that resource's module. |
| Product version | Version of Agentstration itself. |

## Installed state and provenance

The instance records an `InstalledPack` management record containing at least:

- publisher, Pack name, and installed version;
- the deterministic installation namespace;
- installation source and timestamp;
- explicit installation state;
- the managed resources and their installed fingerprints or revisions.

Per-resource provenance must answer which Pack owns the resource, which Pack version supplied it, and whether the current resource still matches the installed content. This evidence enables later `managed`, `detached`, `overridden`, or `conflict` policies without silently destroying local changes.

## Supported resources

Local V1 installs these resource kinds in dependency-safe order:

1. `ModelProvider`;
2. `RuntimeProfile`;
3. `ModelProfile`;
4. `Agent`;
5. `Flow`;
6. `Entry`.

Every contained resource uses `apiVersion: agentstration.io/v1`, the declared `kind`, `metadata.name`, and a typed `definition`. Management resources retain their normal definition shape. Pack Flow and Entry definitions use their existing domain properties under the same envelope. Unsupported kinds are rejected before any mutation.

Unqualified references between contained resources resolve relative to the Pack namespace. A dependency outside the Pack must carry an explicit namespace; shared standalone resources normally use `namespace: default`. A fork changes the Pack coordinate and therefore installs the same local resource names into a different namespace without conflicting with its source.

## Requirements and configuration

A manifest may describe dependencies on another Pack, a platform capability, or an integration/provider capability. Local V1 rejects every non-empty `requirements` list because dependency resolution is not yet available; it does not silently install or configure a sensitive integration. Pack-to-Pack and capability resolution are deferred.

An installation may consequently be ready, need configuration, or report a missing requirement. Pack-wide configuration remains distinct from Work input: global limits and connections belong to installation configuration, while values for one Task or Automation belong to that Work instance.

## V1 lifecycle

```text
Read local archive
  -> validate archive and manifest
  -> validate every resource with its owning module
  -> validate supported requirements
  -> apply resources
  -> record provenance
  -> record InstalledPack
```

Validation completes before mutation. Application must be an explicit durable operation: it either succeeds coherently or records enough state to compensate or resume. Direct cross-store writes that bypass module validation are not allowed.

Uninstall checks Pack dependencies, live resource references, ownership, and local modifications. It removes only still-managed resources and then the InstalledPack record. Detached or changed resources are preserved unless a later explicit conflict policy authorizes another outcome.

The executable local increment covers:

1. a local archive and `Pack` manifest;
2. bounded archive and manifest validation;
3. resource installation through owning services;
4. per-resource provenance;
5. installed-Pack listing;
6. safe uninstall;
7. independent Pack versions.

Advanced dependencies, update/merge policy, signatures, verified publishers, Gallery clients, ratings, licensing, payment, and Marketplace policy are outside V1.

## HTTP API

The authorized Management API exposes:

```text
POST   /api/packs/preview
POST   /api/packs
GET    /api/packs
GET    /api/packs/{publisher}/{name}
DELETE /api/packs/{publisher}/{name}
```

`POST /api/packs/preview` and `POST /api/packs` accept a ZIP body with `Content-Type: application/zip` or `application/octet-stream`. Preview performs the complete archive and resource validation, reports existing-resource conflicts, and makes no changes. `X-Pack-File-Name` records a display-safe local source name. Compressed input is limited to 8 MiB; the reader permits at most 128 files, 4 MiB per expanded file, and 16 MiB total expanded content. Absolute paths, parent traversal, duplicate paths, and symbolic links are rejected.

Install the version-controlled offline example from the repository root:

```powershell
Compress-Archive -Path samples/packs/offline-runtime/* -DestinationPath offline-runtime.zip
Invoke-RestMethod -Method Post -ContentType application/zip -InFile offline-runtime.zip -Headers @{ "X-Pack-File-Name" = "offline-runtime.zip" } http://localhost:5100/api/packs
Invoke-RestMethod http://localhost:5100/api/packs
Invoke-RestMethod -Method Delete http://localhost:5100/api/packs/agentstration/offline-runtime
```

Installation refuses to replace an existing resource in the same namespace. It records progress after every applied resource and compensates in reverse order on failure. Uninstall compares each current ETag or module version token with its installation evidence; modified resources are preserved and the Pack becomes `degraded`.

The repository also contains `samples/packs/who-am-i`, a five-resource distribution smoke test with three role-specific Agents, a Direct Flow, and a conversational Entry. It deliberately documents rather than conceals the current execution gaps: Agent deployment, Workspace exposure, multi-agent turns, private state, and generic human-input suspension are not supplied by Pack V1.

## Distribution and trust

A Pack can eventually come from a local file, URL, repository, private Gallery, or public Gallery. The format does not depend on any one source and local offline installation remains required.

Packs are untrusted input. Installation must show the publisher, resources to be created or changed, declared requirements, and relevant tools or integrations before applying sensitive changes. Hashes, signatures, verified publishers, permissions, and trust levels are later hardening layers, not reasons to make remote distribution mandatory.
