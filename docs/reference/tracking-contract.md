# Tracking contract over MCP

The standalone Agentstration Tracking Contract SDK implements
`agentstration.tracking.contracts.mcp.v1` (`ATC.mcp.v1`). It supplies neutral target/item
identities, capabilities, typed MCP client/server adapters, existing-AEP contribution
metadata integration and deterministic conformance fixtures. See the
[SDK README](https://github.com/gbaudrit/agentstration/blob/main/atc/README.md), [normative contract](https://github.com/gbaudrit/agentstration/blob/main/atc/docs/contract.md) and
[ADR-0280](../decisions/0280-atc-is-an-independent-tracking-contract-over-mcp.md).

A logical tracker target can represent a repository, project or team. Selecting it
never grants authority. The host rechecks current target/subject permissions for reads,
mutations and recovery. Clients preserve native status/transition and relationship
semantics and distinguish atomic revision enforcement from a preflight observation.

The SDK is callable by deterministic application services without a Run, agent or LLM.
GitHub/GitLab are optional adapters, and a tracker need not host code. Tracking, forge
and shared project YAML are separate responsibilities. ATC and AFC can be supplied by
one extension or different providers; neither defines the local Project/plan model.

This increment provides the executable standalone foundation. Product connection and
durable Work-operation adoption remains #951/#955, vendor adaptation #958/#959 and
issue-realization policy #1346. The first memory fixture has process-local receipts;
it is not a production tracker or proof of live vendor durability. See the SDK commands
for independent build, tests, packaging, schema export and the authenticated demo.
