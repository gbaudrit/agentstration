# ADR-0280: ATC is an independent tracking contract over MCP

## Status

Proposed — implementation for [#1378](https://github.com/gbaudrit/agentstration/issues/1378).

## Context

External tracking operations need to be callable by deterministic application services
and authorized agents without importing GitHub/GitLab or a mandatory repository into
Project/plan semantics. The existing Work task-provider boundary and provider receipts
show the required authorization, operation-identity and uncertain-effect concerns.
MCP supplies invocation, not common domain semantics or native recovery guarantees.

The maintainer selected separate Storage, Tracking and Forge contracts, with names
`agentstration.<domain>.contracts.<exposure>.v<major>`. Storage renaming #1377 is a
parallel independent change; forge #1379 is a separate foundation. The historical
host/provider implementation under #955/#958 remains consumer-adoption work, not a
dependency that forces these SDKs to reference product aggregates.

## Decision

- Stage an autonomous `atc/` SDK with neutral abstractions, typed MCP client/server
  adapters and an optional AEP contribution adapter. Its current exposure is
  `agentstration.tracking.contracts.mcp.v1`, revision `1.0`. No product, provider SDK,
  Runtime, EF, LLM, forge or storage contract is required by the neutral packages.
- Every call selects an exact provider/target/binding/revision. The authenticated host
  reauthorizes the subject, allowed operations and related targets on every call,
  including description and cached receipt recovery. Configuration remains host-owned;
  target IDs and caller-supplied business arguments grant no authority.
- Keep bounded describe/list/read/mutate schemas. The mutation is discriminated into
  create, supported update fields, comment, relation and transition. Advertise only
  actual executable capabilities. Preserve native state/completion and distinguish
  hierarchy from dependency relations; do not infer local work acceptance.
- Separate observed revision from atomic revision enforcement. Preserve operation keys,
  exact intent digests, replay/conflict and explicit uncertain outcomes. Positive proof
  is required for Missing; cancellation or absence from a search is insufficient.
  Process versus durable recovery and receipt windows are explicit capabilities.
- Put declarations in existing AEP Tool contribution metadata using the contract
  identity key and exact MCP mappings/schema digests. Validate those declarations and
  actual discovered Tools; introduce no generic contract registry or enrollment flow.
- Assess OSLC CM before fixing vocabulary. Retain its useful distinctions around native
  state, actions and relationship meaning; the executable binding remains typed JSON
  over MCP and does not claim OSLC service conformance.
- Supply a real authenticated MCP demo and deterministic conformance/adversarial tests.
  The memory provider is bounded/process-local and fences restarts with new target
  generations. It cannot provide production durable receipt recovery.

## Consequences

ATC can be consumed without an agent or code forge. One extension can implement ATC
and AFC, while Projects select their connections independently. Git transport, shared
YAML, issue-to-plan policy and local task acceptance keep their existing owners.

#951/#955 own product connection/routing adoption; #958/#959 own vendor adaptation.
This foundation does not rewrite retained Work histories or migrate vendor operations.
Its normative semantics and limits are in the [ATC specification](https://github.com/gbaudrit/agentstration/blob/main/atc/docs/contract.md).
No REST adapter, new product process or Console/Workplace journey is introduced.
