# Declarative bootstrap

Agentstration can create initial resources from YAML when the optional `Agentstration:Bootstrap:Path` setting points to a directory. There is no default path and no `Enabled` setting. An unconfigured path, a missing directory, or a directory containing no `.yaml` or `.yml` files has no effect.

Relative paths resolve from the Web host content root. Files use ordinal lexical filename order; use numeric prefixes when order matters. A file may contain one YAML resource or several YAML documents separated by `---`. Bootstrap runs once during every process startup after the stores and Identity migrations are initialized, but each handler creates only an absent resource.

## Platform administrator

The first supported kind uses the canonical resource envelope and the existing Identity bootstrap workflow:

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

Configure the path without putting the password in the YAML:

```json
{
  "Agentstration": {
    "Bootstrap": {
      "Path": "./bootstrap"
    }
  }
}
```

Supply the referenced value through any normal .NET configuration provider. For example, a container can mount declarations read-only and inject the secret separately:

```yaml
volumes:
  - ./bootstrap:/app/bootstrap:ro
environment:
  Agentstration__Bootstrap__Path: /app/bootstrap
  Agentstration__Bootstrap__Secrets__AdminPassword: ${ADMIN_PASSWORD}
```

The password must satisfy the existing ASP.NET Core Identity policy. Identity validates and hashes it; Agentstration never adds it to the canonical resource or logs it. If the declared account already exists and its Principal is already a Platform administrator, bootstrap skips it without resolving or checking the password. Password changes therefore survive restarts. An existing account without the declared grant is treated as an inconsistent collision and fails startup rather than being silently changed.

## Visual Studio and local Development

The opt-in `BootstrapDevelopment` launch profile activates the versioned bundle under `deploy/bootstrap/profiles/development`. It is available from Visual Studio or the command line:

```powershell
dotnet run --project src/Agentstration.Web --launch-profile BootstrapDevelopment
```

This Development-only profile creates the public local account `admin / admin` on a fresh instance. The normal `http` profile does not configure a bootstrap path and therefore creates no default account. The known credential is a development fixture, not a secret: never use this profile for an exposed or production instance. Development relaxes the local Identity password policy to accept it; every other environment retains the strong password policy. Standard .NET configuration can override the password when needed.

Malformed YAML, an unsupported `apiVersion`, an unknown `kind`, a missing required field, or an absent referenced configuration value fails startup explicitly. `PlatformAdministrator` creation also fails if another account has already initialized the instance because bootstrap is not an administrative synchronization mechanism.

## Extension boundary

Each supported kind is implemented by an `IBootstrapResourceHandler`, which owns its business identity and calls the existing resource service. This avoids assuming that all resources use `kind + metadata.name` as their existence key.

`PackInstallation` is not supported yet. The current Pack service installs validated ZIP archives, while local-directory and registry source resolution are not established Pack contracts. A future Pack-owned handler can add that resolution and delegate installation to the existing Pack service without changing the YAML loader.
