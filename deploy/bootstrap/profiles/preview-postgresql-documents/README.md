# Preview PostgreSQL document profile

This optional Instance profile creates a Schema, local Vault, Secret, Connected
Service and document Storage after the PostgreSQL AEP extension is enrolled.
It does not create an administrator, Tenant or Workspace, enable initial bootstrap
or provision PostgreSQL. Use the existing profile application flow and choose
the target explicitly; automatic PR profile selection remains #1419.

The preview recipe supplies `Agentstration:Bootstrap:Parameters:PostgreSqlDocumentsHost`,
`PostgreSqlDocumentsPort`, `PostgreSqlDocumentsDatabase` and `PostgreSqlDocumentsUsername`
under that same Parameters section. The server startup wrapper reads the target's
0600 password file locally and exports
`Agentstration__Bootstrap__Secrets__PostgreSqlDocumentsPassword` before starting
ASP.NET Core. `21-password.yaml` resolves that configuration key; the profile
contains no password and has no disposable login credentials.

Preserving updates reuse the local password and document volume. A full reset
erases the preview's local resources and credential state. With `externalDB`,
the external database is never deleted. Schema provisioning is enabled only
for the managed internal preview database; an external database requires its
normal provisioning policy or an explicit operator setting.
