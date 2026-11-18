# Flows

A Flow defines how work is routed and processed. Editable drafts can be validated and published as immutable versions. A `FlowReference` selects either an exact version or the active version; Work Items store only that reference, never an embedded definition.

The five Flow kinds are Direct, Routing, Workflow, Orchestration, and Composite. They do not provide the same execution semantics or current implementation level. See [Flow modes](flow-modes.md) for the decision guide, exact behavior, orchestration strategies, limits, and current restrictions.

The typed graph supports Input, Agent, Flow, Repeat, governed Tool and ToolRoute, Router, Condition, Transform, and named Output steps. Each Output has a stable name, a `success` or `error` outcome, an optional schema, and an optional payload mapping. `completed` and `error` are convenient defaults rather than reserved names. A generic Flow card selects any accessible published Flow by active or exact version, maps its declared input schema, discovers all of its named outputs, and can route on the output name returned by the child run. Repeat composes that same child boundary with first/next input mappings, an explicit stop condition, and a finite iteration limit. Publication rejects missing versions, incompatible or ambiguous output schemas, incompatible mappings, and direct or indirect Flow dependency cycles; graph transitions remain acyclic.

Definitions published before named output outcomes remain executable: an Output without an outcome emits the historical successful `completed` event. Error termination uses an ordinary named Output with `outcome: error`; the unreleased `failure` step discriminator is not supported. See [ADR-0148](../decisions/0148-flow-termination-uses-named-outputs.md) and [ADR-0175](../decisions/0175-flow-error-termination-has-no-failure-step.md).

Published Workplace Entries always resolve to an exact executable Flow, including Agent selections normalized through system-managed Direct Agent Flows.

## Manual input forms

The Console renders manual execution input from the schema of the selected immutable published version. The generated editor supports object properties, required fields, nested objects, homogeneous arrays, string, integer, number, boolean and null values, scalar enumerations, defaults, examples, descriptions, URI format, regular-expression patterns, and common numeric, string and collection bounds. Schema defaults initialize only a new empty input; examples remain guidance. When raw JSON mode opens on an empty input, the editor creates a schema-shaped template for required properties and properties with defaults. Optional properties without defaults remain absent until the user enters a value.

The browser editor accepts schemas up to 64 KiB, values up to 64 KiB, eight levels of nesting, 128 generated fields, and 100 rendered collection items. A missing or free-form schema, a non-object root, or an unsupported semantic construct such as `$ref`, `oneOf`, `anyOf`, `allOf`, conditionals, schema-valued `additionalProperties`, tuple arrays, or an unknown format uses the raw JSON fallback for the complete value. The fallback still requires a bounded JSON object. Switching published versions preserves the current value, reports incompatibilities against the newly selected schema, and never resets input implicitly. Client-side feedback does not replace authoritative validation by the Flow API.

See [Flow definitions](../flow.md) and the [Flow execution architecture](../architecture/flow-execution.md).

## Processing regions and work units

A step can expose a private `process` region. Its executor chooses the work units;
each unit runs the complete connected chain as a child Flow Run of one immutable,
derived definition. Authors connect ordinary Agents, Tools and named Flows directly,
then return the result to the owning step. No extra Flow publication is needed.

Artifact transformation creates one unit per Artifact. An ordinary Transform can
optionally partition an array into batches or text into bounded fragments. The
Designer configuration selects that mode, unit size and maximum parallelism. A
Transform returns unit results in input order as an array; an Artifact transformation
returns its Artifact manifest after preparation and finalization.

Process runs appear under their owning step in Causality. Their source Flow/version
remains visible separately. History defaults to Flow invocations and historical runs;
its execution-kind filter also exposes process units or all executions. Each unit has
its own scoped run link, output inspection, status and original-failure link. Unknown
historical origins are not inferred from names or inherited Artifact access.
