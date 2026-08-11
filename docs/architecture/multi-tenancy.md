# Multi-tenancy and isolation

Agentstration is not yet a production multi-tenant platform. A canonical Tenant model, authentication, roles, and permission enforcement are **not implemented yet**.

Workspace isolation is implemented and mandatory for Workspace-owned data: routes, services, repository queries, background processing, and artifacts carry Workspace identity. Cross-workspace reads and mutations return not found.

Future Tenant support must add a broader isolation boundary without weakening existing Workspace scoping. See [Tenant](../concepts/tenants.md) and [Workspace](../concepts/workspaces.md).
