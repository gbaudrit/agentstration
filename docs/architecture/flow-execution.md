# Flow execution

A submitted Entry is resolved to an exact immutable Flow version. Work creates functional state; the Flow engine creates a durable FlowRun; Agent steps invoke Runtime; results and events are projected back into Work and Workplace. The canonical behavioral scenarios are [Submit Work](dynamic-views.md#submit-work), [Execute Flow](dynamic-views.md#execute-flow), and [Execute Tool](dynamic-views.md#execute-tool); the [interactive L3 view](interactive.mdx) owns the structural relationships.

Draft Runs retain the exact draft revision, definition hash, and immutable snapshot. Published Runs resolve an immutable semantic Flow version. Continuations create a new FlowRun and link it to the terminal predecessor.
