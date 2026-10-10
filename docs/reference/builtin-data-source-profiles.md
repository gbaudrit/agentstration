# Built-in Data Source Profiles

Agentstration reconciles three published Data Source Profiles into every Workspace. They are executable local defaults and do not require an AEP extension, a model, a cloud service, or a remote provider.

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

Data Sources and Data Source Profiles may be owned at instance, tenant, or Workspace scope. A Data Source may reference only a profile visible through the ancestor-only scope rules. Acquisition always executes in a consuming Workspace because Flows, Tools, Tool Providers, staging, and durable Artifact stores are Workspace-owned.

The `-builtin` identities are owned by Agentstration and cannot be edited, republished, reactivated, deleted, or replaced through YAML. Their metadata includes:

```yaml
annotations:
  agentstration.io/builtin: "true"
  agentstration.io/origin: agentstration.core
  agentstration.io/owner: agentstration
```

These annotations describe provenance; the protection boundary is the reserved identity plus the system Control Plane writer. To customize acquisition, create a separate Data Source Profile and publish its own immutable revision. Existing custom profiles are never overwritten by Workspace reconciliation.

## Optional extension profiles

An AEP extension may contribute a declarative Data Source Profile bundle containing ToolSets, Flows, and a profile. Agentstration resolves contributed Tool bindings and installs the documents through the ordinary host-owned Bootstrap handlers; the extension never writes the resource store directly.

The optional Crawl4AI extension contributes `crawl4ai-web`. Installing that bundle is an explicit operation today. Automatic installation in the extension's scope during enrollment is tracked independently by [#696](https://github.com/gbaudrit/agentstration/issues/696).

