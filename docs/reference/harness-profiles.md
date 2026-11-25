# HarnessProfiles and native configuration

A HarnessProfile publishes a pinned native configuration and instructions for a developer CLI.
The first materializer is Codex `1.0.0`. Preview/export work offline and do not run Codex or modify
a repository. See [ADR-0268](../decisions/0268-harness-profiles-publish-pinned-standalone-native-configuration.md).

## Publish and export

Routes are beneath `/api/workspaces/{workspace}/coding`. Resource reads/writes require the current
Workspace, the corresponding resource permission and the normal resource governance authority.
Profiles/native configurations are Workspace-owned; another Workspace cannot be selected through
an embedded native reference.

| Route | Result |
| --- | --- |
| `POST /harness-configurations` | Publish immutable native namespace/name/version/settings. |
| `GET /namespaces/{namespace}/harness-configurations/{name}/versions/{version}` | Read the exact native configuration. |
| `POST /harness-profiles` | Create an editable ordinary resource envelope with kind `HarnessProfile`. |
| `GET /namespaces/{namespace}/harness-profiles/{name}` | Read its draft and ETag. |
| `PUT /namespaces/{namespace}/harness-profiles/{name}` | Body `definition` and `expectedETag`; stale writes conflict. |
| `POST /namespaces/{namespace}/harness-profiles/{name}/versions` | Body `version` and `expectedETag`; publish a pinned immutable version. |
| `GET /namespaces/{namespace}/harness-profiles/{name}/versions/{version}` | Read the exact version and native configuration pin. |
| `POST /harness-preview` | Return native files, ownership, digests, provenance and usage field availability. |
| `POST /harness-export` | Return a deterministic ZIP with the same files and portable file digest header. |

Native configuration creation uses:

```json
{
  "name": "codex-default",
  "namespace": "default",
  "version": "1.0.0",
  "definition": {
    "materializer": "codex",
    "materializerVersion": "1.0.0",
    "settings": { "model": "gpt-5.3-codex", "sandboxMode": "workspace-write", "approvalPolicy": "on-request" }
  }
}
```

Choose a model available to your native CLI account before publishing. The model value above is
an explicit example, not a local automated-test requirement or a promise of account availability.
Read-only is the default sandbox. Supported approval values are `on-request`, `untrusted` and
`never`; the latter still retains the selected native sandbox. Full-access mode is unsupported.
The generated workspace-write sandbox sets `network_access = false`.

A profile definition contains `displayName`, `instructions`, `bindingsSchema`,
`instructionsPath: "AGENTS.md"` and `nativeConfiguration: { "name", "namespace", "version" }`.
Use `{{binding-name}}` placeholders in instructions. Version 1 bindings are bounded strings with
an object JSON Schema, `properties`, explicit `maxLength`, `required` and
`additionalProperties: false`. Every placeholder must be a required property. No Secret binding
or resolver participates in export. Do not put credentials in user-authored instructions or text.

Preview/export bodies contain `namespace`, `name`, `version`, `expectedDigest` (the published
profile digest), `bindings` and `existingFiles`. Each existing file observation has `path`,
`digest`, `ownerProfile` and `kind` (`file`, `directory` or `symlink`). Supply relevant actual
repository observations before a later write. Preview is a declaration check; a writer must also
verify the actual filesystem immediately before writing.

Outputs are root `AGENTS.md`, `.codex/config.toml` and `agentstration-harness.provenance.json`.
Unowned or changed outputs, symlink/file parents, directory/file collisions, path case conflicts,
root `AGENTS.override.md`, traversal and unsafe path forms fail explicitly. Identical owned replay
is accepted. There is no overwrite/reconciliation switch. The archive uses UTF-8/LF, sorted paths
and fixed timestamps. Its ETag describes archive bytes; `X-Agentstration-Harness-Digest` describes
the portable file inventory. Provenance records exact semantic profile/native/materializer versions
and digests, without local Workspace IDs or runtime credentials.

## Workspace Bootstrap

The optional `workspace-codex-harness` catalog profile demonstrates the two owning handlers:
`HarnessNativeConfiguration` and `HarnessProfile`. Its ordered documents first publish the exact
native configuration and then the profile version referencing it by name/namespace/version. No
local UIDs are required in the manifest. Bootstrap is explicit and does not launch Codex.
An equivalent published version is skipped; a different immutable version or an edited unpublished
draft causes a conflict. Missing planned/installed native prerequisites fail during planning/apply.

## Standalone CLI and usage

After reviewing/exporting into a repository, install/configure Codex and use its normal repository
trust workflow. Run `codex exec --json "<task>"` from the repository. No Agentstration service or
SDK is required to read these files. Project `.codex/config.toml` applies only to trusted projects;
user/system config and nested instruction overrides can also affect effective execution. Export
does not automatically grant permanent trust or copy the user's authentication.

The installed fixture CLI reported `0.172.0-alpha.2`; parser/help checks are separate from a live
model execution. Current native reporting uses JSONL events. A `turn.completed` event's `usage`
can include `input_tokens`, `cached_input_tokens`, `output_tokens` and optional
`reasoning_output_tokens`. Cached input is a subset of input, and reasoning output is a subset of
output when reported. Do not add those subsets again to totals. Missing counters, provider
cost/currency and provider duration stay unavailable. Automatic correlated accounting belongs to
#1348; coordinated local Coding execution belongs to #966.

## Reproduce a standalone clone

The checked-in [standalone fixture](https://github.com/gbaudrit/agentstration/tree/codex/946-durable-task-trees/tests/fixtures/standalone-codex) contains an
exact deterministic v1 export and a native Git recipe. It is also reusable as a minimal repository
input for Workspace seeding. Its `fixture-model` is intentionally offline; a vendor smoke needs
a separately published native configuration naming an available model. The ordinary
`workspace-codex-harness` catalog demonstrates that publication shape without enabling it by default.

`HarnessProfileApiTests.FreshGitCloneRunsOfflineAfterAuthorityStopsAndRetainsV1AndNativeMergeConflicts`
publishes through the real service/API/SQLite path, commits the downloaded archive, clones it and
disposes the authority before starting a separate inventory probe. The probe references only the
.NET base libraries. The test restarts/publishes v2 and proves the v1 clone's bytes/commit remain
unchanged, then asserts ordinary Git conflict markers for divergent native edits. Changed native
files invalidate provenance. No background propagation, runtime profile fetch or Git reconciliation
is installed. This extends ADR-0268 without adding another execution authority.

Run the offline scenario with the standard Release build:

```sh
dotnet test --project tests/Agentstration.Api.Tests --configuration Release --no-build --filter 'FullyQualifiedName~FreshGitCloneRunsOffline'
```

The fixture README separates an installed vendor CLI smoke from the offline process probe. Native
help/parser/diagnostic checks are not evidence of a successful live model turn. Preserve the
native project's trust/override behavior; publishing or cloning does not authorize permanent trust.

Native references: [non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode),
[configuration precedence](https://learn.chatgpt.com/docs/config-file/config-basic), and
[AGENTS.md discovery](https://learn.chatgpt.com/docs/agent-configuration/agents-md).
