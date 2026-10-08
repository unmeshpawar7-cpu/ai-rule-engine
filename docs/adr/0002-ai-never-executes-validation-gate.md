# ADR 0002: AI output is a proposal behind a validation gate, never executed

* Status: Accepted
* Date: 2026-10-08

## Context

Rule generation from natural language is useful but unreliable: an LLM can invent fields, misread thresholds or
pick the wrong action, and even a deterministic grammar can misinterpret a sentence. Rules decide approvals and
rejections of real documents, so a wrong rule has business impact.

## Decision

* Generators only produce a *proposal*. The proposal goes through `RuleValidator`, the same gate as manual rules.
* A valid proposal is stored as an inactive rule whose single version is `Draft` with `Source = AiGenerated`, the
  prompt/grammar version and the generation metadata. An invalid one is returned with errors and nothing is stored.
* Publishing (`RuleAdmin` only, with passing tests when configured) and activation are separate, explicit,
  audited human actions. No code path publishes, activates or executes a generated rule.

## Consequences

* The worst case of a bad generation is a draft that fails validation or that a reviewer discards.
* Generated rules are traceable: who generated them, with which generator, model and prompt version, and which
  assumptions were made.
* Adoption requires a review step; this is intentional and matches four-eyes expectations for approval policies.
