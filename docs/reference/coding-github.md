# Coding GitHub connections

The optional `Agentstration.Extensions.GitHub` executable supplies native repository,
issue and pull-request operations through existing AEP/MCP discovery. The deterministic
standalone server remains executable without this provider.

## Configure one consumer connection

1. Launch the extension with a persistent `GitHub:StateDirectory`. By default only
   `https://api.github.com` is an allowed API base. For an enterprise base, configure
   its exact HTTPS URL in `GitHub:AllowedApiOrigins`; redirects are disabled. Keep
   enrollment/transport credentials separate from downstream GitHub credentials.
2. Register/enroll it using existing ExtensionRegistration and AEP ToolProvider flows.
   Discover `github.issue` and `github.coding`, and explicitly enable required Tools.
3. Publish two ToolDefinition relays to the discovered exact Tool UID/generation.
   Use the existing Tool relay editor/API and bind these fixed input paths:

   | Path | Binding |
   | --- | --- |
   | `/credential` | Secret reference to this consumer's GitHub token |
   | `/apiBaseUrl` | Literal or Parameter, for example `https://api.github.com` |
   | `/allowedRepositories` | Literal array of `{ "repository": "owner/name", "nativeId": "123" }` |
   | `/managedLabelPrefix` | Issue relay only, for example `agentstration:` |

   Supply only the source's capability metadata on the relay definition:
   `agentstration.work.task-provider` for issues and `agentstration.coding.repository`
   for repository/PR operations. Metadata persists in the immutable publication and
   projected Tool; its platform identity keys are reserved. Caller arguments cannot
   override fixed fields. Tokens never belong in manifests, Project bindings or API
   operation bodies. Secret rotation uses existing protected resolution.
4. Bind the exact published issue Tool in the private Project with purpose
   `TaskOperations`, and the repository Tool with purpose `Repository`. A Tool
   reference pins scoped address, UID and generation; omit `version` (the publication
   version is already in its immutable name). Associate the Project with the task
   tree or WorkPlan. A Project has at most one Repository binding.

## Canonical host API

All routes are under `/api/workspaces/{workspaceName}/projects/{projectId}` and use the
authenticated current Workspace/Principal and current RunsExecute authority.

| Route | Body/outcome |
| --- | --- |
| `POST /coding/repositories/resolve` | `taskId`, `repositories`, optional `expectedTaskVersion`; returns native identity, capabilities, observed commit and pinned connection/context |
| `POST /coding/pull-requests` | `id`, `taskId`, `expectedTaskVersion`, `expectedProjectVersion`, `connection`, `operation`, `input`, optional `recover`; returns scoped operation coordinates and structured provider status |
| Existing `/task-provider` and `/task-operations` routes | Generic issue discovery, read, publication, additive update, private durable history and explicit resume; see [WorkPlans](work-plans.md#optional-external-task-operations) |

An atomic task must select exactly one repository; composite work may resolve up to
16. Repository names are exact authorized names, paired with native IDs to reject
rename/replacement. Merely selecting a repository performs no clone, task execution,
plan activation, issue publication or acceptance. Selection is an observation; later
consumers retain its pins and reauthorize before effects.

Repository capability metadata version `1.0` declares operations `repository.resolve`,
`pull-request.create` and `pull-request.read`. Pull-request input contains repository,
optional native number, title/body, head/base, expected head revision and draft flag.
Creation requires an atomic task, observed Project/task versions and the exact
connection returned by resolution. Reuse the same ID, input, versions and connection
with `recover: true` after response loss. Recovery may inspect an older task intent;
it cannot adopt another relay publication. API acceptance includes the explicit
Succeeded/Uncertain/Missing provider result; inspect it before proceeding. Explicit
provider rejections return Problem Details with HTTP 409 and the operation ID.

## Native issue semantics and limits

- Generic Read returns authorized issue content, native parent/children/blocked-by
  relations, labels and an observation revision. An inaccessible or incomplete
  relation list fails instead of reporting absence. Reads are bounded to 1,000
  observed items per native collection and 100 returned relations/32 labels.
- Publish accepts a body up to 32,600 characters, leaving room for its recovery marker,
  and creates only the requested issue. Children stay internal unless a consumer
  explicitly publishes them. A native body marker permits original-artifact recovery.
- Update requires the observed revision. It adds only labels under the configured
  managed prefix and explicit `parent` or `blocked-by` relations. Other labels remain.
  A parent relation never replaces an existing parent. Relations name an authorized
  repository and native issue number. Sub-issues require the same repository owner.
- Title/body/state edits return `github_conditional_update_unsupported`. GitHub's
  unsafe REST methods do not provide general atomic If-Match semantics; the revision
  is a preflight guard for additive operations, not cross-instance claim authority.
- Native permission denial, changed repository identity, unsupported capabilities,
  rate limits, stale observations and partial outcomes use bounded technical error
  codes. Credentials and native response bodies are excluded from diagnostics.
- PR creation checks the requested head SHA before and after the call. A branch race
  remains Uncertain; callers need exclusive branch ownership/CAS for stronger claims.

Provider calls have a 30-second host deadline, a 25-second native HTTP timeout, bounded
structured output and normal Tool governance/security audit. Approval-required routes
fail closed; no implicit approval or external artifact is produced.

## Durable recovery and validation

Persist the extension state directory together with its deployment. Same-key leases,
write-ahead Sent receipts, intent/authority checks and native recovery prevent duplicate
creates after process restart or cancellation. Absence from native lists after Sent
is not proof of no effect. Recovery remains Uncertain when it cannot prove a unique
original artifact; it never blindly resubmits. Losing the provider state volume needs
manual native reconciliation before issuing writes again.

Offline tests use synthetic repositories/tokens and a fake native HTTP boundary with
real AEP/MCP discovery and protected relays, plus provider/host restart and response-loss
injection. They do not validate live GitHub credentials, permissions or service behavior.
Live vendor smoke is an explicit opt-in using a disposable repository. No browser
journey is introduced by this increment.

See [ADR-0264](../decisions/0264-coding-github-operations-use-protected-relays-and-native-recovery.md).

Generic repository content/diff/publication operations use the separate existing
[Git extension capability](git-operations.md); native issue and PR policy stays here.
