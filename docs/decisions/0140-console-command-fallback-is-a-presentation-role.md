# ADR-0140 — Console command fallback is a presentation role

## Status

Accepted

## Context

Console already searches authorized pages, commands, and resources. When none match, it may offer Console-exposed Entries as explicit continuations without changing Entry ownership, executing from typed input alone, or duplicating Entry visibility and readiness rules.

## Decision

`EntryExposure.Console.Role` is the minimal versioned presentation policy for this affordance. `Standard` is the compatible default and `Fallback` designates a candidate for command/search fallback. A non-Console Entry cannot declare the fallback role.

The Console fallback provider consumes the canonical Console Entry discovery projection from ADR-0114. Every discovered fallback Entry that is executable becomes an explicit choice. Missing, unauthorized, disabled, unavailable, and malformed Entries remain absent; an empty candidate set preserves the existing empty-search result. The provider preserves discovery order but does not define a business priority between multiple candidates; that policy is deferred to #543.

Existing authorized page, command, and resource matches remain authoritative. Typing never invokes an Entry. Explicit selection navigates with the Entry owner Workspace, namespace, name, and exact initial query to the generic Entry interaction route; that route does not derive execution scope from the Workspace currently administered in Console.

## Consequences

The role is reusable by any Entry and contains no official-Assistant semantics. Console Home placement remains independent. Multiple fallback Entries remain useful before a later ordering or priority policy is introduced. A later interaction surface can consume the stable navigation contract without introducing a command-palette execution path.
