# System context

The canonical C4 L1 view represents Agentstration as one software system. Workplace, Console, REST, MCP, and realtime endpoints are interaction channels owned by that system, not separate external systems.

End users work through Workplace, operators and administrators govern resources and supervise execution through Console, and API consumers integrate through REST, MCP, or realtime APIs. Agentstration may call configured AI and model services during execution and may delegate authentication to an external OIDC provider.

[Open the canonical interactive System Context view](../interactive.mdx). Its elements and relationships come from the shared LikeC4 model used by the lower-level views.

AI/model services and identity providers are explicitly optional. Azure, Foundry, Ollama, Docker, remote credentials, and external identity are not mandatory system dependencies. Local identity and the deterministic provider preserve offline execution.
