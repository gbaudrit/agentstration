# AI Defect Causal Analysis and Resolution

Causal Analysis and Resolution (CAR) is an established software approach for identifying the causes of defects, resolving them, and preventing recurrence. Agentstration applies that practice to defects introduced by AI-generated or AI-assisted changes through an **AI Defect CAR**.

An AI Defect CAR complements Architecture Decision Records: an ADR explains a significant architectural choice, while a CAR explains what drifted, why repository safeguards did not catch it, how it was corrected, and how recurrence is prevented.

## When a CAR is required

Create a CAR when an AI agent resolves an issue labeled `ai-defect`. The label is a provenance and quality signal, not an issue type or severity:

- keep exactly one issue type label such as `bug` or `technical-task`;
- assign priority independently from observed impact;
- apply `ai-defect` to the corrective pull request so repository validation can require the CAR.

Use the GitHub issue number as the stable CAR identifier and name the file `NNNN-short-title.md`, zero-padding numbers below 1000. Refer to it as `CAR-NNNN`. One issue produces one CAR; update the same document if the corrective pull request evolves.

## Required content

Start from [the CAR template](template.md). A complete CAR must:

- link the labeled issue, the change that introduced the defect when known, the corrective pull request, and any affected ADR;
- describe the violated expectation, detection stage, and observable evidence;
- identify the faulty assumptions, missed repository context, and validation or review gaps;
- explain the resolution and the durable prevention added to instructions, tests, automation, or architecture;
- report validation that was actually performed.

Keep the analysis blameless and factual. Do not include private chain-of-thought, hidden reasoning, credentials, personal data, full prompts, or unsupported claims about a model. If the introducing change or cause cannot be established, state that explicitly.

## Status

Use one of these values:

- **Open**: causal analysis or resolution is incomplete;
- **Resolved**: the defect is corrected and validation is complete;
- **Prevented**: the defect is corrected and a durable recurrence safeguard is in place;
- **Accepted**: the maintainer explicitly accepts the residual risk.

A CAR remains in Git after resolution so future agents and maintainers can recognize the same failure pattern.
