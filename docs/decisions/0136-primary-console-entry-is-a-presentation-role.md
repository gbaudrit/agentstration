# ADR-0136 — The primary Console Entry is a presentation role

## Status

Accepted

## Context

Console already searches authorized pages, commands, and resources. When none match, it may offer one Console-exposed Entry as an explicit continuation without changing Entry ownership, executing from typed input alone, or duplicating Entry visibility and readiness rules.

## Decision

`EntryExposure.Console.Role` is the minimal versioned presentation policy for this affordance. `Standard` is the compatible default and `Primary` designates a candidate for command/search fallback. A non-Console Entry cannot declare the primary role.

The Console fallback provider consumes the canonical Console Entry discovery projection from ADR-0114. It offers a fallback only when exactly one discovered primary Entry is executable. Missing, unauthorized, disabled, unavailable, malformed, and ambiguous configurations fail closed to the existing empty-search result.

Existing authorized page, command, and resource matches remain authoritative. Typing never invokes an Entry. Explicit selection navigates with the Entry owner Workspace, namespace, name, and exact initial query to the generic Entry interaction route; that route does not derive execution scope from the Workspace currently administered in Console.

## Consequences

The role is reusable by any Entry and contains no official-Assistant semantics. Console Home placement remains independent. A later interaction surface can consume the stable navigation contract without introducing a command-palette execution path.
