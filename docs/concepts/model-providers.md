# Model providers

A Model Provider describes how Agentstration reaches an out-of-process AEP extension contribution and what that provider can do. Provider declarations are durable Management resources with connectivity testing, dynamic model discovery, ETag concurrency, usage visibility, and deletion protection.

Ollama is the first implemented contribution and runs in `Agentstration.Extensions.Ollama`; the persisted endpoint is the extension URL rather than Ollama's native URL. Cloud services are optional. Provider endpoints and credentials do not belong in portable Agent definitions.
