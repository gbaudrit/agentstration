# Additional settings and GUID deployment profiles

Additional settings are an application capability. They do not depend on GitHub
or Azure. See [ADR-0270](../decisions/0270-additional-settings-compose-independent-startup-bootstrap.md)
and the [preview operator guide](../contributing/preview-on-demand.md).

## Authoring and runtime paths

A preview source has this layout, with a canonical lowercase GUID independent of
any PR number:

```text
deploy/profiles/previews/<guid>/
  <guid>.appsettings.json
  bootstrap/                  # optional
    scenario/
      profile.yaml
      10-resources.yaml
      artifacts/example.zip   # optional local Pack
```

The image copies `deploy/profiles/` to `/app/deploy/profiles/` and the existing
shared `deploy/bootstrap/profiles/` to `/app/bootstrap/profiles/`. Both are
read-only at runtime. Relative settings/reference paths resolve from the host
content root (`/app` in the image). YAML and Pack-relative paths resolve from the
referenced profile's own directory, using the existing bounded loader.

The Core configuration selects files explicitly:

```json
{
  "Agentstration": {
    "AdditionalSettings": [
      "deploy/profiles/previews/2e65e8a4-0f73-49b6-ae24-636a65d7e793/2e65e8a4-0f73-49b6-ae24-636a65d7e793.appsettings.json"
    ],
    "Bootstrap": {
      "InitialBootstrapEnabled": true,
      "EnableAdditionalSettingsBootstrap": true
    }
  }
}
```

A file can change ordinary global configuration and declare its own block:

```json
{
  "AI": { "Provider": "Deterministic" },
  "2e65e8a4-0f73-49b6-ae24-636a65d7e793": {
    "Bootstrap": {
      "InitialBootstrapEnabled": true,
      "ProfileReferences": [
        { "Path": "bootstrap/profiles/development", "TargetLevel": "/instance" },
        {
          "Path": "deploy/profiles/previews/2e65e8a4-0f73-49b6-ae24-636a65d7e793/bootstrap/echo",
          "TargetLevel": "/tenants/dev/workspaces/default"
        }
      ]
    }
  }
}
```

`ProfileReferences` are independent startup directories, each with its own scope
and target. The manual Console catalogue continues to use Core `Bootstrap:Path`.
Existing `Path` plus `InitialProfiles` and `InitialApplications` remain supported
in each block. Define dependencies before consumers; scope order remains
Instance, Tenant, Workspace, with Core then additional-file order inside phases.

The example reuses the existing Development topology. Its `admin / admin`
credential is public disposable **local Development** data. Preview preparation generates a strong unique administrator initializer in its
existing private server configuration and preserves it on updates. Protected
configuration providers can override it, for example `Agentstration__Bootstrap__Secrets__AdminPassword`. Never put its value
in a profile, PR, command, log or public artifact. Extensions keep their existing
protected SharedKeyFile enrollment and contribution readiness requirements;
Bootstrap does not start extension processes.

## Precedence and validation

The input list is captured before loading. At most sixteen unique GUID-named
files, each at most one MiB, load once. Native .NET JSON parsing supports comments,
trailing commas, case-insensitive keys and colon-delimited keys. Later files
replace earlier ordinary values with the standard provider's semantics, including
ordinary array index overlays. Environment variables and command-line arguments
remain higher priority. Additional files do not hot reload.

An isolated file is validated **before** integration. `Agentstration:Bootstrap`
and `Agentstration:AdditionalSettings`, including all descendants and null/empty
section replacement attempts, are forbidden. Case variants, nested objects and
colon-delimited properties are equivalent. There is no general allowlist of
other sections. Invalid files fail startup explicitly; the loader never silently
ignores forbidden values or recursively extends its selected list.

Each selected GUID's Bootstrap is independently enabled by its own
`InitialBootstrapEnabled=true`, Core `EnableAdditionalSettingsBootstrap=true`
and Core `InitialBootstrapEnabled=true`. A NoBootstrap launch profile sets the
last flag to false and skips all initial application, while settings overlays
still load. An unselected GUID block never runs. Keep each Bootstrap block in its
own GUID file; Bootstrap arrays are neither concatenated into Core nor rewritten
by deployment tooling.

All applications are structurally prepared before topology mutation. Existing
scope/target checks and profile-local resource/Pack validation remain active.
The fenced startup lifecycle opens enrollment, waits within the configured
contribution deadline, applies dependent resources and only then reports ready.
Invalid or unavailable required contributions keep readiness false with bounded
diagnostics. Successful enrollment alone does not imply functional readiness.

## Deterministic executable example

From the repository root, with .NET 10 and Python 3:

```sh
dotnet publish src/Agentstration.Web -c Release -o /tmp/agentstration-guid-publish
dotnet publish src/Agentstration.Runtime.Worker.MicrosoftAgentFramework -c Release -o /tmp/agentstration-guid-worker
python3 scripts/preview/profile_example.py --published /tmp/agentstration-guid-publish --worker-published /tmp/agentstration-guid-worker
```

The script copies the published application and unmodified versioned profiles to
a disposable directory, binds a free loopback port, applies topology plus the
GUID-local echo Flow/Entry, checks liveness/readiness, authenticates using the
public fixture and starts the independently published MAF Worker using a temporary authenticated
credential to execute the deterministic Flow. It restarts on the same
SQLite storage and checks that the published Entry is preserved. It deletes its
private local state on exit. It never accesses Azure or a model endpoint.

## PR selection and lifecycle

Under the PR template's **Changes** section, add this visible subsection:

````markdown
### Preview configuration

```json
{"profiles":["2e65e8a4-0f73-49b6-ae24-636a65d7e793"]}
```
````

Use one section, one JSON object, and at most sixteen unique canonical GUIDs.
Order is significant. An absent section or empty list selects no profiles.
Unselected folders inherited from `main` stay inactive. The manual form's
`pr:NUMBER`, `/preview` on that PR, and automatic opt-in use its selected order.

Trusted-main tooling freezes/rechecks the head and selection and reads exact
source content as data, never as executable scripts. Profile references must
stay under that selected GUID's `bootstrap/` or the shared `bootstrap/profiles/`
catalogue. Missing files/directories, links, escapes, malformed JSON, reserved
keys, digest/source mismatches and changed PR metadata fail before deployment
mutation. Full native YAML/resource validation runs during fenced startup.

The existing saved deployment request binds GUID order, settings SHA-256,
selected subtree/shared content pins, source SHA and exact server image digest.
Status/destroy use this saved selection even after branch deletion or PR edits.
Feedback distinguishes requested selection from successfully applied selection
and reports Bootstrap readiness without exposing configuration values.

Ordinary deploys and metadata-only changes preserve storage, identities,
passwords and operator edits. Deselection does not delete resources created
previously; selecting a new profile creates only missing resources. Settings
changes affect the next process but do not reconcile existing definitions.
Explicit `/preview redeploy` resets owned infrastructure/data using the existing
lifecycle. Persistent `preview:auto:redeploy` keeps its existing source-update
reset contract; a metadata-only GUID change alone never resets data.
