# Data Source operation and Storage composition

This vertical implements [#1041](https://github.com/gbaudrit/agentstration/issues/1041)
on `integration/fr-1032`. See [ADR-0202](../../decisions/0202-data-sources-compose-operation-acquisitions-with-logical-datasets.md)
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
   refuses a changed version. Content is data, never executable markup.

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
Creating a Knowledge Source with such an input now returns an explicit HTTP 422
`knowledge_projection_dataset_input_unsupported`, localized by the editor, rather
than a server error. This correction does not fabricate a Profile, rewrite a
source or acquired content, or implement native Knowledge Snapshot composition.

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
- Generic relay and parameterized Tool exposure remains dependent on unmerged
  #1044. No dataset-specific replacement relay is introduced. Historical fixed
  acquisition Tools continue to use their original implementation.

## Validation evidence

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
