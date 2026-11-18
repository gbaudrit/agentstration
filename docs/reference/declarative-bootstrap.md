# Declarative bootstrap

Agentstration treats `Agentstration:Bootstrap:Path` as the root of a profile catalog. Each immediate child directory is one independently selectable profile. Startup applies the ordered `Agentstration:Bootstrap:InitialProfiles` list only when `Agentstration:Bootstrap:InitialBootstrapEnabled` is `true`. Initial bootstrap is disabled by default.

Profiles use configuration order; files inside each profile use ordinal lexical filename order. Use profile order for dependencies across profiles and numeric filename prefixes for dependencies inside one profile. A file may contain one YAML resource or several YAML documents separated by `---`. Bootstrap runs after persistence initialization under the fenced instance initialization lifecycle. Handlers only create absent resources and never reconcile existing ones. Legacy endpoint synchronization can run before and after Bootstrap only when `Agentstration:Extensions:DiscoverOnStartup` is explicitly enabled; it is disabled by default. New staged startup accepts extension-initiated announcements instead.

For example:

```text
deploy/bootstrap/profiles/
  base/
    10-tenant.yaml
  development/
    00-platform-administrator.yaml
    20-workspace.yaml
  demo/
    10-demo-resources.yaml
```

Profile names identify one immediate child directory. Absolute paths, separators, `.` and `..` are rejected. A selected profile must exist and may appear only once. The root and selected profiles fail clearly when an enabled startup is misconfigured; an existing selected profile without YAML files is a valid no-op.

## Initial topology

An initial local topology is composed from four independent resources. The Platform administrator is intentionally first because it is global to the instance and does not belong to a Tenant or Workspace.

`00-platform-administrator.yaml`:

```yaml
apiVersion: agentstration.io/v1
kind: PlatformAdministrator
metadata:
  name: admin
definition:
  displayName: Platform administrator
  email: admin@example.test
  passwordFrom:
    configuration: Agentstration:Bootstrap:Secrets:AdminPassword
```

`10-tenant.yaml`:

```yaml
apiVersion: agentstration.io/v1
kind: Tenant
metadata:
  name: dev
definition:
  displayName: Development
```

`20-workspace.yaml`:

```yaml
apiVersion: agentstration.io/v1
kind: Workspace
metadata:
  name: default
definition:
  displayName: Default workspace
  tenantRef:
    name: dev
```

`30-principal-default-context.yaml`:

```yaml
apiVersion: agentstration.io/v1
kind: PrincipalDefaultContext
metadata:
  name: admin
definition:
  principalRef:
    localAccount: admin
  tenantRef:
    name: dev
  workspaceRef:
    name: default
```

`PrincipalDefaultContext` is a navigation preference, not an authorization grant. Its `metadata.name` must match `principalRef.localAccount`. The referenced account, Tenant, and Workspace must already exist, and the Workspace must belong to the referenced Tenant. A different persisted default is reported as a non-fatal conflict rather than overwritten.

A Platform administrator has instance-wide access to every active Tenant and Workspace, including resources created later. Bootstrap does not create Tenant memberships, Workspace memberships, or role assignments for that Principal. Ordinary Principals still require their normal memberships and roles.

## Manual profile application and bindings

After initialization, a Platform administrator can open **System → Bootstrap profiles** to compose profiles in lexical dependency order, choose an explicit Tenant or Workspace target, resolve profile bindings, preview the complete application, and apply it. Preview is side-effect free and its digest covers the ordered profile contents, scope, target, and binding selections. The retained application history records the actor, target, selections, outcomes, and partial failures.

Workspace profiles can declare ordinary interactive bindings. Automatic extension contribution bindings are also supported in Instance and Tenant profiles. Each declaration is typed and may provide a default target for non-interactive application:

```yaml
apiVersion: agentstration.io/v1
kind: BootstrapProfile
metadata:
  name: solution-discovery
definition:
  displayName: Solution discovery
  targetScope: workspace
  bindings:
    - name: agent-model
      targetKind: modelProfile
      displayName: Agent model
      required: true
      defaultTarget:
        name: reasoning-default
        namespace: default
```

An ordinary resource references the declaration through a reserved structured object:

```yaml
definition:
  modelProfile:
    binding: agent-model
```

The resolver replaces only an object containing the single `binding` property with the selected `ResourceReference`; it does not perform string interpolation. A selection cannot cross Workspace boundaries. It may reference existing state or a compatible resource planned earlier in the same composed application. Required unresolved bindings, unknown names, malformed placeholders, and unavailable or type-incompatible targets invalidate preview. Secret bindings carry only a resource reference, never the secret value.

## Credentials and deployment

Configure the path without putting a production password in YAML:

```json
{
  "Agentstration": {
    "Bootstrap": {
      "Path": "./bootstrap/profiles",
      "InitialBootstrapEnabled": true,
      "InitialProfiles": [
        "base",
        "production"
      ]
    }
  }
}
```

Supply the referenced value through any normal .NET configuration provider. For example, a container can mount declarations read-only and inject the secret separately:

```yaml
volumes:
  - ./bootstrap:/app/bootstrap:ro
environment:
  Agentstration__Bootstrap__Path: /app/bootstrap/profiles
  Agentstration__Bootstrap__InitialBootstrapEnabled: "true"
  Agentstration__Bootstrap__InitialProfiles__0: base
  Agentstration__Bootstrap__InitialProfiles__1: production
  Agentstration__Bootstrap__Secrets__AdminPassword: ${ADMIN_PASSWORD}
```

The password must satisfy the ASP.NET Core Identity policy. Identity validates and hashes it; Agentstration never adds it to a canonical resource or logs it. If the declared account already exists and its Principal is already a Platform administrator, bootstrap skips it without resolving the password. Password changes therefore survive restarts. An existing account without the declared grant is a non-fatal conflict: startup continues without changing its password or granting new privileges.

Outside Development, Agentstration does not choose or activate an unattended topology. Supply explicit manifests and the referenced secret, or use the one-time interactive `/bootstrap` page. The page asks for the initial account, Tenant, and Workspace; it creates the same global Platform administrator plus its default context.

## Visual Studio and local Development

The default `http` and `https` launch profiles use `deploy/bootstrap/profiles` as their catalog, enable initial bootstrap, and select `development`. They are available from Visual Studio or the command line:

```powershell
dotnet run --project src/Agentstration.Web
dotnet run --project src/Agentstration.Web --launch-profile https
```

Both Development profiles create `admin / admin`, Tenant `dev`, Workspace `default`, and the administrator's default context on a fresh instance. The known credential is a development fixture, not a secret: never use these profiles for an exposed or production instance. Development relaxes the local Identity password policy to accept it; every other environment retains the strong password policy.

Use the corresponding explicit profile when Development must start without applying initial profiles:

```powershell
dotnet run --project src/Agentstration.Web --launch-profile http-NoBootstrap
dotnet run --project src/Agentstration.Web --launch-profile https-NoBootstrap
```

Build configuration and host environment are independent. `dotnet run --configuration Release` still uses the default `http` launch profile and its Development bootstrap settings. Use a `NoBootstrap` profile or `--no-launch-profile` when initial bootstrap must be disabled. A `NoBootstrap` profile changes only `InitialBootstrapEnabled`; the catalog path and selected profiles remain configured for later manual application.

When Visual Studio starts `Agentstration.AppHost`, select the profile on that startup project. Its `https` profile passes the resolved catalog path, activation flag, and ordered initial profiles to the orchestrated Console; `https-NoBootstrap` forwards the same catalog selection with initial bootstrap disabled:

```powershell
dotnet run --project src/Agentstration.AppHost
dotnet run --project src/Agentstration.AppHost --launch-profile https-NoBootstrap
```

An invalid or duplicate profile name, a missing selected profile, malformed YAML, an unsupported `apiVersion`, an unknown `kind`, a missing required field, a missing referenced resource, or an absent referenced configuration value fails enabled startup explicitly.

`InitialBootstrapEnabled` controls startup application only. It does not disable profile loading itself: the PlatformAdmin-only Console and HTTP API can preview and apply explicit ordered profile lists independently of the startup flag.

## Extension boundary

### Staged initial startup

Enable phased startup when selected resources depend on extension announcements
or on contributions that do not exist yet:

```json
{
  "Agentstration": {
    "Bootstrap": {
      "Path": "./bootstrap/profiles",
      "InitialBootstrapEnabled": true,
      "PhasedStartupEnabled": true,
      "ExtensionDependencyTimeout": "00:10:00",
      "InitialProfiles": ["base"],
      "InitialApplications": [
        {
          "Profiles": ["tenant-resources"],
          "TargetLevel": "/tenants/acme"
        },
        {
          "Profiles": ["workspace-resources"],
          "TargetLevel": "/tenants/acme/workspaces/operations"
        }
      ]
    },
    "Aep": { "Enrollment": { "AutoEnrollmentLevel": "/instance" } }
  }
}
```

`InitialProfiles` target the instance. Each additional application selects profiles
of one scope and an explicit target resolved after topology creation. Scoped
applications use an existing global Platform administrator; the initial topology
may create it first. `InitialApplications` require `PhasedStartupEnabled`.

The sequence is storage, structural preparation, topology, enrollment policies,
required contributions, resolved references, dependent resources, then readiness.
Within each phase, file/profile order remains deterministic. Required extensions
are derived from selected public references and consumed bindings, not a global
list. Unrelated optional extensions do not block startup.

The listener accepts health and `POST /api/aep/enrollments/announce` during this
sequence. `/health/ready` and other operations return 503 until completion.
Application workers start afterward, preserving their original order. A required
extension may therefore arrive after its destination Workspace is created.

The dependency timeout is bounded between one second and thirty minutes and
defaults to ten minutes, allowing recovery of an interrupted automatic assignment
lease. Missing contributions report the profile/file, consumer and public identity;
disabled contributions and ambiguous selectors fail explicitly. Newly discovered
ordinary Tools keep their existing disabled default. An extension-declared bundle
may enable its own required Tools through the existing trusted bundle lifecycle.
Bootstrap never overrides an operator's disabled-resource decision just to satisfy
a reference.

Changing the catalog while startup waits fails the application. Completed topology,
assignments and resources survive retries; the handlers preserve existing state.
Staged startup repeats the idempotent readiness sequence under the initialization
lease even when the durable initialization version is already Ready. New optional
announcements remain eligible for policy-driven assignment after startup.

The default non-staged startup and `NoBootstrap` activation settings remain valid.
Manual preview stays read-only and reports unavailable dependencies without
enrolling or preparing them. Durable manual progress remains the separate workflow
tracked by issue #871.

### Public extension and contribution references

A `ModelProvider` can select its extension by public AEP identity instead of a
local registration name:

```yaml
definition:
  extension:
    id: Agentstration.Extensions.Foundry
    # instanceId: <stable-installation-uuid> # disambiguates several installations
  contributionId: microsoft-foundry
```

Use either the existing name-based reference or the public selector, not both.
Bootstrap resolves the selector to the enabled registration visible from the
consumer's scope. An enrollment record must be Available. Multiple visible
installations require an explicit `instanceId`; resolution never picks the first.

For an extension-owned resource, use a typed automatic profile binding:

```yaml
definition: # inside profile.yaml
  targetScope: workspace
  bindings:
    - name: search-tool
      targetKind: tool
      extensionTarget:
        extensionId: example.search
        kind: Tool
        contributionId: search
        # version: 1.0.0
```

The consumer uses the existing `{ binding: search-tool }` placeholder at its Tool
reference. Only bindings used by selected resources become extension requirements.
The same replacement works inside a `PackInstallation` binding's `target` while
Pack resources retain their normal ownership. Public contribution bindings cannot
also declare `defaultTarget` or receive a local-name override from the apply API.

A direct structured placeholder is also available:

```yaml
definition:
  type:
    extensionContribution:
      extensionId: example.services
      kind: ConnectedServiceType
      contributionId: example-api
      version: 1.0.0
```

Supported contribution kinds are `Tool`, `ConnectedServiceType`, `StorageType`
and an installed bundle's `DataSourceProfile`. Their binding target kinds are
`tool`, `connectedServiceType`, `storageType` and `dataSourceProfile` respectively.
`ExtensionRegistration` selects the registration itself. Service and Storage Type
placeholders replace the complete native type reference, including UID/version/
digest pins. Ordinary resources authored by Bootstrap keep their declared names
and are referenced normally by later manifests.

Preview is read-only. Missing, disabled, wrong-version or ambiguous contributions
invalidate it with a diagnostic. Resolved identities participate in its digest;
application rejects changed authoritative selections.

### Automatic SharedKeyFile enrollment

Set `Agentstration:Aep:Enrollment:AutoEnrollmentLevel` to `/instance`,
`/tenants/<name-or-id>`, or `/tenants/<name-or-id>/workspaces/<name-or-id>` to
assign verified SharedKeyFile installations without manual scope selection.
UUID-shaped segments select IDs; other segments select exact names. The target
must exist and be active, and a Workspace must belong to the selected Tenant.
Without a configured destination, assignment remains manual.

An instance profile can override the default for one extension or installation:

```yaml
apiVersion: agentstration.io/v1
kind: ExtensionEnrollmentPolicy
metadata:
  name: utilities-enrollment
definition:
  extensionId: Agentstration.Extensions.Utilities
  # instanceId: <stable-installation-uuid> # optional
  autoEnrollmentLevel: /tenants/dev/workspaces/default
```

An installation-specific policy takes precedence over an extension policy, then
the global default. Omit `autoEnrollmentLevel` from a policy to retain manual
assignment for its selector even when a global default exists. Conflicting
selectors are rejected. Policies are instance-owned and create-only; existing
assignments, disabled registrations and explicitly unenrolled installations are
preserved. `SharedKeyFileAllowed: false` cannot be overridden by a policy.
The effective policy is also applied to verified announcements arriving later.

Each extension must still be configured with its own key file, authority URL,
public endpoint and durable installation state. Aspire, Compose or the operator
owns that process and key lifecycle; Bootstrap never launches extensions.

Several installations of one public extension can use independent trusted files
under `Agentstration:Extensions:<extension-id>:Instances:<instance-id>:SharedKeyFile:Path`.
An installation-specific `EnrollmentMode` or `RegistrationName` can also override
that extension's defaults. Default registration names receive an installation
suffix when needed; an explicitly configured name that is already bound to another
installation fails instead of replacing its connection.

Each supported kind is implemented by an `IBootstrapResourceHandler`, which owns its business identity and calls the existing resource boundary. This avoids assuming that all resources use `kind + metadata.name` as their existence key.

Workspace profiles support direct editable `ModelProvider`, `RuntimeProfile`, `ModelProfile`, `Agent`, `Flow`, and `Entry` manifests. They also support `PackInstallation` with a bounded local ZIP source beneath the profile directory; those contained resources retain normal Pack ownership and immutability.

## Flow authoring requirements

Bootstrap Flow manifests use named `output` steps for every terminal result. Manifests must not use the removed, unreleased `failure` step. Declare at least one successful output and one error output; `completed` and `error` are the recommended names. An error output declares `outcome: error` and should provide a stable code, message, and an `outputMapping` when the failure payload must be preserved.

Every Agent and Tool step exposes `success` and `error`; both paths must be connected. Every Flow-call step must likewise connect each named error output declared by the called Flow, using that output name as the transition event. Put referenced Agents and child Flows earlier in lexical profile order than their consumers.

```yaml
steps:
  - { type: agent, name: answer, agent: { resourceId: support-agent } }
  - { type: output, name: completed, outcome: success, outputMapping: "${transition.output}" }
  - { type: output, name: error, outcome: error, code: SUPPORT_FAILED, message: The support Agent failed., outputMapping: "${transition.output}" }
transitions:
  - { id: answer-completed, fromStep: answer, event: success, toStep: completed }
  - { id: answer-error, fromStep: answer, event: error, toStep: error }
```

The empty Flow template already supplies both terminal outputs. In the visual Designer, adding an Agent, Tool, or Flow call automatically connects its conventional `error` event to the first error terminal when one exists. Bootstrap preview and Flow validation reject missing required error paths.
