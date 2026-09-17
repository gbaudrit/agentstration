# ADR-0120: Foundry local Aspire discovery is explicitly opt-in

Status: Accepted — 2026-09-17

## Context

[ADR-0119](0119-foundry-model-deployments-use-an-isolated-aep-extension.md) isolates Foundry behind an autonomous AEP extension and reserves secure orchestrated preview for the later security and deployment gates. A local developer still needs to start that discovery-only extension alongside Agentstration in Aspire to exercise its enrollment and model catalog. Always starting it would break the offline default because Foundry requires explicit endpoints and authentication.

## Decision

- The AppHost adds `foundry-extension` only when `Foundry:Enabled=true`. Unset or false preserves the existing offline resource graph.
- The AppHost passes the configured project and inference endpoints and authentication mode to the child process. The extension remains responsible for validating the complete Foundry configuration and enforcing its outbound transport policy.
- In API-key mode the AppHost models `FOUNDRY_API_KEY` as an Aspire secret parameter, sourced from AppHost configuration and injected into the extension process environment. Local Visual Studio values belong in AppHost User Secrets, which are distinct from the standalone extension's User Secrets. Entra modes do not create this parameter.
- The conditional extension joins the existing development AEP enrollment, key provisioning and allowed-host composition. This is a local discovery test path, not a packaged deployment, chat capability or secure remote/orchestrated preview.

## Consequences

The first Foundry test can run in the Aspire dashboard without making Azure or a key mandatory for other developers. The later FR-449 and FR-451 gates still control secure preview and the complete deployment experience; FR-439/FR-452 will replace the temporary downstream key binding.
