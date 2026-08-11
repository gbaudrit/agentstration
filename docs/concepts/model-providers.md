# Model providers

A Model Provider describes how Agentstration reaches a model service and what that provider can do. Provider declarations are durable Management resources with connectivity testing, dynamic model discovery, ETag concurrency, usage visibility, and deletion protection.

Ollama is the currently implemented mutable provider type. Cloud services are optional. Provider endpoints and credentials do not belong in portable Agent definitions.
