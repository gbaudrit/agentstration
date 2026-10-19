# ADR-0134 — Foundry connections are provider-owned AEP values

Status: Accepted — 2026-09-19

## Context

The first Foundry vertical configured one project, inference endpoint and downstream identity for the whole extension process. That prevents one extension instance from serving independent Model Providers and makes endpoint or credential rotation require process reconfiguration. ADR-0122 now supplies contribution-scoped `ValueRequirements`, standard Parameter-or-Secret bindings, secured Secret-only bindings and one-use grant redemption for discovery and inference.

## Decision

- The `microsoft-foundry` contribution declares required standard string values `projectEndpoint` and `inferenceEndpoint` with URI format, plus required standard string `authenticationMode`.
- It declares optional secured string `credential`; `ApiKey` mode requires it. Optional standard identity values are declared for managed and workload identity and are rejected outside their applicable mode. Workload identity requires tenant ID, client ID and an absolute token-file path. There is no legacy `SecretRequirements`, process-configuration fallback or migration.
- Every discovery, capability check, non-streaming chat and streaming chat resolves one operation-scoped connection from the selected Model Provider's bound values. A secured credential is redeemed once through the existing AEP Secret callback and is retained only for that bounded operation.
- The extension process retains only technical limits and outbound trust policy. AppHost and Compose opt in to the extension without project endpoints, authentication mode or downstream credentials. The optional Bootstrap profile selects three Parameters and one Secret to create a ready Model Provider.
- HTTPS DNS URL, project/inference path, exact-origin credential forwarding, disabled redirect, DNS-at-connect, address filtering, cancellation and payload/stream bounds remain enforced. A bound endpoint never adds itself to `AllowedPrivateHosts`; private-network trust remains operator-owned configuration.
- Target-specific health is evaluated through bound discovery. The process-level AEP health endpoint reports only extension availability because it has no Model Provider identity or bound values.

## Consequences

One extension instance can safely serve concurrent Model Providers for different projects and credentials. Parameter updates, Secret rotation or revocation and binding removal affect the next operation. Existing installations must create the required values and bindings explicitly.

This decision supersedes ADR-0126 and ADR-0127 where they scope a Foundry project and downstream identity to one process; ADR-0132 where topology configuration supplies those values; and ADR-0133 where it describes the former Secret-only discovery contract and transitional environment modes. Their isolation, capability, streaming, Tool governance, egress, bounds and operator-workflow decisions remain accepted.
