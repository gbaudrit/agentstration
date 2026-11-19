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

## Implementation follow-up — #1235

The Agent pilot now adopts the component through `ResourceDetailsTemplate`, with a sticky right rail, expanded groups and a shared header trigger for the mobile drawer. The native-hash limitation recorded above was corrected during integration: cancellable native hash pushes use public Blazor navigation before committing browser history. Focused browser probes verify native hash selection and guarded Back with retained form and YAML drafts. Default-context normalization also notifies Blazor, and preferred-culture redirects preserve the fragment. The decision remains Proposed; deferred UX scenarios #1234 and #1236 still require maintainer UX validation and the implementation merge.

The standard composition now reserves Overview, Definition/Configuration, conditional Access and final YAML. Families extend Definition with children and may supply additional primary sections before Access; the fragment IDs remain unchanged. The template owns standard panel order and retains visited section content, while family adapters retain permission checks, resource coordinates, editor state and commands. The legacy content/navigation contract remains available for existing adopters during migration.

Agent and its template share one `ResourceSectionState`, updated by accepted navigation. `ConsoleResourceDetailsTemplate` supplies Access from the saved resource identity, combines policy/definition leave protection and hosts the shared YAML editor. Family code retains YAML parsing and resource commands; Web components acquire no Console dependency.
