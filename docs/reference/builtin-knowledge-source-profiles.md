# Built-in Knowledge Source Profiles

Agentstration reconciles three published Knowledge Source Profiles into every Workspace. They are executable local defaults and do not require an AEP extension, a model, a cloud service, or a remote provider.

| Profile | Source configuration | Acquisition behavior |
| --- | --- | --- |
| `web-builtin` | `url`: one absolute HTTP(S) URL | Fetches one textual Web resource, stages it, and persists it through `artifact-storage-filesystem-write-builtin`. |
| `rest-builtin` | `url`: one absolute HTTP(S) URL | Performs one unauthenticated GET for a structured or textual public response, then uses the same staging and Storage Flow path. |
| `artifact-import-builtin` | `artifactIds`: up to 100 durable Artifact identifiers | Imports existing governed durable Artifacts without a network request. |

All three profiles use `knowledge-retrieval-builtin` for bounded local search, query, and read over the selected immutable Snapshot. Their published revisions retain exact Flow, Tool, provider, and ToolSet provenance.

## Network limits

The Web and REST implementations accept a response of at most 1 MiB, follow at most three redirects, and time out after 20 seconds. Every initial destination and redirect is checked before connection. Loopback, private, carrier-grade NAT, link-local, multicast, documentation, benchmark, and other non-public address ranges are denied, including after DNS resolution. URLs cannot contain user information or fragments.

`web-builtin` accepts HTML, plain text, Markdown, JSON, and XML. `rest-builtin` accepts JSON, XML, and plain text. The initial REST profile does not accept credentials or arbitrary headers. Use a separately governed Tool and custom profile when a source needs authentication, private-network access, pagination, JavaScript rendering, crawling, or another media type.

## Ownership and customization

The `-builtin` identities are owned by Agentstration and cannot be edited, republished, reactivated, deleted, or replaced through YAML. Their metadata includes:

```yaml
annotations:
  agentstration.io/builtin: "true"
  agentstration.io/origin: core
  agentstration.io/owner: agentstration.knowledge
```

These annotations describe provenance; the protection boundary is the reserved identity plus the system Control Plane writer. To customize a composition, create a separate Knowledge Source Profile and either author its dependencies directly or apply a published built-in revision to it. Existing custom profiles are never overwritten by Workspace reconciliation.

Startup repairs a missing initial revision for the current definition, but it does not silently switch an existing built-in profile to a newer implementation. A later core definition must introduce an explicit upgrade operation that adds an immutable revision and preserves every revision already referenced by acquisitions, Snapshots, and retrieval runs.

