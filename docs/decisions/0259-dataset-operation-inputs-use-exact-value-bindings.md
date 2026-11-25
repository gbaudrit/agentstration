# ADR-0259: Dataset operation inputs use exact value bindings

## Status

Proposed — maintainer-requested Data Source creation corrections, 2026-11-22.

## Context

Dataset acquisition previously stored only literal operation parameters. The Console
must reuse `ValueBindingRequirementEditor` for literal, Parameter and Secret inputs.
Resolving a Secret into the source definition or an accepted Flow input would expose
it in manifests, histories and Worker execution material, contrary to ADR-0203.

## Decision

- Keep literal JSON in `dataset.acquisition.parameters`. Add `parameterBindings` for
  exact references on declared top-level operation inputs. Each binding names one
  input and exactly one Parameter or Secret, including its namespace, scope and the
  resource UID pinned by the server. A field cannot have both a literal and a binding.
  Object/array inputs retain the existing literal schema editor; Secret inputs must
  be strings. Unknown, duplicate, unavailable and incompatible bindings fail closed.
- Resolve Parameters through `IParameterResolver` at admission and retain that
  accepted value for the acquisition. Later acquisitions resolve the current value.
  Recheck the original reference identity and use authorization before execution.
- Authorize Secrets through `ISecretAccessAuthorizer` without reading their values
  when validating definitions. Admission creates trusted `SecretInputReference`
  records and symbolic markers using the ADR-0203 mechanism. Source definitions and
  accepted acquisition/Flow inputs never contain resolved Secret values.
- Propagate those trusted references through the existing Flow gateway. Materialize
  the current Secret only in the server-owned Connected Service acquisition executor
  using `ISecretInputResolver`, after rechecking the accepted acquisition and exact
  dependency pins. Validate the complete live operation input immediately before the
  HTTP or AEP request. Rotation affects a later attempt; replacement or revoked access
  is refused without fallback. Caller input cannot override saved acquisition values.
- Redact exact or embedded Secret echoes before returning operation output to the
  Flow, and sanitize protected failures. Mark protected HTTP/AEP requests so HTTP
  diagnostics retain safe metadata without capturing either body. Final output
  validation remains authoritative, including after redaction.
- The creation screen composes the existing shared value editor and contextual
  creators. The Source/Knowledge shared composition editors continue to own the
  transformation presentation. No new picker, runtime, store or transport is added.

## Consequences

Existing literal-only definitions remain valid. The additional reference collections
default to empty; the existing JSON document stores require no relational migration.
The public Data Source contracts and Console review/export show references, never
Secret contents. Bindings are bounded to 64 declared inputs; exact inventory scans,
schemas and payloads retain bounded validation.

Tests must cover literal/reference conflicts, current Parameter values, pinned UID
replacement, Secret rotation and absence from durable JSON, authorization failure,
protected diagnostics, shared editor reuse, selection reset and retained wizard state.
The change is an executable local/SQLite vertical and requires no external provider.
