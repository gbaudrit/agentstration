# Flow execution

A submitted Entry is resolved to an exact immutable Flow version. Work creates functional state; the Flow engine creates a durable FlowRun; Agent steps invoke Runtime; results and events are projected back into Work and Workplace.

```mermaid
sequenceDiagram
    participant UI as Workplace
    participant Work as Work API / Work Plane
    participant Flow as Flow engine
    participant Runtime
    UI->>Work: Submit Entry or follow-up
    Work->>Flow: Execute pinned FlowReference
    Flow->>Flow: Create durable FlowRun
    Flow->>Runtime: Execute Agent step
    Runtime-->>Flow: Result and technical events
    Flow-->>Work: Functional progress/result
    Work-->>UI: REST projection + SignalR invalidation
```

Draft Runs retain the exact draft revision, definition hash, and immutable snapshot. Published Runs resolve an immutable semantic Flow version. Continuations create a new FlowRun and link it to the terminal predecessor.
