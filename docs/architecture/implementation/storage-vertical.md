# Storage vertical

FR #1045 adds a resource family independent of Data Sources, Knowledge, Connected
Services and provider transport. [The API reference](../../reference/storages.md)
defines routes and contracts; [ADR-0199](../../decisions/0199-storages-own-explicit-service-export-bindings.md)
records ownership and authority decisions without changing accepted ADRs.

| Project | Owns | Allowed boundaries |
|---|---|---|
| `Agentstration.Storage.Contracts` | StorageType/Storage resources, exact revision evidence, manual bindings, protected resolution and availability/usage ports | Resources, generic ResourceManagement, ConnectedServices.Contracts |
| `Agentstration.Storage` | Immutable versions, lifecycle, validation, use/access authorization, reconciliation, resolution | Contracts, Identity.Contracts, ResourceManagement.Contracts, bounded Schemas validation |
| `Agentstration.Storage.Api` | HTTP mapping, ETags, pagination, Problem Details | Storage services and family-neutral API contracts |
| `Agentstration.Infrastructure` | Composition, local metadata availability, builtin type provisioning, Bootstrap handlers | Existing inward-facing ports and document store |

There is no Storage-specific relational schema. ResourceManagement's existing
document stores preserve exact scope, UID, governance and optimistic concurrency.
The family never references EF, ASP, AEP, physical provider SDKs, Console, Data
Sources or Knowledge. Extension materialization uses `SynchronizeManagedAsync`
under exact trusted governance; #1040 owns the actual AEP mapping adapter.

The three authority layers remain separate:

1. Connected Service owns connection options, Parameter/Secret references and typed
   exports. Its metadata inspector validates exports without resolving Secret values.
2. Storage owns implementation selection, explicit requirement/export mappings,
   bounded configuration and a fixed envelope.
3. Data Source/Knowledge own logical dataset or output rules and exact selected
   Storage revisions. They receive no ability to replace connection or envelope.

Resolution repeats current authorization and contract checks. It verifies both
descendant UsePolicy and the resource access policy for each dependency. Protected
configuration does not serialize with the logical revision. Sharing physical
placement or an extension never grants shared logical authority.

Reconciliation observes drift without rebinding. Accepting a new complete
definition requires an explicit request and ETag; dependencies prevent destructive
rebinding or deactivation. Missing providers leave stored resources and evidence
unchanged. Read-time observations do not create writes or change ETags.

The Core `local` type supports management/resolution without a Connected Service.
It advertises no unimplemented content capabilities. #1040 delivers actual
ASP/AEP/Artifact adapters, #1046 the Console, and #1041/#1042 the dataset and
Snapshot consumers. Existing acquisitions and Artifact histories are untouched.

Fast tests exercise domain and authority invariants with deterministic ports.
Integration tests cover real HTTP/Problem Details, Bootstrap, Secret non-resolution,
SQLite restart and concurrency. Architecture tests enforce project direction and
keep provider/transport implementations outside the family.
