# Direct agent definitions

Agentstration no longer has an Agent Type resource. An Agent directly declares its handler, instructions, model profile, tools, behaviors, middleware, context providers, and settings.

`AgentDefinitionCompiler` normalizes instructions, removes duplicate references, sorts resolved collections ordinally, and hashes a canonical representation with SHA-256. Identical inputs therefore produce identical resolved definitions and hashes.
