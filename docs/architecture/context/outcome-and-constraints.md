# Outcome and constraints

Agentstration is an open-source, self-hosted agent platform built on the Microsoft .NET AI stack. It is delivered first as an executable modular monolith with explicit resource-family, Work, Flow, Runtime, Identity, Tooling, and transport responsibilities.

The authoritative server remains the source of truth for definitions, immutable revisions, desired state, functional Work, Flow Runs, and technical Runtime Runs. Independently hostable Console and Workplace shells consume its public APIs and own no authoritative business store. Runtime `AIAgent` objects are reconstructed from durable definitions and are never persisted.

The executable default is local-first: .NET, SQLite or local files, bounded in-process execution, local identity, and deterministic AI are sufficient. PostgreSQL, Aspire, Compose, AEP extensions, inference servers, external OIDC, Foundry, and OTLP are optional profiles or integrations.

The architecture prioritizes workspace isolation, explicit module ownership, durable execution identities, immutable publications, provider-neutral application contracts, bounded untrusted inputs, and shared use cases across REST, UI, MCP, SignalR, and background workers.

See the [System Context and lower-level C4 views](../interactive.mdx), the [dynamic execution scenarios](../runtime/dynamic-views.md), and the [current implementation inventory](../implementation/current-state.md).
