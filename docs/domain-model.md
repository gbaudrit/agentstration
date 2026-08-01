# Domain model

Agentstration separates durable management resources from reconstructible runtime objects.

The management resource chain is:

```text
AgentTypeResource -> AgentResource -> AgentRevision -> AgentDeployment
```

- `AgentTypeDefinition` supplies a handler, base instructions, model profile, tool allowlist, behaviors, middleware, context providers, and override policy.
- `AgentDefinition` references exactly one type version and contains only permitted customizations.
- `AgentRevision` is an immutable resolved snapshot with a deterministic SHA-256 definition hash.
- `AgentDeployment` carries desired state, provisioning state, operational state, hosting mode, and observed revision separately.

Resources use stable identifiers such as `/resourceGroups/default/providers/Agentstration.Agents/agents/sql-expert`. The domain contains no EF Core, SQLite, ASP.NET Core, Foundry, Entra ID, or concrete `AIAgent` dependency.
