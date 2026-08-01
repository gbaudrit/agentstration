# ADR-0003: Microsoft.Extensions.AI boundary

Status: Accepted — 2026-07-31

Application orchestration depends on `IAgentRuntime`; infrastructure adapts it to `IChatClient` and marks the Microsoft Agent Framework native boundary. A deterministic `IChatClient` is the default, and an OpenAI-compatible client supports Ollama or remote providers. Domain types never reference either Microsoft framework.
