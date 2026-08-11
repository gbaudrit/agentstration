# ADR-0028: Tool Providers materialize a governed catalog

## Status

Accepted — 2026-08-11

## Context

ADR-0027 established AEP-to-MCP mappings and direct MCP support, but treating a server declaration as the runtime source left discovery transient and did not model provider enablement, disappearance, or administrator approval. Agentstration needs one source abstraction without converting external MCP through AEP or making agents aware of transports.

## Decision

- `Agentstration.Tools/toolProviders` is the first-class source resource. V1 provider families are AEP and MCP only.
- MCP providers support STDIO and Streamable HTTP through the official `ModelContextProtocol` SDK. STDIO environment configuration persists references to host configuration keys, never resolved secret values.
- AEP providers discover lightweight contributions, resolve their declared MCP servers, and obtain schemas through MCP `tools/list`. MCP providers call `tools/list` directly.
- Discovery materializes and updates `Agentstration.Tools/tools`. Existence means discovered; `enabled` and `available` are independent booleans. New tools are always disabled. Missing tools become unavailable rather than being deleted, and reappearing tools become available again.
- Refresh preserves administrator-owned enablement and returns new, changed, unchanged, and unavailable counts. V1 performs discovery on create/configuration and on manual refresh; it has no polling scheduler.
- Runtime resolves assigned canonical Tool IDs, then requires provider enabled, tool enabled, and tool available. The provider adapter supplies the official MCP `AITool` to MAF.
- The Console organizes Tools into Providers and Catalog, supports connection tests, refresh, inspection, enablement and Agent assignment.
- `Agentstration.Extensions.Utilities` is a small autonomous AEP/MCP sample with deterministic hash, JSON and text tools.

## Consequences

External MCP servers remain native MCP integrations and AEP remains an integration descriptor rather than a competing tool protocol. Tool identity and authorization survive provider outages and schema changes. Discovery requires executing an administrator-configured STDIO command or contacting an HTTP endpoint, so it is explicit, bounded by cancellation/timeouts, and disabled tools never become usable automatically. The earlier standalone `McpServerResource` shape remains readable for compatibility but ToolProvider is the primary management model and supersedes direct server resources for new configuration.
