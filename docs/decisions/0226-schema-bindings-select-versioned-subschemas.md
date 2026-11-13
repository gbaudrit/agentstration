# ADR-0226: Schema bindings select sub-schemas of exact published versions

## Status

Accepted

## Context

FR #1172 connects OpenAPI discovery to the existing Schema library, acquisition Artifacts and logical document collections. An API envelope such as `total_count/results` has a different contract from a projected result document. Creating a separate Schema resource for every extracted node would introduce an unnecessary publication and adoption lifecycle.

## Decision

Extend `SchemaVersionReference` with an optional `schemaPath`, an RFC 6901 JSON Pointer into that exact published version. Empty means the whole parent. Selection does not create another resource or follow an active version. Cross-scope Use checks apply to the parent. Missing paths, replaced parent identities, mismatched version hashes, remote references, cycles, scoped reference constructs and unsupported schema constructs fail explicitly.

Resolution copies the selected effective definition and records the parent's identity, UID, generation, exact version and definition hash alongside the selected definition hash and path. Local references resolve against the original parent before materialization; no network access or payload inference is performed. Metadata-only saves retain the server's existing snapshot when the binding is unchanged; a new or changed binding requires resolution. Bounded local references are supported by the built-in JSON Schema handler. A selected schema must also satisfy the consumer's actual validation/query profile. The ASP provider receives effective native JSON Schema, never a product registry reference.

OpenAPI Connected Service discovery exposes separate operation choices for successful status/media combinations and retains the exact native response schema, source location, OpenAPI version and contract digest. These fit the existing extensible `responseShape` metadata; the published AEP definition schema and wire protocol remain unchanged. For a single response, the existing external operation identity remains stable. Adding multiple response choices requires explicit operation reconciliation. Invocation refetches the pinned contract and verifies the real status/media against the selected choice, including default-response precedence.

The Console can explicitly copy a candidate's executable standalone definition into a Schema draft while retaining its original native definition and source evidence. Creation neither publishes nor adopts it, changes access policy, nor executes a business operation. Unsupported OpenAPI 3.0 constructs such as `nullable` are diagnosed; they are not silently translated into a weaker schema. OpenAPI 3.1 schemas use only the supported consumer subset. An absent or empty schema retains a generic JSON contract with an incompleteness diagnostic. Generic JSON acceptance is presented separately from structural validation.

Data Source acquisition bindings and final dataset-output bindings are separate. Save/adoption resolves both; accepted acquisitions retain those snapshots. Raw results are checked against the operation contract and any adopted raw-response contract. Final results are checked against the final output contract. An untransformed acquisition may retain its raw Schema evidence; a shape-changing composition does not inherit it implicitly. An operator can explicitly bind a projection output to `/properties/results` or `/properties/results/items` as appropriate. Splitting arrays or inferring a transformation's contract is outside this change.

Collections adopt bindings during create or explicit reconciliation. Publishing Schema B never changes a collection bound to A. A binding change is a new explicit Storage revision, subject to existing usage/reconciliation guards; it does not migrate existing documents or rewrite an immutable publication. Runtime uses the stored resolved contract, while current consumer/provider authorization and exact Storage checks remain in force. No runtime lookup of the parent Schema is required.

Artifacts, content-backed Knowledge outputs and structured publication references retain schema evidence. Their content digest, data-contract digest, parent Schema hash, selected-definition hash and ASP model digest have distinct meanings and are not required to be equal. Historical Artifacts/Snapshots are not rewritten.

## Consequences

- Sub-schemas share the parent's lifecycle and access policy. There is no derived-Schema resource, implicit publication, adoption or grant.
- Existing inline JSON remains supported. New persisted fields are additive and nullable; no relational migration or historical backfill is needed. Legacy collection binding digests omit absent new fields.
- Schema versions stay immutable. Retained references/snapshots provide usage evidence for #769; that issue continues to own the generic usage/extraction Console journey. There is no new Schema deletion API.
- The required date-time assertion reuses JsonSchema.Net across the built-in Schema and ASP document validators. Broader format alignment remains owned by #867. This change deliberately rejects incompatible document/query constructs instead of dropping constraints.
- Deferred reusable browser journey #1173 remains gated on maintainer UX validation and merge; implementation regression tests and browser smokes are required independently.
