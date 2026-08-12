# ADR-0027: AEP tool contributions resolve to MCP

## Status

Accepted — 2026-08-11

## Context

AEP V1 made model providers out-of-process, but agent tool references still resolved to an empty in-process catalog. Defining another schema, invocation protocol, or result envelope in AEP would duplicate MCP and couple extension discovery to runtime execution. At the same time, Agentstration must retain stable tool identity, workspace-scoped persistence, enablement, assignment, and future governance independently of an extension process or MAF.

## Decision

- An AEP descriptor may declare multiple MCP servers and lightweight tool contributions. A contribution contains only its extension-local id, display metadata, arbitrary metadata, and `mcp.server`/`mcp.tool` mapping. AEP tool contributions never contain input/output schemas.
- MCP is authoritative for discovery, schemas, annotations, calls, results, and operational errors. Agentstration uses the official `ModelContextProtocol` 2.0 SDK for Streamable HTTP discovery and invocation.
- Management persists `Agentstration.Tools/tools` resources separately from `Agentstration.Integrations/mcpServers`. A Tool resource selects exactly one source: an AEP extension/tool-type reference or a direct MCP server/tool reference. Agents continue to store only canonical Tool resource IDs.
- `Agentstration.Tools.Mcp` owns the runtime-independent Tool Catalog. Resolution follows Tool resource → AEP contribution → MCP server/tool, then merges Agentstration/AEP presentation metadata with MCP schemas. A direct Tool → MCP server/tool path preserves support for external MCP servers that do not implement AEP.
- The catalog exposes provider-neutral `IAgentTool` values. Its MCP implementation also exposes the SDK's native `AITool` through a neutral service lookup; only `Agentstration.Runtime.AgentFramework` consumes that optional runtime shape. No AEP or Tool Catalog contract references MAF.
- AEP extension base URLs are host configuration (`Agentstration:Extensions:{extensionId}:Endpoint`). AEP-relative MCP endpoints resolve against that URL; persisted direct endpoints and absolute AEP endpoints must be HTTP(S).
- AEP chat tool definitions/calls/results remain the model-provider function-calling exchange from ADR-0026. They do not define operational extension tools.

## Consequences

An extension can add or rename MCP-backed contributions without loading code into Agentstration, while agents keep stable governed Tool resource IDs. MCP schema changes are observed dynamically from the server instead of being copied into AEP or persisted as a second authority. Resolution now performs network discovery and can fail explicitly for missing resources, disabled mappings, invalid extension identity, malformed descriptors, unreachable servers, or absent MCP tools. Connection credentials, fine-grained authorization, schema caching, and richer policy remain later increments.
