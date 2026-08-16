# Principals and authentication identities

Agentstration represents a stable caller as a provider-neutral `Principal`. A Principal may be human or a workload and can be linked to a local ASP.NET Core Identity account and multiple external identities. `LocalIdentity` links the Identity account identifier without exposing credentials. Each external identity is keyed by the exact `(Issuer, Subject)` pair from a validated OIDC/OAuth identity. Email and display name are attributes and never identity keys.

Workspace membership and role assignments are Agentstration data. An identity-provider tenant, group, or role does not automatically create a Workspace or grant access. The first enforced vertical protects Management Agents and Identity Workspace operations; other planes remain planned.

Local-account administration is restricted to Platform administrators. Workspace-role administration is contextual and uses the built-in `Owner`, `Admin`, `Member`, and `Viewer` roles. `PlatformAdmin` remains an instance grant rather than a Workspace role.

Platform administration is transferable between active Principals, regardless of whether they authenticate locally or externally. The safe handover grants the successor first, then the authenticated successor disables or revokes the predecessor. Agentstration rejects self-revocation, self-disable, assignment to a disabled Principal, and removal of the last active administrator. Disabling an administrator preserves its grant until a separate revoke. See [ADR-0043](../decisions/0043-platform-administration-is-explicitly-transferable.md).

An authenticated local user can change their own password and revoke their other cookie sessions from `/account/security`. These operations remain credential concerns handled by ASP.NET Core Identity; they do not alter the Agentstration Principal or Workspace memberships.

Bootstrap, local authentication, account lifecycle, password/session changes, and Workspace membership mutations produce structured append-only security events in the Management Control Plane. Only Platform administrators can read this history. Events correlate stable IDs and scopes without copying credentials or mutable personal attributes.

The conversation labels `User`, `Agentstration`, and `System` remain functional message roles, not Principal records. See [ADR-0039](../decisions/0039-authentication-and-authorization-boundaries.md).
