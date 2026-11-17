# ADR-0232: Tool relays can omit optional inputs

## Status

Accepted

## Context

FR #1179 exposes saved Data Source Queries as named relays. An unbound optional
acquisition object remained visible to the model with locally required `id` and
`uid` children. The reported agent consequently requested identifiers and
credentials instead of making the valid initial empty call. The maintainer also
requested a **Hide** choice in the existing relay binding editor.

## Decision

Extend ADR-0202's discriminated Tool bindings with `kind: omitted` and an exact
declared JSON Pointer. It removes an optional source property and its subtree
from the published caller schema. The caller cannot supply or override that
path. Reconstruction omits it entirely: it does not inject null, an empty
object, a fabricated identifier, or a resolved Parameter/Secret value.

Validate omission against the pinned source contract during draft validation,
publication and invocation. A locally required property cannot be omitted on
its own; an optional containing object can be omitted with all its children.
Omission creates no ancestor objects and preserves their existing requiredness.
The existing path, overlap, payload, authorization and source-revision guards
remain authoritative. Bindings and derived schemas remain immutable parts of
each published Tool version.

The shared value editor exposes **Hide** / **Masquer** only when the consuming
vertical explicitly enables `Omitted`. The Tool relay editor enables it for
optional declared inputs and hides their descendant controls. Existing secured
Secret-only requirements retain their restrictions. Other value-binding
consumers do not acquire omission behavior implicitly.

New dynamic-acquisition Query relays omit the technical `/acquisition` pin by
default. Their descriptions identify the saved Query and Data Source and state
that the first call accepts `{}`, selects the latest retained acquisition and
uses platform authorization. Optional explicit acquisition and pagination
arguments remain available unless the operator deliberately hides them too.

## Consequences

- Operators can narrow a relay without changing its source Tool or native schema.
- Existing native Tools, generic executors, source revision pins and published
  relay versions remain unchanged. No configuration migration rewrites history.
- Existing relays adopt omission through a new publication and explicit consumer
  adoption. Compatible Query-body publications retain ADR-0228's independent
  lifecycle and require no Tool or Flow republication.
- Regression coverage includes projection/reconstruction, required ancestors,
  overrides, manifest round trips, localized editing/reopening, actual Query
  execution with `{}` and the AWP model-facing optional contract.
