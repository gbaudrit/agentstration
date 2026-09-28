# Architecture overview

The `docs/architecture/` section is the canonical Agentstration Technical Architecture Document (DAT). [Architecture documentation conventions](conventions.md) define the ownership boundaries between LikeC4, Markdown, Mermaid, and ADRs, the [interactive architecture](interactive.mdx) publishes the shared structural model, and the [migration inventory](migration-inventory.md) tracks the progressive consolidation of existing sources.

Agentstration is a modular monolith with explicit resource-family, Runtime, Work, and Flow boundaries. The repository contains one authoritative server/API, independently hostable Console and Workplace shells, autonomous AEP extensions, and an Aspire development orchestrator. The shared [interactive C4 model](interactive.mdx) is the sole structural source for L1 through selected L3 views.

The DAT is organized by concern:

- [Context and constraints](context/outcome-and-constraints.md)
- [System context](c4/system-context.md), [containers](c4/containers.md), [server components](c4/server-components.md), and [API transport composition](c4/api-composition.mdx)
- [Dynamic execution views](runtime/dynamic-views.md), including Work, Flow, Agent, Tool, AEP, and MCP paths
- [Resource and persistence models](data/resource-model.md)
- [Standalone deployment](deployment/standalone.md) and [tenancy/isolation](cross-cutting/tenancy-and-isolation.md)
- [Implementation mapping](implementation/project-structure.md), [dependency rules](implementation/dependency-rules.md), the [detailed current-state inventory](implementation/current-state.md), and the [initial reconciliation baseline](implementation/reconciliation-baseline-2026-09-28.md)

The dominant design rules are local-first operation, provider-neutral application contracts, separate persistence boundaries, reconstructible runtime objects, and shared use cases across REST, Razor, MCP, and workers.

Packs form a distribution layer above these resource owners. Installation delegates each contained manifest to its owning module and records provenance; Packs never enter the execution path. See [Packs](../concepts/packs.md) and [ADR-0037](../decisions/0037-packs-are-management-distribution-artifacts.md).

The detailed implementation inventory remains available in [Architecture: current implementation](implementation/current-state.md).
