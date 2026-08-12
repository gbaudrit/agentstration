# AEP Inspector workbench

The Inspector answers two questions: what does this extension declare, and does the declared behavior actually work? It is intentionally a protocol workbench rather than an Agentstration administration screen.

## Model playground

When `aep.model-provider` is present, the Inspector lists providers, checks provider health, discovers models, and sends chat or streaming chat requests. System instructions, temperature, output-token limits and cancellation exercise the canonical AEP request path.

## Tool explorer

When `aep.tools` is present, the Inspector resolves the manifest's AEP-to-MCP mappings, obtains authoritative schemas from MCP `tools/list`, and invokes tools through MCP `tools/call`. Input can be entered as JSON or through a form generated from top-level JSON Schema properties.

## Diagnostics

The right-hand panel records bounded AEP and MCP HTTP exchanges. Authorization, cookies, tokens, passwords, secrets and API-key-shaped JSON fields are redacted before reaching session state. Streaming bodies are not buffered; their trace records transport metadata and a streaming marker.

## Configuration

The workbench only enables configuration editing when a capability contract publishes enough schema and secret metadata to do so safely. A provider-specific hard-coded form is considered a protocol defect, not an Inspector feature.
