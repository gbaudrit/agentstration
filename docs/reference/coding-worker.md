# Local native Coding worker

The optional `Agentstration.Runtime.Worker.Coding` executable runs one atomic Coding task at a time
through the existing AWP enrollment, claim, lease, cancellation and fencing protocol. It advertises
`coding-cli`, capability `1.0`, material `1.0`. It cannot claim MAF assignments. This is an optional
Linux capability; ordinary deterministic Work and the MAF worker do not require it.
See [ADR-0270](../decisions/0270-coding-cli-runs-use-awp-and-an-isolated-native-helper.md).

## Prerequisites and configuration

Install .NET 10, Git, Bubblewrap with usable user/PID namespaces, and the Codex CLI explicitly.
The host checks isolation before enrollment and fails closed if it cannot create the namespace.
There is no unrestricted-process fallback. Run the worker as its own OS account; keep its AWP
credential state outside every executable/runtime/cache directory exposed below.

Use the same `Agentstration:RuntimeWorker` authority, worker UUID, instance ID and enrollment
settings as the MAF worker. `CredentialStateFile` is the private durable AWP enrollment state;
`PairingCodeFile` supplies an operator-approved pairing code. Existing preprovisioned
`CredentialId`/`SharedKeyFile` are also supported. HTTPS is the default requirement; local HTTP
requires explicit `AllowInsecureHttp: true`. Maximum concurrent assignments must be one.

Add the following operator-owned section to a worker configuration file, replacing paths and
repository identity with actual installed values:

```json
{
  "Agentstration": {
    "CodingWorker": {
      "BubblewrapExecutable": "/usr/bin/bwrap",
      "GitExecutable": "/usr/bin/git",
      "CliExecutable": "/opt/codex/bin/codex",
      "ReadOnlyRuntimeDirectories": ["/opt/codex"],
      "Repositories": [{
        "provider": "github",
        "nativeId": "123456",
        "origin": "https://github.com/example/project.git",
        "directory": "/srv/coding-cache/project.git"
      }],
      "CheckExecutables": { "dotnet": "/usr/share/dotnet/dotnet" }
    }
  }
}
```

The standard host accepts
`--Agentstration:RuntimeWorker:AuthorityUrl=https://authority.example` and other explicit
configuration keys, environment variables with `__` separators, or an `appsettings.json` in the
worker content root. Publish the executable and supply the configuration there for routine use:

```sh
dotnet publish src/Agentstration.Runtime.Worker.Coding -c Release -o ./coding-worker
cd coding-worker
dotnet Agentstration.Runtime.Worker.Coding.dll
```

Prepare an operator-maintained **bare** cache with the exact canonical origin and required base
commit. Refreshing this cache is an explicit operator operation, outside task execution. The task
cannot select an arbitrary path, remote fetch, repository or credential. Admission resolves the
Project's protected repository connection through the ordinary Coding gateway and captures its
native identity. The worker verifies that identity, cache origin and exact base commit before
checking out a disposable repository. A cache mismatch or missing commit is an explicit failure.
This first slice does not relay repository credentials to a CLI or implement remote cache refresh.

Only curated read-only OS, .NET, CLI and approved runtime directories plus the cache enter the
helper namespace. Its worktree, temporary files, home and evidence directories are private bounded
tmpfs mounts; default worktree capacity is 64 MiB (configurable from 1 to 256 MiB), cache scan is
bounded to 50,000 entries/256 MiB by default. Credential-path overlap and symbolic-link mount paths
are rejected. Native execution cannot modify `.git`. All subsequent Git inspection also executes
inside isolation. Git hooks, external diffs, textconv, prompts and global/system Git configuration
are disabled. Repository checks use fixed operator command aliases and bounded arguments in a
network-disabled namespace with a fresh home and no native account file.

If inference needs native authentication, `NativeApiKeyAuthFile` may point to an explicit Codex
`auth.json` containing `OPENAI_API_KEY`. It is copied with mode 0600 into the private native home.
This first version supports static API-key authentication only; OAuth `tokens` are rejected because
refresh state cannot be discarded safely. Native authentication is separate from Agentstration
Secrets and AWP credentials. Known key plaintext in summary/patch is refused; raw native stdout,
stderr and prompts are not persisted. Never place real credentials in task/profile content.

## Publish and admit a task

An administrator installs an Instance RuntimeProfile with `runtimeType: coding-cli` and grants the
consumer Workspace `Discover` and `Use`. Publish a Workspace HarnessProfile and native configuration
as described in [HarnessProfiles](harness-profiles.md). Publish an ordinary Flow with this definition:

```json
{
  "flowKind": "runtime",
  "runtimeCapability": "coding-cli",
  "capabilityVersion": "1.0",
  "materialVersion": "1.0",
  "configuration": {
    "runtimeProfile": { "name": "coding-local", "namespace": "default" },
    "harness": { "name": "coder", "namespace": "default", "version": "1.0.0", "digest": "<published SHA-256>" },
    "repository": "example/project",
    "timeoutSeconds": 300,
    "checks": [{ "name": "build", "command": "dotnet", "arguments": ["build", "--no-restore"] }]
  }
}
```

Publication validates and captures local UIDs/generations in addition to portable semantic pins.
The Flow has no separate workflow graph. The Console exposes its saved identity and YAML without
proposing the workflow designer. This increment adds no native Flow creation wizard.

Use the normal WorkPlan execution API with an atomic task, Project ID, exact published Flow and
input `{ "baseRevision": "<40- or 64-character commit>", "bindings": {} }`. Bindings must satisfy
the exact HarnessProfile schema. Admission and material resolution reauthorize runtime grants,
exact profile pins and the captured Project repository connection; later Project edits cannot
substitute another connection. A direct Flow Run without an admitted functional attempt fails.

The helper materializes native files only after checking actual repository files and provenance.
Unowned or modified instructions/configuration are explicit conflicts. Codex executes ephemerally
with JSONL reporting, ignored user config, private home and trust scoped to the disposable checkout;
its materialized native sandbox settings still apply. Native inference is an explicit opt-in and
uses the operator's native account, while MAF continues to use governed server model operations.

## Evidence and limits

Assignment Diagnostics retain `codingConsumption` observations and one `codingResult` receipt with
stable Workspace/Principal/Project/Plan revision/Task admission/functional attempt/Flow Run/AWP
assignment/assignment-attempt/invocation coordinates. Usage IDs derive from invocation plus turn
ordinal. Input/cached/output/reasoning counters distinguish known, partial and unavailable reporting;
invalid/inconsistent counters do not become zero. Cached and reasoning counters are subsets, not
additional totals. Cost, currency and provider duration are not invented.

The final receipt contains a bounded 64 KiB binary Git patch and SHA-256, an 8 KiB summary, CLI exit,
actual configured check exits and explicit unavailable checks/usage. After checks, optional
`codeChanges` retain bounded regular UTF-8 staged blobs/modes/deletions for
[delivery](coding-delivery.md), excluding materialized harness files. Unsupported encoding/file type/size
sets a separate `codeChangesDiagnostic` without rewriting the generic CLI/check outcome. Credential
exposure is refused before encoding, and no server-side patch application is used. Timeout is bounded to 1–600
seconds. AWP cancellation kills the isolated process tree; partial observations already accepted
under the lease remain durable. Failure/cancellation retain their receipt reference and do not
accept the functional task. Lease expiry cannot authorize late events or terminal mutations.
The canonical [task execution history](task-execution-evidence.md) retains the result/error snapshot;
AWP assignment history retains placement and detailed observations across authority restart.

Deterministic tests use real Bubblewrap, Git and AWP with a fake CLI, including success, failure,
partial/absent usage, cancellation, timeout, incompatible placement, ownership conflicts, authority
credential exclusion and rejected native-key output. They do not call a model. Vendor inference is
a separately reported opt-in. This worker does not publish code, accept issues, schedule retries,
recover a lost worker, review across workers or execute multiple repositories. Shared accounting and
issue delivery consume these retained facts in their owning increments.
