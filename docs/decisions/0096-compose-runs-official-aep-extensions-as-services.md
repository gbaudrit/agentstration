# ADR-0096 — Compose runs official AEP extensions as internal services

## Status

Partially superseded by [ADR-0097](0097-compose-owns-its-local-ollama-service.md) and [ADR-0098](0098-provider-specific-compose-owns-inference-services.md)

## Context

AEP extensions are autonomous HTTP services, but the Compose topology previously started only the authoritative Agentstration server. The Aspire AppHost already starts the official Ollama, llama.cpp, LocalAI, and Utilities extensions as separate processes and injects their endpoints. A container launch therefore did not expose the same extension capabilities and required operators to start every extension manually.

The native inference servers have a different lifecycle. They may already run on the host, hold large model stores, or be administered independently. Bundling them with the extension containers would duplicate ownership and conflict with the local-first decisions for Ollama, llama.cpp, and LocalAI.

## Decision

- Provider-specific `deploy/compose/ollama.yml`, `llama-cpp.yml`, and `localai.yml` topologies build and run only the selected model-provider extension plus the provider-neutral Utilities extension. `deploy/compose/base.yml` remains the minimal deterministic launch.
- Agentstration receives the selected model-provider and Utilities AEP service URLs through its existing `Agentstration:Extensions` configuration. Compose does not introduce a second registry or persist container identity in Management resources.
- Compose does not download a model, publish extension ports to the host, or manage third-party extension installation and upgrades.
- One parameterized extension Dockerfile provides a common build/runtime shape while each service still publishes and starts its own autonomous project.
- The normal Compose execution mode is `Managed`. Operators can explicitly select `Deterministic` through `AI_PROVIDER` when they need the offline fallback.

## Consequences

Selecting one provider-specific Compose file provides extension discovery and governed AEP tool/model-provider integration without requiring host-side extension processes. Extensions remain independently replaceable network services. Startup ordering guarantees only that extension containers are started before Agentstration; live AEP health and provider availability continue to be observed through discovery and provider diagnostics.

This extends ADR-0029, ADR-0061, and ADR-0067 to the Compose topology without changing their external inference-server ownership decisions.

ADR-0097 and ADR-0098 subsequently place inference services and their model storage inside the provider-specific Compose topologies while leaving Aspire's external ownership unchanged.
