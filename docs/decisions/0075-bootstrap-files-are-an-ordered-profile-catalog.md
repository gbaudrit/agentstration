# ADR-0075: Bootstrap files are an ordered profile catalog

Status: Accepted — 2026-08-29

## Context

A single bootstrap directory is sufficient for one fixed initial topology, but it encourages duplication when an installation needs a reusable base plus Development, demonstration, or deployment-specific resources. Disabling startup by erasing the path also makes the same configured files unavailable to a future administrative UI that should validate and apply a profile manually.

Startup activation, profile selection, and catalog location are separate concerns. Their configuration must remain explicit outside Development, deterministic, safe against arbitrary path traversal, and compatible with the existing idempotent resource handlers.

## Decision

`Agentstration:Bootstrap:Path` identifies a catalog root. Each selectable profile is one immediate child directory. `Agentstration:Bootstrap:InitialProfiles` is an ordered list of profile names, and `Agentstration:Bootstrap:InitialBootstrapEnabled` explicitly controls whether that list is applied at process startup. The activation flag defaults to `false`.

Selected profiles are applied in configuration order. Files within each profile retain ordinal lexical ordering, and YAML documents retain document order. A profile name must be one valid directory name: rooted paths, directory separators, `.` and `..` are rejected. Duplicate and missing selected profiles are configuration errors when initial bootstrap is enabled. An empty selected profile is a valid no-op.

The application service also accepts an explicit ordered profile list independently of the startup flag. This preserves one loading and dispatch boundary for a future PlatformAdmin-only validation and manual-application surface without exposing that surface in this increment.

The standard Web and AppHost Development profiles configure the catalog root, select `development`, and enable initial bootstrap. Their `NoBootstrap` variants keep the same path and selection but set `InitialBootstrapEnabled` to `false`. AppHost resolves the catalog root and forwards all three values to the Console resource.

## Consequences

- Installations can compose profiles such as `base`, `development`, and `demo` without copying shared declarations.
- Changing the activation flag never hides the catalog or destroys the configured selection.
- Dependencies across profiles are visible in configuration rather than hidden in profile metadata; no dependency graph or recursive inclusion mechanism is introduced.
- Existing direct paths to a leaf manifest directory must be migrated to the catalog root plus one selected profile.
- Idempotency, conflict handling, secret resolution, and the non-reconciliation behavior from ADR-0073 remain unchanged.
- A future UI can list, validate, preview, and apply catalog profiles through a secured application boundary; audit history and HTTP/UI contracts remain future work.
