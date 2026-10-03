# Flows

A Flow defines how work is routed and processed. Editable drafts can be validated and published as immutable versions. A `FlowReference` selects either an exact version or the active version; Work Items store only that reference, never an embedded definition.

The five Flow kinds are Direct, Routing, Workflow, Orchestration, and Composite. They do not provide the same execution semantics or current implementation level. See [Flow modes](flow-modes.md) for the decision guide, exact behavior, orchestration strategies, limits, and current restrictions.

The typed graph supports Input, Agent, Flow, Repeat, governed Tool and ToolRoute, Router, Condition, Transform, Output, and Failure steps. A Flow call selects any accessible published Flow by active or exact version, maps its declared input schema, and executes it as a durable child FlowRun. Repeat composes that same child boundary with first/next input mappings, an explicit stop condition, and a finite iteration limit. Publication rejects missing versions, incompatible mappings, and direct or indirect Flow dependency cycles; graph transitions remain acyclic.

Published Workplace Entries always resolve to an exact executable Flow, including Agent selections normalized through system-managed Direct Agent Flows.

See [Flow definitions](../flow.md) and the [Flow execution architecture](../architecture/flow-execution.md).
