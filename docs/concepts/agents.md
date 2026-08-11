# Agents

An Agent is a declarative Management resource describing a named executable participant. It references an [Agent Type](agent-types.md) and a [Model Profile](model-profiles.md), and may add instructions and tool references.

Functional changes increment the Agent generation. Immutable revisions capture deployable snapshots. Runtime `AIAgent` objects are reconstructed and are never the source of truth.

See the [Agent resource reference](../reference/resources/agents.md).
