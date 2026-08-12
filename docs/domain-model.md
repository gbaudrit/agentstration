# Domain model

Agentstration separates durable management resources from reconstructible runtime objects.

The management resource chain is:

```text
AgentResource -> AgentRevision -> AgentDeployment
```

- `AgentResource.definition` directly supplies the handler, instructions, model profile, tools, behaviors, middleware, context providers, and settings.
- `AgentDefinition` references exactly one type version and contains only permitted customizations.
- `AgentRevision` is an immutable resolved snapshot with a deterministic SHA-256 definition hash.
- `AgentDeployment` carries desired state, provisioning state, operational state, hosting mode, and observed revision separately.

Management resources use server-generated immutable UIDs and logical `(Workspace, Kind, Name)` keys. The domain contains no EF Core, SQLite, ASP.NET Core, Foundry, Entra ID, or concrete `AIAgent` dependency.
