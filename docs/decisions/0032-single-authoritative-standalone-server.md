# ADR-0032: Use one authoritative standalone server

Status: Accepted — 2026-08-12

## Context

ADR-0021 introduced a separate `Agentstration.Work.Api` host so Workplace could be deployed without the operations Console. The host compiled endpoint and worker source files owned by `Agentstration.Web`. In the Aspire profile, both hosts also composed Management, Runtime, Flow, and Work services with separate local stores. This created two control-plane authorities and made assembly-reference tests unable to detect the source-level coupling.

The standalone product is intentionally a modular monolith and its default profile must have one source of truth. Workplace still needs a separately deployable end-user UI and must remain isolated from server implementation assemblies.

## Decision

`Agentstration.Web` is the single authoritative standalone server. It hosts the Management, Runtime, Flow, Work, Workplace, content, MCP, worker, and SignalR surfaces over one set of stores. `Agentstration.Workplace.Web` remains a separate HTTP/SignalR client UI and Aspire points it to `Agentstration.Web`.

The `Agentstration.Work.Api` executable is removed. The Work API remains a public transport surface hosted by `Agentstration.Web`; it is not a separate process or persistence authority. The Workplace workspace-list endpoint uses `/api/workplace/workspaces` so it does not collide with the content API at `/api/workspaces`.

Projects must not compile source files located outside their own project directory. Shared behavior belongs in an explicitly referenced project. An architecture test enforces this rule by inspecting every project file.

## Consequences

The default direct and Aspire profiles have one server-side authority and one set of local databases. Console self-clients and Workplace call the same HTTP endpoint, while the UI continues to depend only on neutral client, contract, and component projects. Server availability affects both UIs, which is consistent with the standalone modular-monolith deployment choice.

Deploying a separately scalable Work server would require a future ADR, explicit service ownership, independent contracts rather than linked source, and a deliberate data-authority design.
