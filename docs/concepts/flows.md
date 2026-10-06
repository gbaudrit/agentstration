# Flows

A Flow defines how work is routed and processed. Editable drafts can be validated and published as immutable versions. A `FlowReference` selects either an exact version or the active version; Work Items store only that reference, never an embedded definition.

The five Flow kinds are Direct, Routing, Workflow, Orchestration, and Composite. They do not provide the same execution semantics or current implementation level. See [Flow modes](flow-modes.md) for the decision guide, exact behavior, orchestration strategies, limits, and current restrictions.

The typed graph supports Input, Agent, Flow, Router, Condition, Transform, Output, and Failure steps. A generic Flow card selects any accessible published Flow by active or exact version, maps its declared input schema, and exposes its output schema to later steps. Publication rejects missing versions, incompatible mappings, and direct or indirect dependency cycles. Durable child-Flow execution is delivered separately; graphs containing a Flow step are authorable but are not executable until that runtime increment is present.

Published Workplace Entries always resolve to an exact executable Flow, including Agent selections normalized through system-managed Direct Agent Flows.

## Manual input forms

The Console renders manual execution input from the schema of the selected immutable published version. The generated editor supports object properties, required fields, nested objects, homogeneous arrays, string, integer, number, boolean and null values, scalar enumerations, defaults, examples, descriptions, URI format, regular-expression patterns, and common numeric, string and collection bounds. Schema defaults initialize only a new empty input; examples remain guidance. When raw JSON mode opens on an empty input, the editor creates a schema-shaped template for required properties and properties with defaults. Optional properties without defaults remain absent until the user enters a value.

The browser editor accepts schemas up to 64 KiB, values up to 64 KiB, eight levels of nesting, 128 generated fields, and 100 rendered collection items. A missing or free-form schema, a non-object root, or an unsupported semantic construct such as `$ref`, `oneOf`, `anyOf`, `allOf`, conditionals, schema-valued `additionalProperties`, tuple arrays, or an unknown format uses the raw JSON fallback for the complete value. The fallback still requires a bounded JSON object. Switching published versions preserves the current value, reports incompatibilities against the newly selected schema, and never resets input implicitly. Client-side feedback does not replace authoritative validation by the Flow API.

See [Flow definitions](../flow.md) and the [Flow execution architecture](../architecture/flow-execution.md).
