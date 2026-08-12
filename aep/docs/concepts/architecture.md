# Architecture

Dependency direction is one-way: consumers depend on AEP. AEP contracts, client, server SDK, validation, CLI, samples, and Inspector do not reference Agentstration Management, Runtime, Infrastructure, Console, Workplace, or provider-specific extensions.

The canonical client owns protocol requests and optional HTTP tracing. The Inspector composes that client and the validator; it does not have private server endpoints or extension-specific conditions. Its workbench navigation is generated from manifest capabilities. Model requests use the canonical specialized AEP client, while tool schemas and invocation use the official MCP client against mappings declared by AEP. The ASP.NET Core SDK provides `AddAep()` and `MapAep()` without embedding Inspector assets in extensions.

The standalone UI uses three zones: capability navigation, an interactive explorer, and a persistent redacted HTTP exchange viewer. The core Inspector project owns connection/session state and remains independent from Blazor, so CLI or alternative UIs can reuse the orchestration.
