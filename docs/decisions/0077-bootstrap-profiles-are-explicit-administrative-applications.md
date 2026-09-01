# ADR-0077: Bootstrap profiles are explicit administrative applications

Status: Accepted — 2026-08-29

## Context

The bootstrap catalog introduced by ADR-0075 remains useful after first startup. An operator needs to inspect and apply a reusable profile without restarting the process, and must be able to choose the Tenant or Workspace receiving scoped resources. Packs are also natural bootstrap inputs, but their installed resources must retain Pack ownership and must not become ordinary bootstrap-managed documents.

Manual application must not become a reconciliation controller. It must expose conflicts before mutation, preserve local changes, record partial failures, and prevent a profile from silently changing between confirmation and execution.

## Decision

Every profile may contain a reserved `profile.yaml` descriptor using kind `BootstrapProfile`. Its `metadata.name` matches the immediate catalog directory and its definition declares a display name, description, and exactly one `targetScope`: `instance`, `tenant`, or `workspace`. Profiles composed in one application must share that scope. Tenant and Workspace applications require an explicit target; the current navigation context is never an implicit target.

A PlatformAdmin-only Console page and HTTP boundary list profiles, compose an ordered selection, choose the required target, and produce a state-aware preview. The preview identifies each resource as create, skip, conflict, or invalid and returns a digest covering ordered profiles, all profile files, scope, and target. Applying requires that digest. Applications are serialized in-process, re-previewed under the chosen scope, and rejected if the catalog or target changed.

Each manual attempt is persisted as a `BootstrapApplication` Management resource with actor, ordered profiles, target, digest, timestamps, per-resource outcomes, and succeeded, partially-applied, interrupted, or failed status. Cancellation finalizes the attempt immediately. A `Running` attempt left by a process termination is recovered as interrupted when history is next read, after serialization with any active application. The service also appends a structured security audit event. History contains identifiers and outcomes, never secret values or archive contents, and each entry has a stable read endpoint.

`PackInstallation` is a Workspace-scoped bootstrap resource. Its definition points to a local ZIP path relative to its profile and may provide normal Pack binding selections. Preview and installation delegate to `PackManagementService`. A missing pack is installed, the same installed version is skipped, and a different installed version is a conflict. Bootstrap never replaces or uninstalls a pack. Resources created from the archive remain Pack-managed and therefore keep the normal Pack immutability and update rules.

Workspace profiles may alternatively contain the canonical manifests for `ModelProvider`, `RuntimeProfile`, `ModelProfile`, `Agent`, `Flow`, and `Entry`. These handlers delegate validation and creation to the same module services used by HTTP and Console surfaces. They are create-only and idempotent: an existing address is skipped without mutation. Directly created resources receive no Pack provenance and remain ordinary editable Workspace resources. Manifest order is dependency order; a reference may resolve to existing state or to a compatible resource planned earlier in the composed profiles. Model Providers reference Extension Registrations already present in the target Workspace. A published Entry targeting a Flow requires an existing active version or a Flow planned earlier with publication and activation enabled.

A Workspace profile may declare typed logical bindings in its descriptor. Direct resource manifests reference one through an object containing only `{ binding: name }`; before planning and application, that object is replaced with the selected concrete `ResourceReference`. The Console and API collect profile-qualified selections, while `defaultTarget` supports deterministic non-interactive selection. Bindings may resolve to existing resources or compatible resources planned earlier in the composition. Their resolved references participate in the preview digest and application history. Cross-Workspace references and generic text templating are rejected, and Secret bindings never copy or retain secret values.

Catalog traversal, profile count, selected-profile count, file counts, manifest sizes, total profile size, archive size, and reparse points are bounded. Pack sources are local profile files only; remote URLs and paths outside the profile are rejected.

## Consequences

- Initial startup and later manual application share the same catalog, parsing, planning, and resource handlers.
- Profiles are reusable across Tenants and Workspaces without embedding installation-specific identifiers in YAML.
- Typed profile bindings make direct editable resources portable without giving them Pack ownership or introducing unbounded manifest templating.
- One application has one unambiguous target and scope; cross-scope workflows use separate applications.
- Ordinary resources created by bootstrap can later be edited through their normal surfaces. Pack contents remain immutable through those surfaces because their owner is the installed Pack.
- The same YAML resource shape can be chosen as an editable direct import or wrapped in a Pack when lifecycle ownership and immutability are desired.
- Application is create-only and best-effort sequential. There is no rollback across heterogeneous resources; a failure after earlier creates is recorded as partially applied and a retry remains safe through handler idempotency.
- This is an administrative import workflow, not continuous GitOps reconciliation, scheduling, upload, or remote catalog distribution.
