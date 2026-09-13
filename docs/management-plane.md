# Management plane

The management plane is Agentstration's source of truth. It owns agent definitions, immutable revisions, deployments, ETags, and desired state. Agents directly declare their handler, instructions, model profile, tools, middleware, and behaviors; there is no intermediate AgentType resource.

Its module boundary is explicit:

```text
Agentstration.Management.Abstractions
  temporary compatibility resources and ports awaiting explicit family extraction

Plural resource-family modules
  Agents, Extensions, Identity, Models, Packs, Sources, Runtime, Tools and Triggers own their validation and lifecycle use cases

Agentstration.*.Contracts
  versioned HTTP request and response contracts owned by Agents, Extensions, Identity, Models, Runtime, Secrets, Sources, Tools and Triggers

Agentstration.Identity.Contracts and Agentstration.Security.Contracts
  identity, authorization, personal-access-token and provider-neutral security-audit contracts

Agentstration.ResourceManagement.Contracts and Agentstration.Api.Contracts
  generic resource/bootstrap contracts and family-neutral HTTP collection envelopes

Agentstration.ResourceManagement.Storage.Sqlite
  EF Core and SQLite implementation of the control-plane store
```

`Agentstration.Application` does not own resource-family code. Family-owned contracts and resource-family modules remain independent of Microsoft Agent Framework; concrete agent materialization stays in the Runtime plane.

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
