# Data Source operation and Storage composition

This vertical implements [#1041](https://github.com/gbaudrit/agentstration/issues/1041)
on `integration/fr-1032`. See [ADR-0202](../../decisions/0202-data-sources-compose-operation-acquisitions-with-logical-datasets.md)
for ownership, the compatibility decision and routes.

## Configuration and execution

1. Configure a Connected Service and discover its typed Operations using the
   existing durable discovery lifecycle. Discovery preserves the service UID
   and generation and never enables an operation automatically.
2. Explicitly enable a reviewed Operation revision. `PUT
   /api/connected-service-operations/{name}/enabled` requires its current
   `If-Match`. Activation performs no acquisition and publishes no Tool.
3. Create a Data Source selecting an exact existing Storage, an allowed relative
   prefix and one exact Operation with saved JSON parameters. Its type version
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
explicit activation and revision selection. A changed reference is selected
explicitly; refresh does not rewrite saved pins. Existing profile resources,
discoveries, targets, acquisitions, Tools and Knowledge consumers are retained.

The integration baseline includes StorageType administration and scoped generic
grants from #1079, and protected OpenAPI discovery from #1059. Acquisition uses
the merged `local@1.0.0` contract and current Storage/StorageType Use decisions;
it creates no grants. OpenAPI discovery alone does not make its Operations
acquisition-eligible: their exact type version must also declare the compatible
acquisition adapter. No invocation contract is inferred from a discovered HTTP
method or from its read-only flag.

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

Checks completed on 2026-11-08:

- Release solution build: zero warnings and errors.
- Required Fast lane: 1,001 passed, zero failed or skipped.
- Required Integration lane: 1,083 passed, zero failed, 12 existing optional
  integrations skipped (two PostgreSQL and ten live-model cases).
- After the final permission projection and partition-prefix hardening, the
  five native dataset integration tests and the Fast lane passed again. The
  full Integration lane was completed before those final focused changes.
- Ten existing schema-driven source, source lifecycle and artifact-content
  Playwright regressions passed against the local product.
- Temporary implementation smoke checks created, acquired and read a native
  dataset in en-US/fr-FR at desktop (1,440 px) and mobile (390 px) widths, with
  no horizontal page overflow or JavaScript errors. These checks are not a new
  reusable journey.
- Console rendering/locale tests, TypeScript checking, the application-surface
  catalog, ADR/LikeC4 checks and the complete Docusaurus build passed.

`DataSourceDatasetTests` uses actual discovery and acquisition Flows, the AWP
test worker, and the selected builtin local Storage, without a network origin,
cloud credential or model. Its five scenarios cover two datasets sharing one
Operation and Storage, fixed parameters, empty-input calls, concurrent admission,
cancellation, foreign references/Workspaces, reserved-prefix isolation, Storage
envelope refusal, and pending/completed acquisitions across real host restarts.

The architecture and route framing was approved before UI work. Explicit
maintainer approval of the implemented UX and a merged implementation PR are
still required before the reusable integrated scenario in
[#1033](https://github.com/gbaudrit/agentstration/issues/1033) starts.
