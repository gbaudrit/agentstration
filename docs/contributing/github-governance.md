# GitHub governance

Agentstration uses trunk-based development: short-lived branches are merged through pull requests into `main`, normally with squash merge. GitFlow and long-lived integration branches are intentionally not part of the workflow.

## Versioned repository configuration

The repository keeps reviewable governance files in Git:

- `.github/workflows/ci.yml` classifies changed paths, restores, builds, tests, verifies formatting on changed .NET files, checks Windows host lifecycle behavior, and builds the container on Linux;
- `.github/workflows/pull-request-metadata.yml` validates pull request titles and descriptions whenever their content or source revision changes;
- `.github/workflows/codeql.yml` scans C# on pull requests, `main`, and a weekly schedule;
- `.github/workflows/dependency-review.yml` blocks pull requests that introduce known vulnerabilities of moderate severity or higher;
- `.github/workflows/release.yml` validates version tags, rebuilds and retests the product, packages the server and Workplace, and creates GitHub prereleases;
- `.github/dependabot.yml` checks the root and autonomous AEP NuGet manifests plus GitHub Actions each week;
- `.github/CODEOWNERS`, the pull request template, and issue forms provide lightweight contribution ownership and prompts;
- `.github/rulesets/main.json` is the reproducible source definition for `main` protection.

Documentation validation and publication remain independent in `documentation.yml` and `publish-documentation.yml`. Publication is manual and has the only workflow permissions needed for GitHub Pages.

## Issue intake and triage

Blank issues are disabled. Contributors choose one of the structured forms under `.github/ISSUE_TEMPLATE/`:

| Form | Use when | Default label |
|---|---|---|
| Bug report | Existing behavior contradicts an expectation and can be reproduced | `bug` |
| Feature request | The primary outcome is a new user-facing or product capability | `enhancement` |
| Technical task | The primary motivation is architecture, refactoring, performance, testing, security, technical debt, technical documentation, or developer experience | `enhancement` |

Issue titles and bodies use English. Titles describe the outcome without `[Bug]`, `[Feature]`, or priority prefixes. The forms collect the affected area and classification as structured fields; maintainers may translate them into more specific labels during triage.

Priority is a maintainer decision. Assign it through repository labels only after impact, urgency, dependencies, and scope have been reviewed. Do not encode priority in the title.

GitHub API clients do not execute issue forms. Automation and coding agents must therefore reproduce the selected form's required sections, labels, and ordering explicitly. Repository-wide instructions for coding agents are maintained in `AGENTS.md`.

## Checks and pull requests

Pull requests to `main` run these workflows:

| Check | Purpose | Required by the prepared ruleset |
|---|---|---|
| `pull-request-metadata` | Require a Conventional Commit title and the ordered Summary, Changes, Validation, and Breaking changes sections | Yes |
| `build-and-test` | Restore, Release build, tests, and changed-file formatting for Agentstration and the complete AEP solution | Yes |
| `container` | Validate the production Docker build after code validation | No |
| `CodeQL / C#` | Static security analysis | No; review after initial successful scans |
| `dependency-review` | Reject vulnerable dependency additions | No; recommended after repository feature availability is confirmed |

The stable required-check contexts are `build-and-test` and `pull-request-metadata`. Do not rename either job without updating the ruleset and reconfiguring GitHub.

The CI workflow keeps `build-and-test` present for documentation-only pull requests but short-circuits its expensive .NET steps when no product or build input changed. Complete AEP validation runs only when the autonomous subtree or its shared CI inputs change. Container validation is path-aware, runs independently from the required .NET check, and uses the GitHub Actions BuildKit cache. Linux and Windows .NET jobs cache the NuGet global-packages folder while still running restore and NuGet audit on every relevant revision. The C# CodeQL workflow also ignores documentation-only pull requests and pushes; its scheduled and manual scans remain complete.

Format verification is deliberately incremental: the current codebase has pre-existing `dotnet format` debt, so CI verifies every changed C# or Razor file without forcing an unrelated repository-wide rewrite. A separate cleanup can establish a clean full-repository baseline later.

Pull request titles use the same Conventional Commit form as commits: `type(scope): description`, with optional scope and optional `!` for a breaking change. Descriptions use the repository template in this order: `Summary`, `Changes`, `Validation`, then `Breaking changes`. Keep the summary outcome-focused and the changes concise. Validation must report only checks actually completed, with test totals and optional skips when known; UI changes also report desktop and mobile smoke testing against the local executable. Backward-compatible changes state `None.` under `Breaking changes`, while incompatible changes describe both impact and migration. The `pull-request-metadata` check enforces the title, exact section order, non-empty content, and bullet lists for Changes and Validation.

The ruleset requires pull requests, resolved review conversations, linear history, squash merges, an up-to-date branch, and the `build-and-test` and `pull-request-metadata` checks. It prevents branch deletion and force pushes. Because the project currently has one principal maintainer, it requests no mandatory approval and does not require a CODEOWNER approval; reviews remain strongly encouraged.

## Bootstrap and apply the ruleset

GitHub stores active rulesets remotely. Committing `.github/rulesets/main.json` documents the intended state but does not protect `main` by itself.

Rulesets and protected branches are not available for a private repository owned by a GitHub Free personal account. While this repository remains private on that plan, the definition stays prepared but cannot be applied. Application becomes available after making the repository public, upgrading the owner to GitHub Pro, or moving an organization-owned repository to an eligible Team/Enterprise plan. `CODEOWNERS` likewise remains versioned but cannot be enforced as a private-repository code-owner rule on GitHub Free.

Bootstrap in this order because GitHub may not accept or expose a status check until it has run:

1. Merge or push `.github/workflows/ci.yml`.
2. Let CI complete successfully at least once.
3. Verify that the `build-and-test` check exists.
4. Preview the ruleset application.
5. Apply the ruleset and confirm that `build-and-test` is required.

```powershell
./scripts/github/apply-main-ruleset.ps1 -DryRun
./scripts/github/apply-main-ruleset.ps1
```

To target a repository explicitly:

```powershell
./scripts/github/apply-main-ruleset.ps1 -Repository "gbaudrit/agentstration"
```

The script requires an authenticated GitHub CLI (`gh auth login`) whose token can administer repository rules. It discovers the current repository when `-Repository` is omitted, creates `main-protection` when absent, and updates the existing branch ruleset with that name. It never reads or stores a token in the repository.

`-DryRun` validates and prints the local definition without calling the Rulesets API, so it also works while the private repository is on GitHub Free. A real application reports the plan limitation explicitly when GitHub returns it.

## Private-repository feature availability

The core `build-and-test`, container, documentation, and Dependabot workflows remain usable for a private repository on GitHub Free. GitHub-hosted CodeQL code scanning and Dependency Review are different: for private repositories they require an organization with GitHub Code Security or GitHub Advanced Security; GitHub Pro alone does not enable them.

Consequently, the CodeQL and Dependency Review jobs run automatically for public repositories and are skipped while this repository is private. If the repository later moves to an eligible organization and Code Security is enabled, create the repository Actions variable `ENABLE_GITHUB_CODE_SECURITY=true` to activate both jobs without changing the workflows.

## GitHub settings

The following settings are remote and must be checked in the repository UI:

- enable squash merging and automatic deletion of head branches;
- disable merge commits; disable rebase merging unless maintainers deliberately want a second linear merge path;
- enable the dependency graph, Dependabot alerts, and Dependabot security updates;
- enable secret scanning, push protection, and private vulnerability reporting when available for the repository and plan;
- enable code scanning with this repository's advanced CodeQL workflow, not a duplicate default setup;
- confirm Actions workflow permissions remain read-only by default.

After CodeQL and Dependency Review have completed successfully and their repository features are available, maintainers may add them as required checks. Keep that decision separate from the initial bootstrap so a plan limitation or first-run setup cannot deadlock `main`.

## Product releases

The root `Directory.Build.props` is the product-version source of truth. A release requires a matching notes file under `docs/releases/` and a tag named `v<version>` on a commit already contained in `main`. The release workflow rejects mismatched versions and non-main commits before building artifacts.

Docker Hub publication requires an existing `agentstration/agentstration` repository and these GitHub Actions repository secrets:

- `DOCKERHUB_USERNAME`: the Docker Hub account allowed to push the repository;
- `DOCKERHUB_TOKEN`: a scoped Docker Hub access token with write permission. Do not store an account password.

For example, after the release change has merged and all required checks have passed:

```powershell
git switch main
git pull --ff-only
git tag -a v0.1.0-alpha.1 -m "Agentstration 0.1.0-alpha.1"
git push origin v0.1.0-alpha.1
```

GitHub Actions then repeats restore, Release build, and tests; publishes framework-dependent server and Workplace ZIPs plus `SHA256SUMS`; pushes the server/Console image to Docker Hub for `linux/amd64` and `linux/arm64`; records its manifest digest; and creates a GitHub prerelease using the version-specific notes. Alpha releases publish the immutable version tag and the moving `alpha` channel, never `latest`. Do not move or reuse a published tag. Correct a failed release through a reviewed commit and a new prerelease identifier.
