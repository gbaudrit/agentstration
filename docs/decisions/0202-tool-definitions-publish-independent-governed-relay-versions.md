# ADR-0202: ToolDefinitions publish independent governed relay versions

## Status

Accepted — maintainer design decisions recorded in [#1044](https://github.com/gbaudrit/agentstration/issues/1044), 2026-11-07.

## Context

ADR-0106 introduced Flow-backed ToolDefinitions; acquisition replay added another execution path under ADR-0190 (Proposed). The model still required a Flow for every definition. ADR-0193 assigns generic fixed-parameter relays to the product, independently of ASP. A relay and its source must evolve independently without changing immutable consumers silently.

The integration baseline also supplies Connected Service configuration, types, typed literals and discovery (ADR-0195–0198), explicit Storage export bindings (ADR-0199), and protected ASP binding exchange (ADR-0200, Proposed). Their connection configuration, authorization and protected execution envelopes must remain authoritative. Parallel #1041 owns acquisition/Data Source composition; relay publication must not duplicate it.

## Decision

- Keep `kind: ToolDefinition`; discriminate the definition with `toolKind: flow`, `relay` or `acquisition`. Reuse the typed System.Text.Json polymorphism used by Flow definitions/steps. Share presentation, enablement, timeout and publication properties; keep targets on their typed variants. Acquisition has no synthetic executable Flow reference.
- Saving edits a draft. Explicit publication through the versions endpoint or `publish: true` creates an immutable `ToolDefinitionVersion` document through the existing immutable store boundary. Each publication has an independent `major.minor.patch` version and materializes an ordinary Tool named `agentstration.<definition>.v<version>` in its exact namespace/scope. Its internal MCP identity is `<definition>.v<version>`. New publications never rewrite earlier Tools or consumers. Owning-definition enablement remains a live admission check.
- Pin the exact source Tool UID, namespace, owner scope, generation, provider UID/generation, mapping and contracts. Product sources expose publication versions; external sources without historical invocation support expose their current catalog revision. Changed or unavailable pins fail explicitly; cached schemas never impersonate old implementations.
- Direct Agent adoption requires a new Agent revision; ToolSet adoption requires a new ToolSet version and subsequent consumer adoption; direct Flow adoption requires a new Flow version. Republish only changed dependency chains. Fixed-value-only changes also require explicit adoption. Accepted executions retain recorded dependencies subject to live security checks.
- Fixed inputs use non-root JSON Pointer object-property paths and exactly one `literal`, `parameter` or `secret` binding per path. Reuse scoped Parameter/Secret resolvers, authorization, contextual creation and usage reporting. Publication pins reference identities; invocation resolves current values. Updating the same Parameter value or rotating the same Secret requires no republication. Changing reference, binding kind, literal, path or source requires a new relay version.
- Derive the caller schema, reject fixed-input overrides, reconstruct structurally, and validate the complete request against the source contract. Escape opaque names with `~0`/`~1`. Reject duplicate/overlapping paths, duplicate JSON keys, scalar/null replacement of fixed ancestors and unsupported schema constraints. Array-index and nullable-parent traversal are unsupported; whole arrays/objects may be fixed. See the bounded supported profile in the reference.
- Invoke the exact source through the existing governed pipeline with caller scope, correlation, hooks and cancellation. Effective relay approval is at least the source requirement. Console/MCP execution cannot bypass it. Secret values appear only in the execution request; suppress inner argument capture and redact sensitive lifecycle failures. Receipts identify relay/source versions without fabricating a FlowRun for a direct call.
- Bound composition to eight relay levels, 64 bindings, twelve object levels/1,024 characters per path, and 65,536 JSON characters for source schema, literal configuration and reconstructed input. Retain the configured invocation timeout.
- The Console provides Configuration, Versions and Usages, separate relay/source labels, typed value-source controls, preview and explicit publication review. New version retains the selected source. Update source prepares deliberate selection/reconciliation. Missing/incompatible bindings block publication; consumers remain unchanged.

## Consequences

This supersedes ADR-0106's single mutable executable projection and Flow-only definition shape, while preserving ordinary Tool governance. It clarifies the acquisition variant without changing acquisition replay admission or Artifact ownership. Accepted ADR texts remain unchanged.

The intermediate format was unreleased. Update clients/manifests/fixtures to one model, without compatibility aliases or dual readers. Deliberately update old development documents or reseed disposable development state; never silently delete operational history or rewrite execution evidence. This uses document persistence without a relational schema change, new process or mandatory external service.

Durable Flow inputs currently have no protected-value envelope. Reject resolved Secret-bearing Flow submissions before creating Work/FlowRun resources instead of persisting cleartext. This limitation propagates through relay chains and does not invent a new protected-input framework in #1044.

Generic Tools have no dependency on ASP/provider SDKs. The reusable Console scenario [#1050](https://github.com/gbaudrit/agentstration/issues/1050) remains gated on implemented-UX validation and merge. Implementation regressions and smoke tests remain required.

Relays target ordinary governed Tools, including existing acquisition Tools, and reuse the current acquisition replay service. They neither expose Connected Service operations automatically nor create a second acquisition engine. #1041 can expose a Tool through its own explicit composition path and reuse this relay contract. Protected Storage/Connected Service configuration never becomes a relay business-input binding. Flow-backed sources preserve their existing exact/active version strategy; exact references are required for reproducible Flow dependencies. Usages is bounded to published consumers in the definition's owner scope rather than claiming a global cross-scope inventory.
