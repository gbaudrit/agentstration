# Release notes instructions

These instructions apply to files under `docs/releases/`.

## Purpose

Release notes are the authoritative technical summary of a published Agentstration version. They are consumed by maintainers, users, the GitHub Release workflow, and downstream communication tooling. Keep them factual, reviewable, and tied to the exact released version.

## Source of truth

When preparing `docs/releases/<version>.md`:

- Use the exact product version from the repository and the intended Git tag `v<version>`.
- Compare against the previous published release tag when one exists.
- Inspect merged pull requests and relevant commits between the previous release and the target release to understand the delivered behavior.
- Use the tagged or release-candidate source state as the authority for what is actually present.
- Consult current documentation when needed to confirm supported workflows, terminology, installation instructions, and known limitations.
- Do not describe unreleased `main` behavior as part of the target release.
- Do not infer features from issue titles, roadmap items, or planned work unless the implementation is present in the target version.

## Content rules

Write release notes in English.

Prefer a concise technical narrative over an exhaustive commit log. Emphasize changes that materially affect users, contributors, deployment, architecture, compatibility, or supported workflows.

Never invent:

- features or integrations;
- maturity or production-readiness claims;
- performance numbers;
- compatibility guarantees;
- migration behavior;
- roadmap commitments.

For prereleases, clearly preserve the prerelease status and relevant limitations.

Document breaking changes, upgrade requirements, schema-reset requirements, changed defaults, renamed resources, and incompatible behavior when they are actually present. If no migration path is guaranteed, say so explicitly rather than implying compatibility.

## Canonical structure

Use this structure unless the release has a concrete reason to require an additional section:

```markdown
# Agentstration <version>

<Short release introduction and prerelease status.>

## Highlights

- <Meaningful delivered capability or change.>
- <Meaningful delivered capability or change.>

## Release artifacts

<Published applications, containers, packages, checksums, supported platforms, and concise launch/install guidance relevant to this version.>

## Important alpha limitations

- <Known limitation relevant to this release.>
- <Known limitation relevant to this release.>
```

Keep `## Highlights` focused on a small number of the most meaningful changes. Do not turn it into a list of every merged pull request.

Additional sections such as `## Breaking changes`, `## Upgrade notes`, or `## Security` are allowed when they carry material release-specific information.

## Release artifacts and commands

Verify artifact names, container tags, runtime requirements, commands, paths, ports, environment variables, and installation examples against the target version before including them.

Do not copy commands from an older release without checking that they still apply.

When the GitHub release workflow publishes an artifact or image, use the naming produced by the workflow rather than inventing a friendlier alias.

## Validation before handoff

Before handing off release notes:

- Confirm the target version matches the intended `v<version>` tag.
- Confirm the file path is exactly `docs/releases/<version>.md`.
- Confirm every highlight is implemented in the target release.
- Confirm release artifact names and commands match the current release workflow.
- Confirm prerelease status and limitations are accurate.
- Confirm links resolve to current repository documentation where applicable.
- Review the diff from the previous release and ensure no material user-facing or operational breaking change was omitted.

Do not claim validation that was not performed.

## Communication boundary

These release notes are technical source material, not marketing copy.

Do not optimize them for LinkedIn, newsletters, social media, or promotional tone. The separate `gbaudrit/agentstration-communication` repository is responsible for turning release facts, source changes, and product screenshots into editorial communication.
