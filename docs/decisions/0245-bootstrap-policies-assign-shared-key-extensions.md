# ADR-0245: Bootstrap policies assign SharedKeyFile extension installations

## Status

Accepted

Renumbered from ADR-0238 when synchronizing with merged PR #1238, which published
ADR-0238 for fragment navigation. The Bootstrap decision is unchanged.

## Context

ADR-0094 makes the running extension announce its own endpoint with a timestamped
proof. Its mandatory administrative assignment prevents unattended installation.
Bootstrap already creates explicit instance topology and can provide the missing
server-owned assignment intent without changing extension configuration.

## Decision

`Agentstration:Aep:Enrollment:AutoEnrollmentLevel` is an optional default.
An instance-owned `ExtensionEnrollmentPolicy` selects a public `extensionId` and
optionally a stable installation `instanceId`. An exact installation policy takes
precedence over an extension policy, which takes precedence over configuration.
A policy with no destination explicitly keeps its selector manual. Equal-specificity
duplicates fail. With no policy or default, assignment remains manual.

Destinations are `/instance`, `/tenants/<name-or-id>`, or
`/tenants/<name-or-id>/workspaces/<name-or-id>`. UUID-shaped segments select IDs;
other segments select exact names. Resolution happens after topology creation and
validates active state and Tenant/Workspace membership. Persisted ownership keeps
the existing canonical `/workspaces/<id>` reference.

Only proof-verified SharedKeyFile candidates are eligible. Effective enrollment
mode settings, orchestrator-owned key files, exact-scope Secret/registration
creation and authenticated contribution provisioning remain authoritative.
Neither an announcement nor a policy can bypass the configured trust boundary.

Automatic assignment claims the request using its ETag and an expiring lease.
Failures are durable and retryable only for the same automatically selected
target. Existing assignments and explicitly closed or disabled installations are
preserved. Policy creation is create-only; changing configuration is not a
registration migration or continuous resource reconciliation.

## Consequences

- Extensions retain one identity and one destination without Tenant configuration.
- New announcements can be assigned after startup using the same policy service.
- Process interruption can be recovered without issuing a new identity or key.
- This extends ADR-0094's assignment decision; signed announcements are unchanged.
- Extension package installation, process launch and supervision remain external.
