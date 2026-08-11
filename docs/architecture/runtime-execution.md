# Runtime execution

Runtime execution resolves an immutable Agent revision, its model and runtime profiles, and its tool set before creating an executable agent through the runtime adapter.

```mermaid
sequenceDiagram
    participant Caller
    participant Runtime as Runtime Run service
    participant Mgmt as Management resources
    participant MAF as Runtime.AgentFramework
    participant Model as Model provider
    Caller->>Runtime: Create Runtime Run
    Runtime->>Mgmt: Resolve agent revision and profiles
    Runtime->>MAF: Materialize reconstructible agent
    MAF->>Model: Invoke through IChatClient
    Model-->>MAF: Response/tool activity
    MAF-->>Runtime: Normalized events and result
    Runtime-->>Caller: Durable status/events
```

The Runtime owns technical execution, not the functional Work lifecycle. Model-provider resolution is behind application/runtime boundaries; Ollama is an optional adapter rather than a Runtime dependency.
