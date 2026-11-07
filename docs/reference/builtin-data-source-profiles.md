# Built-in Data Source Profiles

Agentstration reconciles three published Data Source Profiles at instance scope. They are executable local defaults shared with authorized tenants and Workspaces and do not require an AEP extension, a model, a cloud service, or a remote provider.

| Profile | Source configuration | Acquisition behavior |
| --- | --- | --- |
| `web-builtin` | `url`: one absolute HTTP(S) URL | Fetches one textual Web resource, stages it, and persists it through `artifact-storage-filesystem-write-builtin`. |
| `rest-builtin` | `url`: one absolute HTTP(S) URL | Performs one unauthenticated GET for a structured or textual public response, then uses the same staging and Storage Flow path. |
| `artifact-import-builtin` | `artifactIds`: up to 100 durable Artifact identifiers | Imports existing governed durable Artifacts without a network request. |

Each profile owns a configuration schema and pins an acquisition Flow with `flow.contract: datasource.acquisition/v1`. An accepted acquisition snapshots the exact Data Source and profile scopes, generations and revision, resolved Flow version, governed Tool and provider generations, limits, policies, caller, and resulting Artifact manifest. A later profile activation affects only future acquisitions.

These profiles acquire governed Artifacts; they do not create Knowledge Snapshots. A Knowledge Source separately projects the artifacts of one or more Data Sources through `knowledge.projection/v1`.

## Network limits

The Web and REST implementations accept a response of at most 1 MiB, follow at most three redirects, and time out after 20 seconds. Every initial destination and redirect is checked before connection. Loopback, private, carrier-grade NAT, link-local, multicast, documentation, benchmark, and other non-public address ranges are denied, including after DNS resolution. URLs cannot contain user information or fragments.

`web-builtin` accepts HTML, plain text, Markdown, JSON, and XML. `rest-builtin` accepts JSON, XML, and plain text. The initial REST profile does not accept credentials or arbitrary headers. Use a separately governed Tool and custom profile when a source needs authentication, private-network access, pagination, JavaScript rendering, crawling, or another media type.

## Scope, ownership, and customization

Data Sources and Data Source Profiles may be owned at instance, tenant, or Workspace scope. A Data Source may reference only a profile visible through the ancestor-only scope rules and an explicit descendant-use grant. The three core built-ins, their acquisition Flows, Tools, Tool Providers, ToolSets, and staging composition are instance-owned; their tenant grants propagate discovery and use to descendant Workspaces. Acquisition records, Flow Runs, staged content, and durable Artifacts remain isolated in the consuming Workspace.

When the Console context is Instance or Tenant, acquisition and discovery use the governed Instance operations or Tenant operations Workspace. The selected administrative scope remains the logical consumer for authorization; the operational Workspace is only the storage and execution destination. Acquisitions and discoveries retain that logical scope alongside the exact source and profile evidence, including when pending work is recovered. Their history and promoted targets are not shared with business Workspaces.

At the resource owner context, the **Access** tab administers descendant discovery and use grants independently of definition protection. This remains available for extension-managed profiles: changing a local access policy does not edit the contributed definition, revision, hash, or provenance. Profile policies identify the mutable profile by UID and generation; a published revision version is not a version of that resource identity.

The `-builtin` identities are owned by Agentstration and cannot be edited, republished, reactivated, deleted, or replaced through YAML. Their metadata includes:

```yaml
annotations:
  agentstration.io/builtin: "true"
  agentstration.io/origin: agentstration.core
  agentstration.io/owner: agentstration
```

These annotations describe provenance; the protection boundary is the reserved identity plus the system Control Plane writer. To customize acquisition, create a separate Data Source Profile and publish its own immutable revision. Existing custom profiles are never overwritten by platform reconciliation.

## Optional extension profiles

An AEP extension may contribute a declarative Data Source Profile bundle containing ToolSets, Flows, and a profile. Agentstration resolves contributed Tool bindings and installs the documents through the ordinary host-owned Bootstrap handlers; the extension never writes the resource store directly.

The optional Crawl4AI extension contributes `crawl4ai-web`. Successful enrollment installs that bundle at the exact extension-registration scope and records its governed resources in the durable extension installation inventory.

The inventory records both the extension version and the independent bundle version, exact resource identities, published versions, definition evidence, and resolved Tool bindings. It remains readable when the extension is unavailable or unenrolled. The Console shows this ownership on Data Source Profile lists and details and links back to the exact managing registration.

An interrupted installation retains its partial inventory and can complete missing publications on retry. An already installed bundle is a no-op only when its recorded identities, generations, versions, owners, and definition evidence still match the stored resources. A mismatch marks the installation degraded and returns a stable conflict instead of adopting, overwriting, or silently repairing the changed resource. Ordinary administration cannot mutate the managed resources or their published versions, including after unenrollment.

The optional OpenAPI extension contributes `openapi-governed`. Its discovery Flow returns bounded hierarchical operation choices, and its acquisition Flow requires an explicitly promoted current target before invoking and persisting a response. See [Governed OpenAPI extension](openapi-extension.md).

