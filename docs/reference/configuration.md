# Configuration reference

The verified startup settings and local defaults are documented in [Getting started: configuration](../getting-started/configuration.md).

Configuration follows ASP.NET Core providers, so environment variables use `__` for nesting. Persisted Model Provider, Model Profile, and Runtime Profile resources are Management data rather than host configuration. Prefer those resources for governed execution settings.

Do not commit API keys. HTTP payload capture is disabled by default, and sensitive prompts, documents, credentials, and agent output must not be logged by default.

## Authentication

`Agentstration:Authentication:Mode` is `Local`, `Oidc`, `Hybrid`, `Development`, or `Disabled`. `Local` is the offline default. `Development` and `Disabled` are rejected outside Development and Testing.

Local mode uses ASP.NET Core Identity and `ConnectionStrings:Identity`, defaulting to `.agentstration/identity.db`. A fresh instance exposes `GET/POST /api/auth/bootstrap`; the POST accepts the first username, password, display name, and optional email. No default credential exists. Hybrid adds explicit OIDC login while retaining local accounts.

After bootstrap, Platform administrators manage local accounts through `/api/identity/accounts` or Console **Organization > Members**. Workspace memberships are exposed under `/api/identity/workspaces/{workspaceId}/memberships`. Built-in roles are `Owner`, `Admin`, `Member`, and `Viewer`; the last Owner cannot be removed or demoted.

OIDC mode requires `Authority`, `Audience`, and `ClientId`; `ClientSecret` is required for confidential clients. `RequireHttpsMetadata` defaults to `true`. OIDC uses authorization code with PKCE and APIs validate JWT bearer access tokens. Agentstration never uses email as a subject, never maps an IdP tenant to a Workspace automatically, and never issues OAuth access tokens. See [ADR-0039](../decisions/0039-authentication-and-authorization-boundaries.md).
