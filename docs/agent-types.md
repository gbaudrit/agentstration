# Agent types

An agent type defines one base behavior and composes cross-cutting capabilities through tools, behaviors, middleware, and context providers. Agents reference exactly one type version.

`AgentDefinitionCompiler` applies `AgentTypePolicy`, normalizes line endings, merges instructions, validates model/tool overrides, removes duplicate tools, sorts all resolved collections ordinally, and hashes a canonical JSON representation with SHA-256. Identical inputs therefore produce identical prompts, ordering, output, and hashes.

Supported handler identifiers are `prompt-agent`, `router-agent`, `remote-agent`, and `custom-agent`. The standalone runtime currently materializes `prompt-agent`; the remaining identifiers require registered factories.
