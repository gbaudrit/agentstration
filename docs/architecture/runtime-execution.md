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
    Runtime->>Runtime: Persist Tenant, Workspace, Principal scope
    Runtime->>Mgmt: Revalidate Principal and runs/execute
    Runtime->>Mgmt: Resolve agent revision and profiles
    Runtime->>MAF: Materialize reconstructible agent
    MAF->>Model: Invoke through IChatClient
    Model-->>MAF: Response/tool activity
    MAF-->>Runtime: Normalized events and result
    Runtime-->>Caller: Durable status/events
```

The Runtime owns technical execution, not the functional Work lifecycle. Model-provider resolution is behind application/runtime boundaries; Ollama is an optional adapter rather than a Runtime dependency.

The Runtime Run scope comes from the authenticated server context and is persisted with the Run before it is queued. It is immutable and is restored only after authorization has been revalidated. Instance-wide deployment reconciliation is different: its hosted worker opens an explicit, short-lived system scope for each iteration and never presents that authority as a user Workspace context.
