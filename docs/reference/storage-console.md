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

The scoped resource name is the canonical identity; no `StorageId` is introduced.
Detail links always preserve namespace and owner scope. `context` continues to
select the administrative context independently of the resource's `scopeRef`.
Creation uses that selected context. Resource write actions require write
permission in the owner context; inherited or managed resources retain a read-only
view. Every API request repeats authorization on the server. UsePolicy and resource
access policy remain authoritative in the API, including for visible ancestor
resources that cannot be used in the current context.

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

The existing `local` type advertises no content capabilities and needs no Connected
Service; its use from a descendant context still requires the type's explicit
UsePolicy. #1040 owns the actual ASP/AEP and Artifact adapters and default Artifact
Storage. This UI does not fabricate that default or advertise content operations
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

## Validation evidence — 2026-11-08

- `dotnet restore Agentstration.slnx`: passed.
- `dotnet build Agentstration.slnx --configuration Release --no-restore`: passed, 0 warnings and 0 errors.
- `dotnet test --solution Agentstration.Tests.Fast.slnx --configuration Release --no-build --minimum-expected-tests 777 --max-parallel-test-modules 4`: 998 passed, 0 failed/skipped.
- `dotnet test --solution Agentstration.Tests.Integration.slnx --configuration Release --no-build --minimum-expected-tests 954 --max-parallel-test-modules 2`: 1,057 passed, 12 optional skips, 0 failed (1,069 discovered). Skips: PostgreSQL (2), live Foundry (8), llama.cpp (1), LocalAI (1).
- TypeScript compilation and the three browser route-catalog checks: passed. Complete Docusaurus/ADR/LikeC4 build: passed.
- Dedicated real-executable Storage smoke: en-US/fr-FR at 1440×1000 and 390×844; Instance builtin local, Tenant local fixture and Workspace remote metadata fixture; explicit complete protected/standard mappings, shared Connected Service, exact namespace/type/service pins, reload, YAML, real ETag conflict, confirmed new service generation, observation/deactivation, Escape, focus restoration and mobile actions above navigation all passed. Extension-contributed type evidence is covered by deterministic component fixtures; no live ASP/Artifact adapter is claimed.
- General `npm run test:smoke` campaign: 102 passed and one existing organization-context read race failed. The helper now captures the validated scope in one read; all four organization locale/viewport cases passed on the focused rerun. System Chromium was used with video disabled; screenshots/traces were retained. Existing Data Source/acquisition/Artifact/Tool and navigation/localization cases passed.

These implementation checks do not constitute maintainer UX approval. The reusable
integrated scenario #1033 remains blocked as described above. The general smoke
campaign and the targeted rerun are reported separately rather than treating the
initial failed run as green.
