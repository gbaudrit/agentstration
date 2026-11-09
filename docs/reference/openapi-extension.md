# Governed OpenAPI extension

`Agentstration.Extensions.OpenApi` is an optional autonomous AEP extension for validating OpenAPI definitions, discovering bounded contracts, and invoking one explicitly promoted operation. It is not required by the standalone product and it never publishes discovered operations as Agent Tools.

## Run and enroll

Configure at least one destination domain and start the extension independently:

```powershell
dotnet run --project src/Agentstration.Extensions.OpenApi --launch-profile http `
  -e OpenApi__AllowedDomains__0=api.example.com
```

The Development profile listens on `http://localhost:5310`. Enroll that endpoint like any other AEP extension. Its manifest contributes:

- schema format `openapi` with OpenAPI 3.0 and 3.1 JSON/YAML validation;
- technical Tools `openapi.contract.discover` and `openapi.operation.invoke`;
- autonomous `openapi` Connected Service and `openapi-operation` operation types;
- the `openapi-connected-services` discovery Flow bundle and retained
  `openapi-governed` Data Source Profile bundle.

Enrollment installs the advertised bundle at the exact Extension Registration scope (Instance, Tenant, or Workspace), resolves and enables only its two required technical Tools, and records a durable governed inventory. Its ToolSet, published Flows, and Data Source Profile remain read-only through ordinary administration, including while the extension is offline. Identical enrollment retries are idempotent; ownership or definition drift produces a conflict. The former `openapi-testing` and `crawl4ai-testing` bootstrap profiles and temporary Tool activation bridge have been removed in favor of this lifecycle.

For the Aspire development topology, set `OpenApi:Enabled=true` and provide at least one `OpenApi:AllowedDomains` entry. The flag is `false` by default, so the standalone topology remains offline and does not launch this optional extension. The AppHost forwards the configured domain, port, method, media-type and execution bounds, enrolls the extension through the selected development AEP enrollment mode, and registers it with the Console automatically.

```powershell
$env:OpenApi__Enabled = "true"
$env:OpenApi__AllowedDomains__0 = "api.example.test"
dotnet run --project src/Agentstration.AppHost
```

### Pair with an existing instance

The `existing-instance` launch profile starts only the extension at
`http://localhost:5311` and announces it to the running Agentstration API at
`http://localhost:5100`. The target slot is selected by its API URL. The
profile stores its pairing state under
`.agentstration/slots/main/aep-pairing/openapi-standalone.json`.

For a different local slot, supply its API URL, a separate pairing state file
and the allowed domain for your OpenAPI documents:

```powershell
dotnet run --project src/Agentstration.Extensions.OpenApi --launch-profile existing-instance `
  -e Aep__PairingCode__AuthorityUrl=http://localhost:5200 `
  -e Aep__PairingCode__StateFile=../../.agentstration/slots/integration/aep-pairing/openapi-standalone.json `
  -e OpenApi__AllowedDomains__0=api.example.com
```

On the target instance, a Platform administrator assigns the candidate under
**Extensions → Enrollment inbox** and enters the one-time code in the extension
form. Keep a separate state file for each authority and reuse it on restart.
For an authority on another host, use HTTPS, set
`Aep__PairingCode__AllowInsecureHttp=false`, and set
`Aep__PairingCode__PublicEndpoint` to an HTTPS origin that reaches the extension
from the authority and the administrator's browser. See the
[AEP transport configuration](../getting-started/configuration.md#aep-outbound-transport)
for endpoint allowlists.

To test Connected Service operation discovery, use an authority build that
includes FR #1058 and its server-side discovery Tool.

## Connected Service operation discovery

The extension also contributes an autonomous `ConnectedServiceType` named
`openapi`, a `ConnectedServiceOperationType` named `openapi-operation`, and the
`openapi-connected-services` bundle containing a published discovery Flow.
These contributions are independent of the builtin HTTP type and of other
extensions. Enrollment installs operation types, bundle Flows, then service types
with exact local revision pins.

Create a Connected Service using the exact enrolled `openapi` type revision.
Bind `baseUrl` to a scoped string Parameter and optionally bind `bearerToken` to
a Secret. Its options contain `candidatePaths`, an explicit array of one through
eight same-origin relative paths. Origin and credentials belong to the service,
not a Data Source or ordinary Flow input. The extension applies its normal
destination and contract policies. Authenticated contract retrieval uses the
optional Bearer credential through the existing one-use AEP Secret callback;
configure `Agentstration:Aep:SecretAccess:PublicBaseUrl` on the authority as for
other AEP Secret consumers.

Start discovery through the generic Connected Services API, in an authorized
operational Workspace. The Flow is marked
`connected-service-operation-discovery/v1` and receives reference-only input.
Its internal Tool can execute only the matching persisted discovery/Flow Run.
The server resolves the configured connection immediately before protected AEP
dispatch and maps provider candidates to exact allowed operation-type revisions.
Results contain bounded operation metadata, exact contract digests and diagnostics,
not document content or credentials. The provider manifest limit is 48 KiB;
larger catalogs fail explicitly and are not silently truncated.
Metadata that reflects the supplied Bearer credential is rejected before it can
enter durable discovery evidence.

Successful discoveries materialize read-only operations. Rediscovery retains
stable identities for the same contract location/operationId, updates observed
definition evidence, and marks disappeared operations unavailable through the
generic lifecycle. Discovery neither invokes a business operation nor publishes
an Agent Tool. Invocation dispatch for these materialized resources remains a
separate delivery; the existing `openapi.operation.invoke` Tool is the retained
Data Source acquisition path, not that dispatch API.

## Migration inventory

| Existing element | Treatment in this delivery |
| --- | --- |
| `openapi-governed` bundle and its published versions | Retained unchanged for existing consumers; the Connected Service Flow is installed in a separate bundle. |
| `openapi-discovery` / `datasource.discovery/v1`, hierarchical choices and promoted selectors | Legacy compatibility path retained until explicit consumer migration in #1041; new service operation discovery uses `openapi-operation-discovery`. |
| Data Source `origin`, `candidatePaths` and `credentialId` | Retained for legacy consumers. New connections use Connected Service options and Parameter/Secret bindings; no values or references are silently copied. |
| `openapi-acquisition`, fixed bindings, invocation Tool and Artifact persistence | Retained; operation invocation/acquisition adoption is coordinated with #1041. |
| Existing targets, immutable Flow versions, discovery/acquisition histories | Preserved without automatic replacement or repinning. |
| Parser, destination policy, schema-format contribution and technical Tools | Reused; no direct extension dependency is introduced. |

Operators should enroll/reconcile the extension to add its new contributions,
create explicit Connected Services and run their discovery. This does not
authorize deletion of old bundles or migration of their dependent resources.
ADR-0201 records the protected execution boundary and this transition.

## Legacy Data Source configuration

The retained `openapi-governed` profile accepts:

| Field | Meaning |
| --- | --- |
| `origin` | Allowlisted credential-free HTTP(S) origin or exact contract URL. |
| `candidatePaths` | One to eight explicit same-origin relative contract paths. Use the exact contract URL as `origin` and omit candidates only when invoking the Tool directly. |
| `credentialId` | Optional opaque identity of an operator-configured credential. Never a secret value. |
| `maximumPages` | Link-header pagination bound, from 1 through 10 by default. |
| `fixedBindings` | Operator-owned path, query, header, and body values that caller arguments cannot override. |

Run discovery from the generic Data Source page, inspect its bounded contract and operation hierarchy, and explicitly promote one eligible operation into a named acquisition target. Missing or duplicate `operationId` values are retained as diagnostics but cannot be promoted. Rediscovery never selects or updates a target automatically.

The technical discovery Tool returns exact contract content by default. The contributed Data Source discovery Flow invokes it with `includeContracts: false`, retaining only choices, digests, and diagnostics in the Flow event so that large contracts do not exceed the AWP 64 KiB event-payload bound. Acquisition refetches the selected URI and verifies that retained digest before execution.

Acquisition requires a current enabled target. The extension refetches the selected contract and verifies the exact SHA-256 digest before invoking the selected server, operation, method, and path. Knowledge Sources select the named target and consume only its durable acquisition Artifacts; they never call the external origin.

Operation selectors use the MCP field names `contractUri`, `contractDigest`, `server`, `operationId`, `method`, and `path`. If an earlier discovery retained PascalCase selector fields such as `ContractUri`, restart the updated extension, run discovery again, and explicitly reconcile the existing acquisition target with the new choice before acquiring. Existing opaque selectors are not rewritten automatically.

## Credentials

Data Source and acquisition inputs carry only `credentialId`. Operator configuration maps that identity to a header or query credential. Inject the value through the host's protected configuration provider, environment-secret facility, or secret mount; do not commit it to configuration files. Credential values are excluded from discovery results, outputs, errors, and structured logs.

## Security and limits

The extension requires an explicit domain and port allowlist. Private destinations are disabled by default. Loopback, link-local, multicast, unspecified, documentation, benchmark, and reserved address ranges remain denied. Redirects are disabled. Every top-level contract, selected server, and pagination destination is checked independently.

The initial `$ref` policy accepts only local references. Unresolved references, cycles, excessive depth or count, external references, unsupported schemes, oversized contracts, unsupported versions, disallowed methods or content types, timeouts, large responses, and excessive pagination fail with bounded `openapi_*` codes.

For compatibility with otherwise usable OpenAPI 3 documents, the parser may validate a temporary copy with the non-standard schema declaration `type: any` removed when that is the only parser error. The retained document and digest remain unchanged, and discovery reports `openapi_compatibility_type_any`. Other invalid schema types remain rejected.

Default ceilings are 8 candidates, 2 MiB per contract, 1,000 operations, 256 references, depth 16, 4 MiB per response, 10 pages, 8 connections per server, and 30 seconds per request. Operators may reduce these limits and can increase them only within the validator's hard ranges.

Discovery and invocation logs contain correlation identifiers and bounded metadata, never documents, response bodies, arguments, or credentials. Side-effecting HTTP methods remain governed by the ordinary Tool approval and execution policy.
