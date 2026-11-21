# Bootstrap resource details and model correction

Implementation smoke captured on 2026-11-21 using the Release standalone Web
executable, the real SharedKeyFile-enrolled Ollama extension and an offline,
controllable upstream fixture. All names and accounts are disposable fixtures.

- [Desktop, French: filtered resource list](desktop-resources-fr.png)
- [Desktop, French: inline provider/model correction](desktop-correction-fr.png)
- [Mobile, English: light theme](mobile-en-light.png)
- [Mobile, English: dark theme](mobile-en-dark.png)

The selected profiles declare topology, enrollment, providers, access policies,
one Model Profile, one Agent and 512 Parameters. The expected model is absent.
The browser checks that only declared families appear, that rows are paged at 25,
and that page changes and search retain exact counts.

The administrator consults Agents during failed preparation, changes the Console
scope to Instance and returns to progress. Provider choices still evaluate the
original Tenant owner: an ungranted Instance provider is excluded. While the
upstream is unavailable, the form remains usable from cached inventory. Applying
an authorized replacement provider/model records the selection and starts a new
attempt without restarting the process. Restoring the upstream completes the
Model Profile, Agent generation and 512 Parameters, then opens the Console.

A process restart with the same data retains the replacement model and skips the
already-created Model Profile, Agent and 512 Parameters.

Desktop and mobile browser runs completed without JavaScript errors or page-wide
horizontal overflow. The mobile run checks both themes and applies an available
replacement model. These are implementation smoke checks, not the deferred
reusable journey: #1251 remains blocked by maintainer UX validation and merge.
