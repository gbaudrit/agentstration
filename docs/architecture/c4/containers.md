# Deployable containers and processes

The canonical [interactive C4 L2 view](../interactive.mdx) distinguishes deployable hosts and persistent stores from the logical modules that run inside them. Management, Identity, Work, Flow, Runtime, Tools, Sources, Packs, and Triggers are module responsibilities; they are not independently deployed services.

Agentstration remains one modular codebase with three product hosts:

| C4 container | Executable | Responsibility |
| --- | --- | --- |
| Authoritative server and API | `Agentstration.Web` | Owns APIs, application services, hosted workers, storage composition, and the embedded Console used by the standalone default. |
| Operations Console host | `Agentstration.Console.Web` | Independently hostable Console UI and BFF. It consumes authoritative Management, Work, Flow, and Runtime APIs and owns no business store or worker. |
| Workplace host | `Agentstration.Workplace.Web` | Independently hostable end-user UI. It consumes Entry, Work, interaction, and realtime APIs and owns no business store or worker. |

The authoritative server composes separate control-plane, resource-planning, Identity, scheduling, Work, Flow, and Runtime persistence responsibilities. SQLite uses separate local database files by default; PostgreSQL is an optional shared infrastructure provider without merging module ownership. Bounded file-backed state covers local secrets, data-protection keys, Packs, Source snapshots and caches, and Work artifacts. Console supervision uses the public Work API rather than Work storage, and Workplace does not reference server, Runtime, provider, application, or storage implementations.

AEP extensions are autonomous processes outside the Agentstration system boundary. They enroll with the authoritative server, expose bounded contributions, and adapt provider-specific inference, source, or tool services. They are optional: the deterministic local path does not require them.

`Agentstration.AppHost` is the Aspire development orchestrator. It assembles hosts, optional extensions, and storage infrastructure for a development deployment, but it is not a product runtime container in the logical L2 view. Deployment topology belongs in dedicated deployment views.
