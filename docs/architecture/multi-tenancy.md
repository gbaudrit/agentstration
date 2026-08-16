# Multi-tenancy and isolation

Agentstration is not yet a production multi-tenant platform. The Management Plane has a canonical Tenant and Workspace model plus provider-neutral Principals, local/external identity mappings, Workspace memberships, role assignments, Platform administrators, and the first enforced ASP.NET Core authorization policies. Local credentials are isolated in the ASP.NET Core Identity store; external authentication uses OIDC/OAuth 2.0. Agentstration does not issue OAuth tokens.

Workspace isolation is implemented and mandatory for Workspace-owned data: routes, services, repository queries, background processing, and artifacts carry Workspace identity. Cross-workspace reads and mutations return not found.

Management Agents and Identity Workspace operations are the first protected vertical. Flow, Runtime, Work, Workplace, MCP, SignalR, and the legacy content Workspace are not yet covered by the canonical authorization boundary because their Workspace identities remain independent. See [ADR-0039](../decisions/0039-authentication-and-authorization-boundaries.md), [Tenant](../concepts/tenants.md), and [Workspace](../concepts/workspaces.md).
