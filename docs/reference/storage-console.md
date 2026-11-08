# Storage administration in the Console

FR #1046 consumes the [Storage API](storages.md) delivered by #1045. Both the
standalone Web host and independent Console host register `IStoragesClient`.
Console assemblies reference `Storage.Contracts`, never Storage services,
persistence implementations or ASP/AEP adapters.

## Routes and context

| Route | Behavior |
|---|---|
| `/storages` | Visible scoped resources, search/status filters and versioned type catalogue |
| `/storages/new` | Create in the selected Instance, Tenant or Workspace administrative context |
| `/storages/{name}?scopeRef=...&namespace=...` | Exact resource: Overview, Definition, Access and YAML |
| `/storages/{name}/edit?scopeRef=...&namespace=...` | Dedicated entry to the same Definition editor |
| `/storage-types` | Scoped StorageType catalogue and links to exact definitions |
| `/storage-types/new` | Create a user-owned type and its initial immutable version |
| `/storage-types/{name}?scopeRef=...&namespace=...` | Exact type: Overview, Definition, Access and YAML |
| `/storage-types/{name}/edit?scopeRef=...&namespace=...` | Edit display metadata without rewriting published versions |
| `/storage-types/{name}/versions/new?scopeRef=...&namespace=...` | Explicitly publish an additional immutable version |

### StorageType interactions (#1064)

The type catalogue is independently accessible from Storage administration. Exact
links preserve the owner scope and namespace. Overview exposes governance,
availability, version contracts and discoverable Storage references. Definition
separates mutable display metadata/enablement/preferred version from publication
of an additional version. Published schemas, requirements, capabilities, digests
and contribution evidence are never edited in place. Disabling an in-use type is
rejected; deletion requires confirmation and an unused user-owned resource.
Core/extension definitions remain read-only while permitted Access administration
remains available. No permission to edit access grants permits provider forgery.

Access reuses the shared grant editor and evaluates saved generic Discover/Use
decisions for one exact scope. Draft grants do not change the saved decision until
saved. Ordinary Storage grants bind to resource UID, owner, namespace and kind,
without a generation pin so metadata updates or new versions preserve sharing.
Explicit revision-pinned policies still fail closed when stale. Policy ETags are
independent from resource ETags; conflicts retain the draft for review. Explicit
review shows the currently saved grants alongside the retained draft before a
replacement can be retried. Type metadata conflicts similarly show the saved
display name, generation, description, enablement and preferred version.

New user-permission work is deferred to [#1077](https://github.com/gbaudrit/agentstration/issues/1077)
and UsePolicy administration to [#1078](https://github.com/gbaudrit/agentstration/issues/1078).
For Storage and StorageType, ResourceAccessPolicy.Use is the current usage
authority, as recorded in [ADR-0200](../decisions/0200-storage-execution-pins-protected-bindings-and-local-workspace-defaults.md).
A Tenant grant with IncludeDescendants includes its Workspaces. Discover alone,
revocation and foreign Tenant targets do not authorize usage. Embedded UsePolicy
adds no second access gate during the deferral. Existing user permissions, scopes,
UIDs/generations, versions/digests, enablement, availability, configuration and
mappings remain checked by the backend. Connected Services retain their own checks.
The Instance-owned `local` type uses functional `1.0.0`; it is neither copied nor
automatically shared with Workspaces.

The native focused scenario [#1065](https://github.com/gbaudrit/agentstration/issues/1065)
remains blocked until explicit implemented-UX approval and implementation merge.
Implementation behavior tests and real desktop/mobile en-US/fr-FR smoke checks
remain required. The integrated #1033 journey and #1040 adapters/provisioning are
separate deliveries.

The scoped resource name is the canonical identity; no `StorageId` is introduced.
Detail links always preserve namespace and owner scope. `context` continues to
select the administrative context independently of the resource's `scopeRef`.
Creation uses that selected context. Resource write actions require write
permission in the owner context; inherited or managed resources retain a read-only
view. Every API request repeats authorization on the server. The selector uses
the request-scoped generic Use observation together with activation and version
availability, without overriding the API. Observations are never persisted.
Denial guidance is localized and bounded; it does not expose hidden resources,
Secrets or physical paths.

The navigation and command palette expose Storages according to resource
permissions. Management, scoped resource inventory, Connected Services, Data
Sources and Knowledge Sources remain distinct journeys.

## Create and update

1. Select an exact visible type version. The catalogue includes unavailable types
   with their evidence; only ready versions can be newly selected. It shows the
   builtin/extension origin, owner, UID, schema and contract digests, contribution
   evidence, supported capabilities and implementation limits.
2. When required, select an enabled, ready Connected Service. The UI reads its
   metadata and exact service type to offer compatible typed exports. It never
   reads a Secret value or copies connection options into the Storage.
3. Select **each** export manually. Type, protection and required format narrow
   the choices; matching names do not select anything. Choosing a different type
   clears connection and mappings; choosing a different service clears mappings.
4. Configure the opaque Storage configuration and fixed logical envelope using
   the existing bounded JSON Schema editor. The API validates the complete
   configuration, mappings, policies and exact revisions before persistence.
5. Save. Required mappings remain empty until explicitly selected. Local types
   that require no service expose no connection editor.

Metadata updates use PUT with the resource ETag. Changes to type, service,
mappings, configuration or envelope use an explicit confirmation and send the
complete replacement to `/reconcile`, also with the saved ETag. Disabling through
the editor is confirmed. The draft preserves saved exact references if a type,
export or service disappears or changes UID/generation; it never silently selects
a replacement by name. After concurrency failure the draft remains visible and
an actionable localized message instructs the administrator to refresh and review.

## Inspect and maintain

Overview shows the resource UID/generation, scoped name, exact selected type,
service UID/generation and type, recorded mappings, schema-based configuration and
envelope, capabilities, bounded observation diagnostics and API-reported usage.
Unavailable types leave saved identity evidence visible. Diagnostics present
invariant codes with localized guidance, without rendering backend exception text.
YAML is read-only and contains the resource definition and references, never
resolved connection secrets.

**Check drift** confirms an observation-only reconciliation. It sends no replacement
properties and performs no storage business operation. **Deactivate** confirms a
request to the safe lifecycle endpoint. Dependencies can reject deactivation or
rebinding; the resource and retained content/history remain. Native modal dialogs
contain keyboard focus, support Escape/cancellation and restore focus on close.

All feature strings have en-US/fr-FR catalogs. Existing schema, scope, use-grant,
access-policy and responsive resource components supply the shared conventions.

## Delivery boundaries and validation

#1045 owns the resource, validation, persistence, API and protected resolution ports.
#1046 owns this Console administration surface and its API client, component,
localization and host-composition coverage. It does not add another backend.

The builtin `local@1.0.0` provides bounded local content operations without a
Connected Service. Descendant use
requires an explicit generic Use grant; synchronization and restart preserve
grants and revocations. #1040 supplies ASP/AEP and Artifact adapters and guarded
default provisioning. This UI does not fabricate defaults or advertise operations
which the selected version does not support. Component fixtures cover an
extension-provided type and typed standard/protected exports independently of a
live provider.

Links into `/data-sources` and `/knowledge-sources` preserve their existing
administration journeys. Their actual Storage selectors, dataset rules and
independent Snapshot output behavior remain #1041/#1042, which own those contracts.
There is no inline connection form or consumer override of a Storage envelope.

The existing native scenario [#1033](https://github.com/gbaudrit/agentstration/issues/1033)
remains under #1032 and blocked on explicit maintainer UX approval and relevant
implementation merges. #1046 adds component/client tests and browser smoke
validation; it does not start or duplicate that reusable integrated journey.

## Validation evidence — combined #1064/#1040, 2026-11-08

- `dotnet restore Agentstration.slnx`: passed.
- `dotnet build Agentstration.slnx --configuration Release --no-restore`: passed, 0 warnings and 0 errors.
- Required Fast lane, minimum 777 and maximum 4 parallel modules: 1,025 passed,
  0 failed/skipped. Includes architecture and localization checks.
- Required Integration lane on the combined product tree in CI: 1,080 passed,
  0 failed and 12 optional integrations skipped (1,092 total), minimum 954 and
  maximum 2 parallel modules. Optional skips are PostgreSQL (2), live Foundry (8),
  llama.cpp (1) and LocalAI (1). The local attempt was interrupted for the later
  test-fixture rebuild; the completed result here is the CI result.
- Targeted checks: 38 Storage application tests, 15 Storage HTTP scenarios,
  32 Storage Console/localization cases and 3 persistence cases passed. These
  include Tenant propagation, Discover-only and revocation refusal, foreign
  Tenant isolation, managed synchronization and real SQLite restarts.
- The initial combined CI passed all 125 Management API cases but exceeded the
  unchanged 2,560 MiB diagnostic limit at 2,683.6 MiB. Five service-only Storage
  cases now use the repository's existing isolated ManagementServiceFixture
  instead of HTTP hosts, preserving their names and assertions. All five passed
  locally after the adaptation. Subsequent metrics are retained with the
  [candidate CI](https://github.com/gbaudrit/agentstration/actions/runs/42506327201).
  No shared mutable test fixture, budget increase or forced GC is introduced.
- TypeScript compilation, all three route-catalog checks, changed C# whitespace,
  ADR validation (198 records), LikeC4 and the complete Docusaurus build passed.
- Real executable smoke passed en-US/fr-FR at 1440×1000 and 390×844. It covered
  type creation, immutable publication, retained metadata/policy drafts and saved
  comparisons, dependency-safe lifecycle, builtin protection, keyboard/focus and
  no horizontal overflow. For the Instance builtin local@1.0.0, each case used the
  Access panel to grant Tenant Discover-only with propagation (visible but not
  selectable; API creation 403), add Use (Workspace Console creation succeeded),
  then revoke Use (selection disabled again and API creation 403). No Console
  authorization override was introduced.
- Combined product tree: `66c5fe5c9708876cbe476cff150fbc3a088f7226`, including
  merged #1080. The later fixture correction `e44ad295` changes only tests and
  documentation, preserving the product source. Initial [.NET CI](https://github.com/gbaudrit/agentstration/actions/runs/42504549843),
  [Browser UX](https://github.com/gbaudrit/agentstration/actions/runs/42504555815)
  and [Documentation](https://github.com/gbaudrit/agentstration/actions/runs/42504555813)
  record the initial remote outcomes, including the memory failure described
  above. Final outcomes and exact validated commits are recorded in
  [PR #1079](https://github.com/gbaudrit/agentstration/pull/1079); earlier evidence
  does not imply an overall CI pass for a later candidate.

These implementation checks do not constitute maintainer UX approval. The reusable
integrated scenario #1033 and focused #1065 remain blocked as described above.
No reusable journey is started, and no implementation merge is performed. Earlier
validation counts in #1045/#1046 records cover their historical implementation
trees and do not replace this combined-tree validation.
