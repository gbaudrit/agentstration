# Standalone Codex repository fixture

These three native files are a byte-for-byte export of the persisted `default/coder@1.0.0`
HarnessProfile used in `HarnessProfileApiTests`. The fixture uses `fixture-model` deliberately:
it is offline test data, not a live model identifier. The ordinary profile catalog at
`deploy/bootstrap/profiles/workspace-codex-harness` supplies the corresponding authoring shape
for an explicitly chosen native model and Workspace bootstrap.

Copy this directory, including its dotfiles, to a disposable directory outside Agentstration.
Initialize and clone an ordinary repository:

```sh
git init --initial-branch=main
git add .
git -c user.name="Standalone fixture" -c user.email="fixture@agentstration.invalid" commit -m "Native harness v1"
git clone --no-local . ../standalone-clone
cd ../standalone-clone
```

No Agentstration service, API, SDK, profile URL, Secret or database is needed in this clone.
`AGENTS.md` is the native instruction source; `.codex/config.toml` is the native project config.
The JSON file records exact portable source versions and native file digests. `.gitattributes`
preserves the UTF-8/LF bytes used by those digests on Windows as well as Unix.

The automated API test commits the real ZIP output, clones it, stops/disposes the authority,
launches the separate dependency-free `NativeHarnessProbe` process, restarts/publishes v2, and
verifies v1 remains identical. It also commits conflicting native edits on two branches and
asserts Git's unmerged path/markers. The probe rejects changed native files; it is an inventory
fixture, **not a Codex emulator or a live model test**.

For a vendor smoke, first export a profile with a real model supported by your installed Codex
account. Use Codex's ordinary authentication and project trust workflow, then run from that
fresh clone with Agentstration stopped:

```sh
codex --version
codex exec --json "Read AGENTS.md and report the requested project name. Do not modify files."
```

The fixture configuration selects the read-only sandbox and disables shell sandbox networking.
Native inference still needs the CLI's chosen model/provider and credentials. A real response is
an optional vendor check, separate from the required offline suite. Help/parser or diagnostic
prompt inspection alone does not prove inference. Do not commit authentication or captured
model output. User/system config, root `AGENTS.override.md` and nested instructions may change
effective execution; project config is ignored until the project is trusted. Native edits remain
ordinary Git edits, and publishing v2 never modifies this clone. There is no auto-update hook.
