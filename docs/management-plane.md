# Management plane

The management plane is Agentstration's source of truth. It owns agent definitions, immutable revisions, deployments, ETags, and desired state. Agents directly declare their handler, instructions, model profile, tools, middleware, and behaviors; there is no intermediate AgentType resource.

Its module boundary is explicit:

```text
Agentstration.Management.Abstractions
  canonical resource envelope, logical keys, structured references, storage ports, lifecycle events, runtime-facing resolved specs

Agentstration.Management.Core
  validation, idempotent resource use cases, generation tracking, revision compilation, deployment orchestration

Agentstration.Management.Contracts
  versioned HTTP request and response contracts

Agentstration.Management.Storage.Sqlite
  EF Core and SQLite implementation of the control-plane store
```

`Agentstration.Application` does not own Management code. Both central Management projects remain independent of Microsoft Agent Framework; concrete agent materialization stays in the Runtime plane.

The standalone host exposes these Minimal API routes:

```text
PUT    /api/agents/{name}
GET    /api/agents/{name}
DELETE /api/agents/{name}
POST   /api/agents/{name}/revisions
POST   /api/deployments/{name}
POST   /api/deployments/{name}/start
POST   /api/deployments/{name}/stop
POST   /api/deployments/{name}/reconcile
POST   /api/routing/invoke
POST   /api/packs/preview
POST   /api/packs
GET    /api/packs
GET    /api/packs/{publisher}/{name}
DELETE /api/packs/{publisher}/{name}
```

The resource document carries `apiVersion: agentstration.io/v1`; the HTTP query parameter is optional and, when supplied, must match it. Resource responses include ETags. Stale `If-Match` and conflicting `If-None-Match` requests return Problem Details with HTTP 412. Deployment mutations return HTTP 202.
