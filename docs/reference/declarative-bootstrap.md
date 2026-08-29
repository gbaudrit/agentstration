# Declarative bootstrap

Agentstration treats `Agentstration:Bootstrap:Path` as the root of a profile catalog. Each immediate child directory is one independently selectable profile. Startup applies the ordered `Agentstration:Bootstrap:InitialProfiles` list only when `Agentstration:Bootstrap:InitialBootstrapEnabled` is `true`. Initial bootstrap is disabled by default.

Profiles use configuration order; files inside each profile use ordinal lexical filename order. Use profile order for dependencies across profiles and numeric filename prefixes for dependencies inside one profile. A file may contain one YAML resource or several YAML documents separated by `---`. Bootstrap runs during every enabled process startup after persistence initialization, but handlers only create absent resources and never reconcile existing ones.

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

`InitialBootstrapEnabled` controls startup application only. It does not disable profile loading itself: the same application service can apply an explicit ordered profile list independently of the startup flag. This is the boundary intended for a future PlatformAdmin-only validation and application UI; no such HTTP or UI surface is exposed yet.

## Extension boundary

Each supported kind is implemented by an `IBootstrapResourceHandler`, which owns its business identity and calls the existing resource boundary. This avoids assuming that all resources use `kind + metadata.name` as their existence key.

`PackInstallation` is not supported yet. A future Pack-owned handler can add source resolution and delegate installation to the existing Pack service without changing the YAML loader.
