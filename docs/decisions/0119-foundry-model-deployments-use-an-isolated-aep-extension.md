# ADR-0119: Foundry model deployments use an isolated AEP extension

Status: Accepted — 2026-09-17

## Context

FR-185 adds optional Microsoft Foundry model connectivity to Agentstration's governed Model Provider and Model Profile workflow. Foundry-hosted Agents are a separate hosting/lifecycle concern. The project deployment catalog and OpenAI-compatible inference surface are separate downstream calls, both carrying a Foundry credential. AEP enrollment and transport credentials authenticate Agentstration to an extension, not that extension to Foundry.

FR-439 will provide portable AEP Secret Requirements and consumer-level Secret Bindings. It is not yet available; blocking all Foundry implementation on it would delay the independent discovery and inference work.

## Decision

- Implement Foundry model access only in autonomous `Agentstration.Extensions.Foundry`, contributing `microsoft-foundry` over existing AEP. Neither Azure SDK types nor Foundry-specific resource shapes enter Management, Runtime abstractions, storage, Application, Flow, Work or Web.
- One configured extension instance represents one Foundry project and downstream identity boundary. It observes project `ModelDeployment` records through the Foundry project data-plane v1 list API. Their deployment names are AEP model IDs; discovered records are not desired-state resources.
- The first increment implements AEP discovery, health and model listing, but explicitly advertises no chat/streaming/tool capability until those calls exist. Only deployments with an affirmative chat-capability signal are listed. Missing or unknown capability metadata is not interpreted as support.
- The first increment uses the documented project REST API directly, rather than coupling discovery to a project SDK transport that could bypass the extension's outbound address policy. `Azure.Identity` is used for explicit managed, workload or Development credentials. The temporary API-key mode reads `FOUNDRY_API_KEY` from the extension process environment only. No credential is accepted in Model Profile options, resource manifests or URL fields.
- The AEP transport credential remains independent. When FR-439 is delivered, a consuming Model Provider binds a logical Foundry credential requirement to an Agentstration Secret, and the temporary environment-key path ceases to be the managed configuration. FR-433 is not a prerequisite for a same-tenant binding.
- Both configured downstream endpoints require HTTPS DNS URLs without user information, query or fragment. Discovery continuation is constrained to the exact configured project origin and deployments path. Automatic redirects are disabled; DNS results are checked at connection time and sockets connect only to allowed addresses. Private DNS destinations require an explicit configured-host allowance; link-local destinations remain denied.
- The extension is opt-in. The deterministic/SQLite startup and existing local AEP providers remain independent of Azure.

## Compatibility and staged delivery

The first increment uses `Azure.Identity` 1.21.0 on .NET 10 and the current local AEP contracts. The stable `Azure.AI.Projects` 2.0.1 and `OpenAI` 2.11.0 packages were reviewed as candidate SDKs, but are not referenced before an executable need: the project v1 REST contract is small, and inference mapping is the later FR-446 decision. The inference endpoint can be resource- or project-routed; FR-446 must verify the selected route, token audience and OpenAI package against representative deployments before fixing the wire mapping. No retired `Azure.AI.Inference` dependency is introduced.

FR-445 delivers bounded discovery and baseline egress checks. FR-446 adds non-streaming chat, FR-450 streaming/tools, FR-448 structured output/reasoning, FR-449 completes security/diagnostics, FR-451 adds opt-in orchestration/Console, and FR-452 adopts FR-439 Secret Binding. No orchestrated or remote preview is advertised before FR-449's release gate.

## Consequences

The temporary environment API key is a deployment-time secret and requires extension restart to rotate; it is not a substitute for Agentstration's Secret lifecycle. Operators must not place it in committed configuration or default profiles. Unsupported chat requests fail explicitly during the discovery-only increment. AEP can still inspect the extension and its bounded model catalog before inference exists.
