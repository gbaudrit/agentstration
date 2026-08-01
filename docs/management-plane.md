# Management plane

The management plane is Agentstration's source of truth. It owns agent types, agent definitions, immutable revisions, deployments, ETags, and desired state.

Its module boundary is explicit:

```text
Agentstration.Management.Abstractions
  canonical resource contracts, identifiers, storage ports, lifecycle events, runtime-facing resolved specs

Agentstration.Management.Core
  validation, idempotent resource use cases, generation tracking, revision compilation, deployment orchestration

Agentstration.Management.Contracts
  versioned HTTP request and response contracts

Agentstration.Management.Storage.Sqlite
  EF Core and SQLite implementation of the control-plane store
```

Neither the general `Agentstration.Domain` nor `Agentstration.Application` project owns Management code. Both central Management projects remain independent of Microsoft Agent Framework; concrete agent materialization stays in the Runtime plane.

The standalone host exposes Microsoft-like Minimal API routes below:

```text
PUT    /resourceGroups/{group}/providers/Agentstration.Agents/agentTypes/{name}
PUT    /resourceGroups/{group}/providers/Agentstration.Agents/agents/{name}
GET    /resourceGroups/{group}/providers/Agentstration.Agents/agents/{name}
DELETE /resourceGroups/{group}/providers/Agentstration.Agents/agents/{name}
POST   /resourceGroups/{group}/providers/Agentstration.Agents/agents/{name}/revisions
POST   /resourceGroups/{group}/providers/Agentstration.Agents/deployments/{name}
POST   /resourceGroups/{group}/providers/Agentstration.Agents/deployments/{name}/start
POST   /resourceGroups/{group}/providers/Agentstration.Agents/deployments/{name}/stop
POST   /resourceGroups/{group}/providers/Agentstration.Agents/deployments/{name}/reconcile
POST   /resourceGroups/{group}/providers/Agentstration.Agents/routing/invoke
```

Every request requires `api-version=2026-08-01`. Resource responses include ETags. Stale `If-Match` and conflicting `If-None-Match` requests return Problem Details with HTTP 412. Deployment mutations return HTTP 202 and a tracking location for the affected resource; durable operation resources are the next increment.
