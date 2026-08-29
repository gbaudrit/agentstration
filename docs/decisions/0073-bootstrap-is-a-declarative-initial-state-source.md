# ADR-0073: Bootstrap is a declarative initial-state source

Status: Accepted — 2026-08-28

## Context

A fresh local or containerized instance may need a known initial administrator before an operator can use the interactive bootstrap. Future deployments may need the same startup source for ordinary Agentstration resources or Pack installations. This must not introduce a second resource model, a reconciliation loop, or persisted bootstrap bookkeeping.

## Decision

An optional `Agentstration:Bootstrap:Path` setting extends the existing bootstrap configuration and points to a filesystem directory containing `.yaml` or `.yml` declarations. An absent setting, missing directory, or directory without matching files is a no-op. Files are read in ordinal lexical filename order, and YAML documents inside each file retain document order.

Every declaration uses the native `agentstration.io/v1` envelope with `apiVersion`, `kind`, `metadata`, and `definition`. Startup validates the envelope and dispatches it to one registered `IBootstrapResourceHandler` for the exact kind. Handlers own resource-specific identity, validation, existence checks, and creation through the normal application boundary. Existing resources are skipped and never reconciled.

The first handler accepts `PlatformAdministrator`. `metadata.name` is the local username; `definition` contains display attributes and a `passwordFrom.configuration` reference. The handler resolves that value through standard .NET configuration only when the account is absent and passes it to the existing ASP.NET Core Identity bootstrap coordinator. Identity validates and hashes the password, while the Management provisioner creates the Principal, Tenant, Workspace, Owner assignment, and Platform administrator grant. An existing account is a no-op only when its linked Principal already owns the Platform administrator grant; an inconsistent collision fails startup.

Bootstrap runs after persistence initialization and before the Web host starts serving. Invalid YAML, unsupported API versions, unknown kinds, invalid declarations, and missing referenced configuration fail startup with the source file and resource identity. Resolved secret values are neither persisted in the canonical declaration nor logged.

`PackInstallation` is deferred. The current Pack service cleanly installs validated ZIP archives, but it does not own the requested local-directory or registry source resolution contract. A later Pack-owned handler can implement that source boundary and call `PackManagementService` without changing the loader or dispatch model.

## Consequences

- Bootstrap represents initial state only; changes made after creation survive every restart.
- No `Enabled` flag, bootstrap table, applied marker, dependency engine, secret store, REST API, or GitOps reconciler is introduced.
- Relative paths resolve against the host content root, while absolute paths support read-only container mounts such as `/app/bootstrap`.
- Adding a resource kind requires one handler; it does not require modifying the loader.
- The first declarative Platform administrator remains subject to the existing password policy and one-time local instance initialization rules.
- The opt-in `BootstrapDevelopment` launch profile supplies a versioned local bundle and the public `admin / admin` fixture; only Development relaxes the Identity password policy, while the normal profile and every other environment retain secure defaults.
