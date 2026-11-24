# Generic Git operations

`Agentstration.Extensions.Git` now offers two separate capabilities. The existing
`git` Source Provider resolves public refs and archives exact commits. The optional
`git.repository` AEP/MCP Tool resolves, reads, diffs and publishes authenticated Git
changes. It does not interpret plans, tasks, Bootstrap or usage documents, or tracker APIs.

## Configure an optional connection

Launch the existing Git extension with `GitOperations:Enabled=true`, a persistent
`GitOperations:StateDirectory` and one to 16 exact `GitOperations:AllowedRepositories`.
Operations are disabled by default. `GitSourceProvider` executable, temporary directory
and repository-byte limits are reused. The default operation timeout is 25 seconds.
HTTPS is the normal transport. HTTP loopback requires `GitOperations:AllowHttpLoopback`;
file fixtures also require `GitSourceProvider:AllowLocalRepositories`. Neither is enabled
by default. This does not enable private Source Channel acquisition or change the
reserved Source `credentialsRef` behavior.

Discover/enable the `git.repository` contribution through the normal AEP ToolProvider.
Publish an existing ToolDefinition relay that pins its source UID/generation and binds:

| Fixed path | Value |
| --- | --- |
| `/repository` | Exact allowed repository URL, without credentials/query/fragment |
| `/authentication` | `Basic`, `Bearer`, or explicitly `None` |
| `/username` | Basic username, otherwise an empty string |
| `/credential` | Secret reference for authenticated access; never a literal token |
| `/allowedRefPrefixes` | Exact/prefix branch names, e.g. `["refs/heads/shared"]` |
| `/allowedPathPrefixes` | Descendant prefixes, e.g. `["shared"]`; `.` explicitly grants the whole tree |

Copy the source `agentstration.git.repository` capability metadata into the published
relay. Its version `1.0` declares resolve/read/diff/publish, `expectedBase=true` and
`operationRecovery=true`, `exactRevisionReads=true`, `historyRead=true` and
`idempotentOperations=true`. History reads cover revisions reachable from the allowed
ref; external history removal fails explicitly. Fixed fields disappear from caller schemas and cannot be
overridden. Existing Secret resolution, Tool approval/access policy and trusted
Workspace/Principal context remain authoritative. The extension has no ambient token.

## Wire operations

The public relay takes `operation`, `mode` (`execute` or `recover`), a stable opaque
64-character lowercase hexadecimal `operationKey`, `intentDigest`, and `request`.
The consumer derives keys from its trusted scope and logical command; they are not
Git refs or artifact names. The request contains:

| Operation | Required request fields |
| --- | --- |
| `resolve` | `refName`, an explicitly allowed full `refs/heads/*` name |
| `read` | `refName`, exact `revision`, and one to 64 `paths` |
| `diff` | `refName`, exact `revision` and `baseRevision`; optional permitted `paths` |
| `publish` | `refName`, `baseRevision`, `expectedRefRevision`, `changes`, `message` |

Existing-ref publication requires identical base/expected revisions. A new ref uses
`expectedRefRevision:null`, with an existing base or `baseRevision:null` for an empty
repository root commit. Each change is `{ "path": "shared/file.json", "contentBase64":
"...", "executable": null }`. Null content deletes a file, empty base64 creates an empty
file, and omitted/null executable mode preserves its old regular-file mode. At most
64 changes and 1 MiB decoded content are accepted. Publication never checks out or
executes repository content.

Responses echo operation key/digest and explicit `Succeeded`, `Missing`, `Conflict`,
`Rejected` or `Uncertain` status, ref, exact publication revision/base and observed
current revision where applicable. Read returns path/blob revision/base64 content/mode.
Diff returns `diffBase64`, preserving exact bounded native patch bytes. Read/diff output
is bounded to 1 MiB. Protocol v1 uses exact lowercase SHA-1 commit IDs (40 characters).
Exact reads require commits reachable from the currently authorized ref; unavailable
or rewritten history fails explicitly. Symlink/submodule reads and unsafe paths fail.

## Publication and recovery

Git constructs a candidate directly on the expected base, with stable journal time and
operation trailers. A native ref lease checks the exact old value, including absence
for new refs. There is no force-history update or automatic merge. Movement before or
during receive is rejected; a concurrent contribution is preserved.

Persist the state directory across restarts. Before push, the journal records Sent and
the exact candidate. Reuse the original key, digest, connection and complete request
with `mode:recover` after response loss/cancellation. Recovery verifies the candidate
in native history and its original operation identity; it returns the original commit,
without another commit or push. If the remote cannot prove that artifact, status stays
Uncertain. Do not treat a missing native ref or lost state volume as permission to retry.
Prepared/no-entry Missing can authorize explicit same-key execution after the consuming
application rechecks its current intent. Changed input/repository/digest conflicts.

Calls have four active slots and bounded waiting admission. Cancellation kills native
process trees and removes temporary repositories. Credentials enter only process-scoped
Git configuration for the exact URL; stderr/command lines are not returned or logged.
Durable operation keys and tracing correlate native work without persisting credentials.

## Validation boundary

Offline MSTest fixtures use real Git and an authenticated local smart-HTTP server.
They check exact reads/publications/diffs, stale and actual receive-time races, response
loss and restart, concurrent replay, path/repository/credential denial, and the existing
public Source workflow. A protected-relay API test exercises actual AEP/MCP and Secret
resolution and denies fixed-credential overrides/foreign Workspace context. Live hosted
Git authentication and permission smoke remains a separate opt-in with a disposable repo.

See [ADR-0265](../decisions/0265-generic-git-publication-uses-protected-connections-and-native-cas.md).
