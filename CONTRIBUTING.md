# Contributing

Thank you for contributing to Agentstration. Open an issue before a substantial change, keep branches and pull requests focused, and extend the existing verticals before introducing new abstractions.

## Prerequisites and local start

Install Git and the .NET SDK selected by [`global.json`](global.json) (currently .NET 10.0.300 or a compatible feature band).

```powershell
git clone https://github.com/gbaudrit/agentstration.git
cd agentstration
$env:AI__Provider = "Deterministic"
dotnet run --project src/Agentstration.Web
```

The local Console is available at `http://localhost:5100` by default and requires no remote model or API key.

## Development checks

Use MSTest for behavior changes and keep the default test suite deterministic and offline. Before submitting a pull request, run:

```powershell
dotnet restore Agentstration.slnx
dotnet build Agentstration.slnx --configuration Release --no-restore
dotnet test Agentstration.slnx --configuration Release --no-build
./scripts/ci/verify-dotnet-format.ps1 -BaseRevision "origin/main"
```

The format script checks changed C# and Razor files in both solutions. The autonomous AEP subtree has a larger standalone solution; when it changes, also restore, build, and test `aep/Aep.slnx`.

If documentation changed, run:

```powershell
cd docs/site
npm ci
npm run build
```

Never commit secrets, personal data, generated data stores, or real document contents used for testing.

## Issues

Use the structured issue forms instead of a blank issue:

- **Bug report** for reproducible behavior that contradicts an existing expectation;
- **Feature request** for a new user-facing or product capability;
- **Technical task** for architecture, refactoring, performance, testing, security, technical debt, technical documentation, or developer-experience work.

Write issue titles and bodies in English, search for duplicates, and keep titles outcome-focused without type or priority prefixes. Maintainers assign `priority:P1`, `priority:P2`, or `priority:P3` during triage according to the criteria in the [GitHub governance guide](docs/contributing/github-governance.md). Agent-led triage remains pending until a maintainer confirms it. Feature requests and technical tasks require testable acceptance criteria; technical tasks also require an explicit validation plan. Report vulnerabilities privately through the Security Advisory link in the issue chooser.

## Branches and commits

Development follows trunk-based development with short-lived branches and pull requests into `main`. Useful prefixes include `feature/`, `fix/`, `docs/`, `refactor/`, and `codex/`. GitFlow and long-lived integration branches are not used.

Use [Conventional Commits](https://www.conventionalcommits.org/) with one of these primary types:

```text
feat fix refactor docs test build ci chore perf
```

Add a concise scope when it improves clarity, for example:

```text
feat(aep): add tool discovery
fix(runtime): handle cancelled flow
docs(architecture): document resource identity
ci(github): add pull request validation
```

Keep the subject imperative and describe breaking changes explicitly in the commit footer and pull request. No Node.js commit-linting tool is required.

## Pull requests

Open a focused pull request into `main`, complete the short template, and keep it current with the target branch. CI must pass, review conversations must be resolved, and the final merge should use squash merge. Delete the source branch after merge.

Use the same Conventional Commit form for the pull request title: `type(scope): description`, with an optional scope and optional `!` for a breaking change. The allowed types are `feat`, `fix`, `refactor`, `docs`, `test`, `build`, `ci`, `chore`, and `perf`.

When behavior changes, add or update tests in the same pull request. Update public contracts, OpenAPI-facing behavior, MCP descriptions, UI behavior, and documentation where applicable.

Keep the template headings in order: `Summary`, `Changes`, `Validation`, then `Breaking changes`. The summary should state the outcome, changes should be concise bullets, and validation should report only checks actually completed, including test totals and optional skips when known. UI changes should include desktop and mobile smoke testing against the local executable. Write `None.` under `Breaking changes` when the change is backward compatible; otherwise describe the impact and migration.

## Architecture and documentation

Inspect the complete affected path—transport, application, domain, infrastructure, and tests—and preserve the dependency rules in [`docs/architecture.md`](docs/architecture.md). Significant architectural choices require a new ADR under [`docs/decisions/`](docs/decisions/); do not rewrite an accepted ADR to hide a later decision.

Every change to a public concept, resource, API, configuration setting, or architecture decision must update the corresponding documentation. Keep the root README concise and put durable product documentation under `docs/`. See [Working on the documentation](docs/contributing/documentation.md) and [GitHub governance](docs/contributing/github-governance.md).

## Licensing contributions

Agentstration is licensed under the [Apache License 2.0](LICENSE). Unless you explicitly state otherwise, any contribution intentionally submitted for inclusion in Agentstration is provided under Apache-2.0 without additional terms or conditions, as described in section 5 of the license.

By submitting a contribution, you represent that you have the right to license it under those terms. Do not submit code, assets, documentation, model output, or other material whose license is incompatible with Apache-2.0. Existing third-party notices and attribution requirements must be preserved.
