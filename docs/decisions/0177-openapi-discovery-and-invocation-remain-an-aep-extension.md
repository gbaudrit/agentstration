# ADR-0177: OpenAPI discovery and invocation remain an AEP extension

## Status

Accepted

## Context

ADR-0169 through ADR-0171 make Schema resources format-neutral and allow an AEP extension to contribute governed format validation. ADR-0172 lets Data Sources discover provider-neutral choices and promote one opaque selector into a named acquisition target. OpenAPI adds protocol parsing, remote contract acquisition, reference handling, credentials, and HTTP invocation; making those concerns built in would add an optional network protocol and parser to the standalone core.

OpenAPI does not define a universal discovery URL. A discovered operation must also not become executable merely because its contract was observed.

## Decision

Ship OpenAPI support as the independently deployable `Agentstration.Extensions.OpenApi` AEP extension. It advertises one `openapi` schema-format contribution and two technical MCP Tool contributions: bounded contract discovery and exact selected-operation invocation. The extension additionally publishes a Data Source Profile bundle composed against `datasource.discovery/v1` and `datasource.acquisition/v1`.

Discovery accepts one allowlisted origin and an explicit bounded list of same-origin candidate paths. It does not crawl links or guess conventional paths. JSON and YAML OpenAPI 3.0 and 3.1 documents are retained exactly in the discovery Tool result, digested with SHA-256, and described with bounded server and operation metadata. Missing or duplicate `operationId` values remain visible but ineligible. Contract and operation choices form a bounded hierarchy. Promotion remains an Agentstration Data Source operation and snapshots only the extension-owned opaque selector.

The initial external-reference policy rejects every non-local `$ref`. Local references are bounded by count and depth, must resolve, and may not form cycles. This deliberately strict policy prevents the parser from becoming an ungoverned secondary HTTP client; a future relaxation requires the same destination, DNS, size, count, depth, and cycle controls as top-level discovery.

Invocation requires the exact contract URI and digest plus the discovered server, unique `operationId`, method, and path. The extension refetches the contract, verifies its digest, reparses it, and rejects selector drift. Operator-owned fixed path, query, header, and body bindings cannot be overridden by caller arguments. Credentials are selected only by an opaque operator-configured identity and are never accepted as caller values or returned in results, errors, logs, discovery content, or provenance. Deployment must inject credential values through a protected configuration provider or secret mount; Data Source configuration retains only the identity.

The transport denies redirects, validates every DNS result and pagination destination, bounds connections, timeouts, documents, responses, candidates, operations, references, and pages, and allows only configured methods and media types. Side-effect metadata is inferred conservatively from the HTTP method and remains subject to the governing Tool approval and execution policy. Stable `openapi_*` error codes separate invalid contracts, policy denials, authentication, authorization, rate limiting, timeout, invalid responses, and unavailable services.

The extension does not publish discovered operations as Tools and never writes directly to Data Source, Knowledge, Schema, Tool, Flow, Artifact, or Resource stores. Agentstration remains authoritative for bundle installation, Flow execution, target promotion, acquisition evidence, Artifact persistence, Knowledge projection, authorization, and Workspace isolation.

## Consequences

- The default local product retains its built-in JSON Schema provider and has no OpenAPI or remote-service dependency.
- Installing the extension adds only two reusable technical Tools; discovery cannot silently expand Agent or Flow authority.
- Data Source and Knowledge contracts remain provider-neutral and contain no OpenAPI fields.
- External references are intentionally unavailable in this increment instead of being followed without complete governance.
- Contract changes invalidate exact selectors until an administrator rediscovers and explicitly reconciles a target.
