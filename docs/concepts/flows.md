# Flows

A Flow defines how work is routed and processed. Editable drafts can be validated and published as immutable versions. A `FlowReference` selects either an exact version or the active version; Work Items store only that reference, never an embedded definition.

The implemented graph executor supports typed Input, Agent, Router, Condition, Transform, Output, and Failure steps. Published Workplace Entries always resolve to an exact executable Flow, including Agent selections normalized through system-managed Direct Agent Flows.

See [Flow definitions](../flow.md) and the [Flow execution architecture](../architecture/flow-execution.md).
