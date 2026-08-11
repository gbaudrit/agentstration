# Configuration reference

The verified startup settings and local defaults are documented in [Getting started: configuration](../getting-started/configuration.md).

Configuration follows ASP.NET Core providers, so environment variables use `__` for nesting. Persisted Model Provider, Model Profile, and Runtime Profile resources are Management data rather than host configuration. Prefer those resources for governed execution settings.

Do not commit API keys. HTTP payload capture is disabled by default, and sensitive prompts, documents, credentials, and agent output must not be logged by default.
