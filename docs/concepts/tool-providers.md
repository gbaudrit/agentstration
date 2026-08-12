# Tool Providers and governed tools

A `ToolProvider` is a configured source of tools. V1 supports AEP extensions and generic MCP servers over STDIO or Streamable HTTP. A provider owns connection and discovery configuration; it does not grant an agent permission to use anything.

Discovery materializes every announced capability as an `Agentstration.Tools/tools` resource. New resources are `discovered = true`, `available = true`, and `enabled = false`. Refresh updates provider-owned descriptions, metadata and MCP schemas while preserving the administrator-owned `enabled` flag. A missing tool remains persisted with `available = false`; reappearance restores availability.

```text
provider enabled
  AND tool enabled
  AND tool available
  AND canonical Tool resource assigned to Agent
  -> exposed to MAF
```

MCP remains authoritative for protocol negotiation, `tools/list`, schemas, invocation and results. AEP contributes only extension identity, display metadata and mappings to MCP. Agent definitions never contain a command, endpoint or raw MCP declaration.

STDIO environment values are not persisted. `environmentReferences` maps child-process variable names to host configuration keys, resolved only when connecting. OAuth, a durable secret store, scheduled polling, MCP Resources and MCP Prompts are outside this iteration.
