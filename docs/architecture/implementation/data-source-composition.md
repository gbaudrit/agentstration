# Data Source operation and Storage composition

This vertical implements [#1041](https://github.com/gbaudrit/agentstration/issues/1041)
on `integration/fr-1032`. See [ADR-0204](../../decisions/0204-data-sources-compose-operation-acquisitions-with-logical-datasets.md)
for ownership, the compatibility decision and routes.

## Configuration and execution

1. Configure a Connected Service and open its Discovery tab. Discover its typed
   Operations, follow the linked Flow Runs, refresh the service's history and
   review its operation list there. Discovery preserves the service UID and
   generation and never enables an operation automatically.
2. Explicitly enable a reviewed Operation revision. `PUT
   /api/connected-service-operations/{name}/enabled` requires its current
   `If-Match`. Activation performs no acquisition and publishes no Tool.
3. Create a Data Source selecting an exact existing Storage, an allowed relative
   prefix, a Connected Service and then one of that service's exact Operations
   with saved JSON parameters. Changing the service clears the operation and
   parameters; saved revisions are never replaced on refresh. Its type version
   must advertise a published read-only acquisition Flow. Unsupported adapters,
   actions, missing required values and unavailable dependencies are refused.
4. Start acquisition using the ordinary Data Source acquisition endpoint with
   an empty request parameter object. The service validates saved parameters,
   authorization and dependency pins, persists acceptance and submits a durable
   Flow. A nonempty caller parameter object is refused.
5. Read acquisition history. Successful operation output is schema-validated,
   bounded and written to the exact selected Storage under a dataset UID and
   Workspace partition. History contains the content key/version/digest and
   separate composition evidence, not resolved connection credentials.
6. Read bounded content through the acquisition content endpoint. It checks
   the dataset, Workspace and Storage again, renews the opaque reference and
   refuses a changed pinned version. If the write receipt has no provider
   version or the listing omits its checksum, the reader verifies the complete
   bounded object against the saved SHA-256 before returning the requested range.
   The Storage read limit still applies. Content is data, never executable markup.

The Console uses the shared schema editor, complete neutral/French resources,
explicit activation on the Connected Service, and revision selection on the
Data Source. Discovery is not offered from the Data Source. A changed reference is selected
explicitly; refresh does not rewrite saved pins. Existing profile resources,
discoveries, targets, acquisitions, Tools and Knowledge consumers are retained.

The integration baseline includes StorageType administration and scoped generic
grants from #1079, and protected OpenAPI discovery from #1059. Acquisition uses
the merged `local@1.0.0` contract and current Storage/StorageType Use decisions;
it creates no grants. OpenAPI now contributes the compatible operation acquisition
adapter, its Flow, and the protected AEP acquisition endpoint. Enrollment publishes
the bundle before resolving the type's exact Flow UID/version/hash. The extension
retains protocol-specific HTTP execution; Data Sources remain provider-neutral.
No invocation contract is inferred from a discovered HTTP method or read-only flag.

The dataset editor resolves the exact operation type UID/version/digest and uses
its named input schema and read-only flag. An absent adapter or schema is explained
before saving an enabled source. Server validation preserves the stable Data Source
error code; the Console localizes known failures and displays the code and correlation
reference without printing arbitrary server details or saved values.

This is unreleased development state: the maintainer requested reseeding rather
than new versions or a migration layer. Existing immutable records are not silently
modified. After reseeding, enroll OpenAPI, create the service and rediscover/enable
its operations before configuring datasets.

Protected discovery preserves the logical Instance, Tenant administration or
Workspace context through the AWP Tool call. An administrative context can carry
a valid operational Workspace while `ICurrentRequestContext.IsInitialized` is
false (that property describes Workspace selection only). The executor checks
the actual operational identity and accepted discovery's logical scope, and
authorizes the service in that scope. It never substitutes Workspace authority
for an administrative call. Fifteen real OpenAPI/Flow/Secret callback scenarios
cover success, disabled services and changed revisions in all three contexts;
forged identities/scopes are refused before the provider discovery request.

Discovery transports standard Literal bindings as typed inline values, resolves
Parameter bindings through their governed resolver, and issues protected Secret
capabilities only for Secret bindings. A literal URL does not require a Secret
reference or callback. Secured inputs still reject literals, and unsupported
binding kinds fail explicitly. The OpenAPI regression matrix includes literal
URLs with and without the optional authentication Secret in all three scopes.

## Dataset Console sections

Saved native datasets open on Overview, with their saved service → operation →
Data Source → Storage composition, readiness, latest acquisition and three recent
runs. Definition, Acquisitions, Content, Access and YAML separate the remaining
surfaces. The optional `view` query opens a named section directly; Access remains
limited to administrators who can manage the owner resource. Tabs support arrow,
Home and End keys. Opening history or acquired content focuses the selected tab
after rendering, keeping it visible in the mobile tab bar.

Definition groups identity, the saved acquisition call and Storage destination.
Switching tabs preserves draft fields and schema parameters. Overview and the
read-only YAML manifest always describe the persisted definition; Cancel reloads
that definition. Acquisition starts use persisted configuration and open history.
Content opens a bounded, escaped preview of an explicitly selected acquisition,
including when navigated from history. No polling, editing of acquired content,
new Storage configuration or Knowledge Snapshot contract is introduced.

Creation retains a single definition form until the resource is saved. Historical
profile-based Data Sources retain their existing journey. Discovery and operation
enablement stay on the Connected Service. This implements the maintainer-approved
tab mockup; it does not remove #1033's implemented-UX review and merge gates.

## Knowledge form submission

The Knowledge Source editor names its form and disables fields until the Blazor
connection is interactive, both on create and embedded edit. A failure to list
acquisition targets stays in the form as a localized error, preserves the entered
values, blocks save until an explicit retry succeeds, and does not terminate the
interactive circuit.

The acquisition-target collection belongs to legacy profiles. For an authorized
native dataset it returns an empty list, without resolving a nonexistent Profile.
Before this correction, selecting a native dataset returned HTTP 422 with a null
`reference` error; the uncaught selection callback could terminate interactivity,
after which a submit could fall back to an unnamed HTML POST.

Native dataset inputs are not yet supported by the existing Knowledge projection
pipeline, which still pins Profile/Artifact dependencies. This remains #1042.
An enabled Knowledge Source with such an input returns the explicit HTTP 422
`knowledge_projection_dataset_input_unsupported`, localized by the editor.
A disabled configuration can be created and edited as a non-executable draft.
All Data Source references still resolve and authorize use; all Flow references,
transformations and later bindings remain validated. Only the unsupported-native
input error is tolerated when disabled. The draft retains `Enabled=false`,
`Composition=null` and `Ready=false`, without partial or fabricated Profile pins.

Activation through either definition update or the enable endpoint revalidates
and resolves the complete graph. Unsupported native inputs remain refused without
mutating the saved draft, and direct projection requests on a disabled source
are refused before execution. Replacing the input with a supported composition
builds fresh exact dependency pins before enabling. The editor explains how to
save disabled and shows localized draft readiness. This does not rewrite a Data
Source or acquired content, or implement native Knowledge Snapshot composition.

## Lifecycle and limits

- Acceptance is idempotent per Workspace, dataset UID and caller request key.
  The same key with changed configuration fails. A dataset has one active
  acquisition in the standalone process; different datasets run independently.
- Restart resumes pending acceptance through the same deterministic Flow identity.
  Published output is not reacquired. A conditional write has a persisted
  validity window; recovery confirms its unique key and digest first.
- Cancellation follows the existing durable Flow lifecycle. A cancelled or
  failed Flow publishes no dataset content. Storage failures preserve the Flow
  and acceptance evidence and expose a bounded failure code.
- Parameters/results are bounded to 64 KiB by the initial schema profile and
  intersected Storage limits. The profile includes typed objects/arrays/scalars,
  required fields, enums and the existing validator's supported constraints;
  `$ref` and composition keywords are rejected, not silently ignored.
- Broader Storage privileges do not broaden the dataset gateway's UID prefix.
  No policy/credential/mapping editing is offered by the dataset form.
- The initial relative-prefix profile reserves the `datasets` path segment.
  Rejecting it prevents a consumer from nesting a new dataset inside another
  dataset's UID partition. A Storage envelope requiring that reserved segment
  is unavailable to this profile; it is never rewritten or relocated silently.
- Historical content remains readable only while its exact Storage revision and
  content version are available and current access allows it. This is not a
  stable Snapshot contract, which belongs to #1042.
- The integration base now provides independently versioned ToolDefinitions and
  relays through #1044 (ADR-0202/0203). This feature adds no automatic native
  dataset Tool exposure or replacement relay engine. Historical acquisition Tools
  retain their governed replay path.

## Disabled Knowledge configuration validation

Follow-up to the tabbed Console on 2026-11-09:

- Release solution build: zero warnings/errors. Fast lane: 1,048 passed, no
  failures or skips. All 22 Knowledge editor/detail cases pass, including
  localized rejection/retry as disabled and non-executable draft readiness.
- Two new Instance/Workspace API lifecycle variants pass. The red regression
  reproduced the disabled-save refusal before correction. Coverage verifies
  create/edit/reload, grant enforcement, later foreign/invalid references,
  both activation routes, unchanged generation after refusal, no projection
  record for a disabled source, and fresh exact pins after a supported input
  replaces the native dataset. The complete resource API module passes.
- Four temporary en-US/fr-FR browser variants pass at 1,440/390 px, including
  delayed Blazor startup, named-form routing, rejected enabled save, disabled
  save and embedded edit, reload and blocked projection. No document POST,
  JavaScript error or horizontal overflow is observed. All eight existing
  source/schema browser regressions also pass.
- The complete Integration invocation ran 1,134 tests: 1,121 passed, 12 existing
  optional skips and one failure in the existing MCP 100 ns timeout assertion.
  All 23 tests in its module pass on rerun. The initial invocation remains
  recorded as failed; no assertion, production MCP code or skip was changed.
- Changed C#/Razor formatting, catalog symmetry, `git diff --check`, ADR registry,
  LikeC4 and the complete Docusaurus build pass. #1033 retains its implemented-UX
  approval and merge gates; native Snapshot projection remains #1042.

## Tabbed dataset UI validation

Follow-up checks on 2026-11-09 against the same integration base:

- Release solution build: zero warnings and errors. Fast lane: 1,046 passed,
  zero failures or skips. Twelve dataset editor cases include three new localized
  draft/cancel, persisted-summary/YAML and history/content-navigation cases.
- The complete Integration lane ran 1,132 tests: 1,119 passed, 12 existing optional
  cases skipped, and one existing MCP timeout assertion failed. Its configured
  100 ns timeout returned `schema_format_provider_failed` instead of
  `schema_format_provider_timeout`. The isolated case passed, then all 23 tests
  in its `Agentstration.Api.Tests` module passed on rerun. No production MCP code
  or timeout assertion was changed. The initial full lane remains recorded as
  failed; the rerun does not erase that result.
- All eight existing source/schema browser regressions passed with system
  Chromium, including desktop, mobile and French variants. The first launcher
  attempt expected an unavailable downloaded browser; the rerun used the
  preinstalled Chromium executable.

- Four temporary native-dataset browser variants passed in en-US/fr-FR at
  1,440/390 px, covering creation, localized validation, draft preservation,
  cancellation, saved YAML, Access, keyboard navigation, direct section links,
  acquisition and bounded content. Selected-tab focus and horizontal visibility
  are asserted after content navigation. Light-theme checks also passed at both
  widths. The mobile content overflow found during the first iteration is fixed;
  final runs report no page errors or document overflow.
- Changed C#/Razor formatting, JavaScript syntax checking, TypeScript checking,
  localization catalog symmetry and `git diff --check` passed. Documentation
  validation includes the ADR registry, LikeC4 and the full Docusaurus build.

## Earlier composition validation evidence

Checks completed on 2026-11-09 against integration base `03ad8f0`:

- Release solution build: zero warnings and errors.
- Required Fast lane: 1,043 passed, zero failures or skips.
- Required Integration lane after the Knowledge form correction: 1,120 passed,
  zero failures, 12 existing optional integrations skipped (two PostgreSQL and
  ten live-model cases), 1,132 total.
- Five native dataset integration scenarios and fifteen protected real OpenAPI
  Flow/Secret callback scenarios passed in focused runs. Successful OpenAPI rows
  now save and acquire two datasets with different parameters into one local
  Storage, then read and compare their isolated content in Instance, Tenant and
  Workspace contexts. Forged identities, runs, completed acquisitions and logical
  scopes dispatch no provider call; Flow evidence contains no redeemed credential.
- All 36 extension tests and 59 standalone AEP SDK tests passed. Coverage includes
  enrollment authentication, request/response bounds, protected bindings, exact
  contract digest, connection-origin enforcement, action refusal, credential
  reflection, effective/default response schemas and case-sensitive query names.
- Six standalone registration graph cases passed after adding the acquisition
  Tool; the Infrastructure registration/lifetime contract also passes.
- Eight affected existing source/schema Playwright smoke regressions passed after
  the Knowledge form correction, covering normal creation/editing and legacy
  projection in desktop, mobile and French variants. Four additional temporary
  en-US/fr-FR desktop/mobile checks passed with Blazor startup deliberately paused:
  the named prerendered form is disabled, then enables after connection; native
  selection and loaded-form validation preserve interactivity without a document
  POST, page error or horizontal overflow.
- Twenty focused Knowledge editor tests pass, including six new prerender,
  failed-lookup/retry and native-input validation cases. Five native dataset API
  scenarios pass with target-list and explicit Knowledge refusal assertions.
- Fifteen focused Console rendering tests cover service-owned discovery and dataset
  editing, locale rendering, ETags, parent filtering, named schema properties,
  missing adapters, stale type digests, parameter reset and preservation of pins.
- Twelve existing `schema-driven-sources`, `source-lifecycle`, `artifact-content`
  and `inherited-resource-readonly` Playwright regressions passed.
- Temporary implementation smoke checks passed in en-US/fr-FR at desktop
  (1,440 px) and mobile (390 px): service discovery/history, activation, Flow Run
  navigation, incompatible-adapter feedback, localized HTTP 422 diagnosis,
  corrected save, acquisition and content read. Screenshots confirm readable
  stacked error details, with no horizontal overflow or JavaScript errors.
  These checks are not a new reusable journey.
- TypeScript checking, changed C#/Razor formatting, ADR registry, LikeC4 checks
  and the complete Docusaurus build passed.

`DataSourceDatasetTests` uses actual discovery and acquisition Flows, the AWP
test worker, and the selected builtin local Storage, without a network origin,
cloud credential or model. Its five scenarios cover two datasets sharing one
Operation and Storage, fixed parameters, empty-input calls, concurrent admission,
cancellation, foreign references/Workspaces, reserved-prefix isolation, Storage
envelope refusal, and pending/completed acquisitions across real host restarts.

The protected OpenAPI acquisition fixture grants builtin StorageType use explicitly
in Workspace context. Tenant administration deliberately cannot read Instance
resources; its offline acquisition fixture seeds a Tenant-owned Core local type
and Storage instead. Production authorization and default provisioning are unchanged.

The architecture and route framing was approved before UI work. Explicit
maintainer approval of the implemented UX and a merged implementation PR are
still required before the reusable integrated scenario in
[#1033](https://github.com/gbaudrit/agentstration/issues/1033) starts.

## Native Artifacts materialized in Storage

Following ADR-0208, native acquisitions register their confirmed content as
`FlowRunArtifact` resources and populate the ordinary acquisition manifest. The
Artifact pins its actual producer resource revision, acquisition/Flow Run and
Storage revision/key/version/digest. History links to the Artifact catalogue and
its governed preview/download path. Content stays in the selected Storage.
Previously produced native content receives missing metadata through bounded,
authorized reconciliation; origins and stored content are not rewritten.

## Configurable Flow sequences

[ADR-0211](../../decisions/0211-resource-bound-flow-composition-points.md) defines
resource-owned bindings to typed points inside published Flows. A native dataset
can select a published orchestration and bind ordered `{ flow, parameters }` calls
to its declared points. The standard orchestration exposes `transformations` after
the governed operation acquisition. The reserved `acquisition` point is supplied by
the server; source configuration cannot replace or bypass it. Only the complete
successful chain publishes the final dataset result.

Knowledge bindings may each declare `transformations`. The Source's global sequence
runs at `postAggregate`, after its selected projection Flow has combined the prepared
inputs. These sequences transform Artifact manifests and carry exact Flow receipts
into projection provenance. A binding cannot combine the historical single
`transformationFlow` and its new sequence. An optional binding failure is recorded;
a required or global failure leaves the previous active Snapshot in place.

At save and admission, ordered child references resolve to published versions with
exact UID and hash evidence and authorized Use. A Flow Run stores the compiled graph
and accepted bindings separately from the immutable template hash. Saved parameters
are literal values. Resuming an accepted invocation retains its exact bindings and
rejects substitution. The Designer exposes composition-point names, identity policy,
input/output schemas and mapping; source editors expose declared points, published
child version selection, parameter entry and reordering in English and French.


An empty `postAggregate` sequence preserves the projection manifest. The required
projection child still executes even when no transformation is configured. Saved
literal parameters remain bounded to 65,536 JSON characters; values passed between
composed Flows have a separate limit of 1,048,576 JSON characters. The larger value
budget accounts for retained content repeated in acquisition evidence and the
projection manifest. Native content and Storage limits remain independently enforced.
Child validation failures retain their stable error code through the governed AWP
error response (HTTP 422), including `flow_composition_value_too_large` for an oversized
value, rather than surfacing as an unhandled HTTP 500.


The Definition editor provides **Create a transformation Flow** alongside adding
an existing published Flow. It creates a draft in the current owner context,
prepopulates the `{ value, parameters }` input and output contracts from the point
(or the previous child's output when appending), and opens the Designer in a new
tab. Source edits stay in the original tab. The initial transform preserves
`input.value`; publication uses the ordinary Designer workflow. Refreshing the
catalog exposes the published version without saving or replacing source edits.
The empty catalog and loading failures have separate localized explanations.
The selector resolves input schemas from the canonical Input step as well as the
graph fallback. `POST /api/flows/drafts` accepts optional `transformation` value
schemas; the application validates their bounded profile before creating any
resource and generates the envelope and graph using the existing draft service.

## Independent content lifecycle

Native datasets may explicitly opt into `lifecycle`: `allowDirect`, `timeToLive`,
`keepHistory` and `historyRetention`. TTL/history require retained input Storage;
no Storage requires explicit direct permission on the saved read-only Operation.
Direct payloads cross acquisition, transformations and projection in bounded
process-local memory. Persisted Flow/runtime records retain redacted metadata and
opaque tickets, not the content. Restart or expiry refuses the accepted handoff.
Only final Snapshot publication creates its retained output Artifact.

Knowledge has its own optional `lifecycle`, including Snapshot TTL, history and
`allowExpiredOnRefreshFailure`. Bindings independently permit an age-bounded stored
input fallback after a failed refresh. Provenance identifies both the failed
refresh and exact reused acquisition. Existing binding refresh settings remain
applicable to datasets without the new lifecycle.

History maintenance uses the existing recovery worker, current authorization,
conditional retirement claims and exact provider checks. Running projections,
native input/read leases and conservatively all nonterminal Workspace Flows defer
retirement. Local providers remove exact content; remote ASP providers may use a
verified conditional empty replacement when supported, otherwise cleanup remains
pending. Immutable Snapshots and Artifact provenance remain visible after their
bytes retire. See [ADR-0212](../../decisions/0212-source-content-lifecycle-and-ephemeral-handoff.md).


## Artifact content processing

[ADR-0213](../../decisions/0213-artifact-transformations-own-scoped-content-processing.md)
adds a distinct `artifactTransform` step to Artifact-manifest transformations. Its
private process/return region can contain ordinary Agents, Tools, mappings and
child Flow calls without another published processor. Invocation adaptation belongs
to the step, not reusable Tool definitions. Content mode supplies bounded UTF-8
text or JSON; access mode supplies execution-bound input reads/searches and a
revisioned output draft that can be reread, corrected and completed. The Agent
chooses its reading strategy. The authority materializes outputs only after all
processing regions succeed. Exact input lineage and the real producer/Storage
remain visible in Artifact receipts. Native dataset sequences still receive JSON
values; this step requires an explicit Artifact manifest rather than silently
interpreting an arbitrary dataset object as a file.

See the [authoring and content-access reference](../../reference/artifact-transformations.md)
for mappings, operation contracts, publication behavior and current bounds. The
reusable browser scenario is tracked separately by #1145, pending UX validation
and implementation merge.


Effective processing contracts follow [ADR-0214](../../decisions/0214-effective-data-contracts-follow-flow-invocations.md).
The authority derives invocation instructions from accepted contracts before the
model gateway; Worker-local ChatOptions do not override authoritative Agent
instructions. The existing Schemas handler validates bounded nullable types and
compositions as well as ordinary object contracts. Retained Artifact receipts and
direct handoffs keep concrete content evidence; Flow Run diagnostics preserve
causal contract failures across child executions.
