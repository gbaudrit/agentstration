# ADR-0238: Resource-local sections use URL fragments

## Status

Proposed — component increment for #1233; resource-detail layout adoption remains pending.

## Context

The maintainer selected a configurable two-level resource-local menu and explicitly chose fragments as its sole section-selection model. Legacy `view`/`tab` section links require no compatibility mapping. ADR-0187 makes the resource's administrative context URL-authoritative; section changes must retain that context and resource ownership.

## Decision

- New `ResourceNavigation` consumers use stable `#primary` or `#primary/child` paths with lowercase ASCII kebab-case IDs. Labels are localized independently.
- Resource identity remains in its canonical route and namespace/owner/context query parameters. A section transition carries no resource command or context change.
- The caller supplies visible/enabled entries and the authorized default; invalid or unavailable destinations resolve to an allowed entry without exposing hidden labels. Normalization replaces the current history entry, while explicit selection participates in Back/Forward.
- Navigation uses the public Blazor navigation API and accepted location changes, with public browser entry observation where supported, plus existing modal/focus infrastructure. Direct native hash mutation combined with the current dirty-navigation guard remains an integration limitation to resolve on the pilot. It does not persist or reload resource drafts.
- Existing tab consumers remain unchanged until explicit family adoption. No universal resource schema, loader or business model is introduced.

## Consequences

Layouts can compose shared local navigation separately from headers, content and actions. Adopted screens update their link producers and keep models/editors alive across local navigation. Incoming obsolete section selectors do not determine the selected view. The component increment alone does not deliver the Agent layout/pilot or satisfy the deferred scenario #1234 gate.

This decision concerns section selection and does not supersede ADR-0187's administrative-context/authorization rules.
