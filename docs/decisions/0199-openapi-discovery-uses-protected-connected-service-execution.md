# ADR-0199: OpenAPI discovery uses protected Connected Service execution

## Status

Accepted

## Context

FR #1058 adapts the autonomous OpenAPI extension to ADR-0198. A discovery Flow
receives exact resource references, not connection values. The existing MCP Tool
adapter sends ordinary arguments only, while AEP Bound Values and one-use Secret
capabilities already support protected model connections. ASP does not supply a
connection-value transport and is a storage protocol, not an OpenAPI prerequisite.

## Decision

The extension contributes independent `openapi` and `openapi-operation` types and
an exact published `connected-service-operation-discovery/v1` Flow. It does not
inherit from `http-endpoint`. Its inputs are a required standard `baseUrl` binding
and an optional secured `bearerToken`; explicit candidate paths are non-sensitive
service options.

The Flow invokes the generic internal `connected-service.operations.discover`
Tool with its discovery ID and namespace. This Tool is not exposed through the public MCP
surface. The application validates the persisted discovery against the trusted
Flow Run, Principal, Tenant and operational Workspace, checks the exact service
UID/generation, type and discovery dependencies, and revalidates use policy.
The extension adapter derives the destination solely from the type's exact
extension-management authority; callers cannot supply another extension endpoint.

An opt-in `aep.connected-service-operation-discovery` capability, version `1.0`,
provides an authenticated execution-only AEP request containing the contributed
type ID/version, options, Bound Values and correlation ID. Parameters resolve
immediately before dispatch. Secrets use existing one-use capabilities, revoked
when dispatch ends; only the extension redeems the Secret. Neither values nor
grants enter Flow input, Tool arguments, execution events or operation definitions.
Existing AEP tracing redacts Bound Values. The protocol contains no product
resource assemblies or cross-extension links.

The provider returns bounded candidate definitions with contribution type IDs and
versions. The host maps them to the exact allowed local type pins before generic
materialization. Both the provider response and the mapped manifest including
exact local type evidence are limited to 48 KiB, below the AWP event payload bound. Oversized inventories
fail rather than silently truncating or declaring omitted operations unavailable.
Contract bytes remain outside the durable discovery result. Operation identity
uses contract location and operationId, independently of the mutable document digest.

Reuse the existing governed AEP bundle installation lifecycle for the new
`openapi-connected-services` bundle. Its historical `data-source-profile-bundle`
wire container additionally accepts nonempty bundles consisting exclusively of
Flows bearing the Connected Service discovery contract. It creates no Data Source
Profile. Other bundles still require a Data Source Profile. This narrow extension
does not introduce another resource installer or relax resource governance.

## Consequences

- The standalone default requires no extension or remote API.
- OpenAPI connections are independently configured product resources; extensions
  contribute types and execution, never configured instances or product writes.
- The unchanged `openapi-governed` bundle remains available for existing Data
  Source discovery/acquisition consumers. New operation catalogs use the new Flow.
- FR #1041 owns migration of acquisition composition. No historical selectors,
  consumer pins, published versions or retained data are rewritten automatically.
- This delivery does not implement dispatch for invoking a materialized operation,
  activation, or Agent Tool publication. The legacy invocation Tool does not make
  those future capabilities available through the new operation resource.
- ADR-0191 describes the retained legacy path; ADR-0198 continues to define generic
  operation discovery and materialization. Neither accepted record is rewritten.
