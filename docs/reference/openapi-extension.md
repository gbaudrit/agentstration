# Governed OpenAPI extension

`Agentstration.Extensions.OpenApi` is an optional autonomous AEP extension for validating OpenAPI definitions, discovering bounded contracts, and invoking one explicitly promoted operation. It is not required by the standalone product and it never publishes discovered operations as Agent Tools.

## Run and enroll

Configure at least one destination domain and start the extension independently:

```powershell
$env:OpenApi__AllowedDomains__0 = "api.example.com"
dotnet run --project src/Agentstration.Extensions.OpenApi
```

The Development profile listens on `http://localhost:5310`. Enroll that endpoint like any other AEP extension. Its manifest contributes:

- schema format `openapi` with OpenAPI 3.0 and 3.1 JSON/YAML validation;
- technical Tools `openapi.contract.discover` and `openapi.operation.invoke`;
- the installable `openapi-governed` Data Source Profile bundle.

Bundle installation remains explicit until extension-scoped installation from #696 is delivered.

## Data Source configuration

The contributed profile accepts:

| Field | Meaning |
| --- | --- |
| `origin` | Allowlisted credential-free HTTP(S) origin or exact contract URL. |
| `candidatePaths` | One to eight explicit same-origin relative contract paths. Use the exact contract URL as `origin` and omit candidates only when invoking the Tool directly. |
| `credentialId` | Optional opaque identity of an operator-configured credential. Never a secret value. |
| `maximumPages` | Link-header pagination bound, from 1 through 10 by default. |
| `fixedBindings` | Operator-owned path, query, header, and body values that caller arguments cannot override. |

Run discovery from the generic Data Source page, inspect its bounded contract and operation hierarchy, and explicitly promote one eligible operation into a named acquisition target. Missing or duplicate `operationId` values are retained as diagnostics but cannot be promoted. Rediscovery never selects or updates a target automatically.

Acquisition requires a current enabled target. The extension refetches the selected contract and verifies the exact SHA-256 digest before invoking the selected server, operation, method, and path. Knowledge Sources select the named target and consume only its durable acquisition Artifacts; they never call the external origin.

## Credentials

Data Source and acquisition inputs carry only `credentialId`. Operator configuration maps that identity to a header or query credential. Inject the value through the host's protected configuration provider, environment-secret facility, or secret mount; do not commit it to configuration files. Credential values are excluded from discovery results, outputs, errors, and structured logs.

## Security and limits

The extension requires an explicit domain and port allowlist. Private destinations are disabled by default. Loopback, link-local, multicast, unspecified, documentation, benchmark, and reserved address ranges remain denied. Redirects are disabled. Every top-level contract, selected server, and pagination destination is checked independently.

The initial `$ref` policy accepts only local references. Unresolved references, cycles, excessive depth or count, external references, unsupported schemes, oversized contracts, unsupported versions, disallowed methods or content types, timeouts, large responses, and excessive pagination fail with bounded `openapi_*` codes.

Default ceilings are 8 candidates, 2 MiB per contract, 1,000 operations, 256 references, depth 16, 4 MiB per response, 10 pages, 8 connections per server, and 30 seconds per request. Operators may reduce these limits and can increase them only within the validator's hard ranges.

Discovery and invocation logs contain correlation identifiers and bounded metadata, never documents, response bodies, arguments, or credentials. Side-effecting HTTP methods remain governed by the ordinary Tool approval and execution policy.
