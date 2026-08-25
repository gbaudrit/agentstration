# Principals and authentication identities

Agentstration represents a stable caller as a provider-neutral `Principal`. A Principal may be human or a workload and can be linked to a local ASP.NET Core Identity account and multiple external identities. `LocalIdentity` links the Identity account identifier without exposing credentials. Each external identity is keyed by the exact `(Issuer, Subject)` pair from a validated OIDC/OAuth identity. Email and display name are attributes and never identity keys.

Platform administrators explicitly add or remove external links from a member's details. A pair cannot move silently between Principals, and the last authentication method cannot be removed. Unknown external logins are not provisioned or matched by email in this iteration. See [ADR-0047](../decisions/0047-external-identities-are-explicitly-linked-to-principals.md).

Workspace membership and role assignments are Agentstration data. An identity-provider tenant, group, or role does not automatically create a Workspace or grant access. The first enforced vertical protects Management Agents and Identity Workspace operations; other planes remain planned.

Local-account administration is restricted to Platform administrators. Workspace-role administration is contextual and uses the built-in `Owner`, `Admin`, `Member`, and `Viewer` roles. `PlatformAdmin` remains an instance grant rather than a Workspace role.

Platform administration is transferable between active Principals, regardless of whether they authenticate locally or externally. The safe handover grants the successor first, then the authenticated successor disables or revokes the predecessor. Agentstration rejects self-revocation, self-disable, assignment to a disabled Principal, and removal of the last active administrator. Disabling an administrator preserves its grant until a separate revoke. See [ADR-0046](../decisions/0046-platform-administration-is-explicitly-transferable.md).

An authenticated local user can change their own password and revoke their other cookie sessions from `/account/security`. These operations remain credential concerns handled by ASP.NET Core Identity; they do not alter the Agentstration Principal or Workspace memberships.

Bootstrap, local authentication, account lifecycle, password/session changes, and Workspace membership mutations produce structured append-only security events in the Management Control Plane. Only Platform administrators can read this history. Events correlate stable IDs and scopes without copying credentials or mutable personal attributes.

The conversation labels `User`, `Agentstration`, and `System` remain functional message roles, not Principal records. See [ADR-0042](../decisions/0042-authentication-and-authorization-boundaries.md).
