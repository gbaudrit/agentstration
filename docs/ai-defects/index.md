# AI Defect Records

AI Defect Records (AIDRs) capture durable, evidence-based learning from defects introduced by AI-generated or AI-assisted changes. They complement Architecture Decision Records: an ADR explains a significant architectural choice, while an AIDR explains what drifted, why the repository safeguards did not catch it, how it was corrected, and how recurrence is prevented.

## When a record is required

Create an AIDR when an AI agent resolves an issue labeled `ai-defect`. The label is a provenance and quality signal, not an issue type or severity:

- keep exactly one issue type label such as `bug` or `technical-task`;
- assign priority independently from observed impact;
- apply `ai-defect` to the corrective pull request so repository validation can require the record.

Use the GitHub issue number as the stable record identifier and name the file `NNNN-short-title.md`, zero-padding numbers below 1000. One issue produces one record; update the same record if the corrective pull request evolves.

## Required content

Start from [the record template](template.md). A complete record must:

- link the labeled issue, the change that introduced the defect when known, the corrective pull request, and any affected ADR;
- describe the violated expectation and observable evidence;
- identify the faulty assumptions, missed repository context, and validation or review gaps;
- explain the correction and the durable prevention added to instructions, tests, automation, or architecture;
- report validation that was actually performed.

Keep the analysis blameless and factual. Do not include private chain-of-thought, hidden reasoning, credentials, personal data, full prompts, or unsupported claims about a model. If the introducing change or cause cannot be established, state that explicitly.

## Status

Use one of these values:

- **Open**: analysis or correction is incomplete;
- **Resolved**: the defect is corrected and validation is complete;
- **Prevented**: the defect is corrected and a durable recurrence safeguard is in place;
- **Accepted**: the maintainer explicitly accepts the residual risk.

A record remains in Git after resolution so future agents and maintainers can recognize the same failure pattern.
