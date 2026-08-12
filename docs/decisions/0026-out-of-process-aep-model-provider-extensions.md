# ADR-0026: Out-of-process model-provider extensions through AEP

## Status

Accepted — 2026-08-11

## Context

The original Ollama integration implemented `IModelProvider` in the Agentstration process. Although Runtime and Microsoft Agent Framework (MAF) consumed only `IChatClient`, the Web and Work API composition roots still loaded OllamaSharp and provider-specific request behavior. This made a provider adapter a deployment-time DLL dependency and prevented language-neutral extensions.

## Decision

- Agentstration Extension Protocol (AEP) V1 is an HTTP/JSON protocol with explicit version `1.0`, discovery at `/.well-known/agentstration`, model-provider endpoints under `/aep/model-providers`, and SSE chat streaming.
- AEP owns independent messages, content, generation options, tools, tool calls/results, usage, finish reasons, descriptors, capabilities, and structured errors. It exposes neither Microsoft.Extensions.AI nor MAF types.
- `Agentstration.Aep.AspNetCore` provides reusable discovery, routing, registration, error, cancellation, health, and streaming behavior.
- `Agentstration.Aep.Client` performs discovery and rejects incompatible protocol versions before invocation. Transport and protocol failures use stable AEP error codes.
- `Agentstration.Aep.MicrosoftExtensionsAI` is the only AEP-to-`IChatClient` mapping boundary. MAF continues to consume `IChatClient` unchanged.
- Persisted `ModelProvider.providerType` identifies an AEP model-provider contribution such as `ollama`; its endpoint is the AEP extension base URL, not the native provider URL. `ModelProfile` retains its model and provider-keyed options.
- `Agentstration.Extensions.Ollama` is an autonomous ASP.NET Core host. It alone references OllamaSharp, translates AEP chat and streaming calls, reports capabilities/models, and forwards tool calls without executing Agentstration tools.
- Aspire provisions Ollama and the Ollama extension separately, then injects the extension endpoint into Agentstration through configuration and service discovery.
- The former in-process `Agentstration.ModelProviders.Ollama` project is removed. Dynamic DLL loading, `AssemblyLoadContext`, gRPC, package installation, and marketplace concerns are outside AEP V1.

## Consequences

Provider implementations can use any technology capable of serving AEP, and replacing Ollama no longer changes MAF or Runtime architecture. A network hop and protocol mapping are now part of every non-deterministic model request. V1 negotiates exact protocol compatibility and performs discovery before calls. The contracts reserve non-text content and tool exchange; the C# adapter implements text plus tool-call/tool-result mapping, while image/file transport and richer structured-output mapping remain later compatible additions.

This ADR supersedes ADR-0013 and ADR-0018 where they place OllamaSharp or an Ollama adapter inside the Agentstration process. Their persisted profile, dynamic discovery, and runtime-resolution decisions remain accepted.
