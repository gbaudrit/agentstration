# Model profiles

A Model Profile expresses portable inference intent—such as generation, reasoning, and output settings—and references a [Model Provider](model-providers.md) and model. Agents store the profile resource identifier, not provider-specific connection details.

Profiles remain structurally distinct from Runtime Profiles: model intent and runtime behavior (sessions, tool invocation, or streaming) evolve through different resources.

An AWP Worker rejects an Agent execution that ends without final text, including after successful Tool calls. When the last model response reports `finishReason: length`, the failure code is `model_output_token_limit`; review the output token budget and reasoning settings. Other empty final responses fail with `model_empty_response`. The failure propagates to a direct Runtime Run or the Agent step of a Flow, where an explicit error transition may handle it. The worker retains the finish reason and content counts in diagnostics without logging model content. Intermediate Tool calls remain valid, and nonempty partial responses are preserved.
