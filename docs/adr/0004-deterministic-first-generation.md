# ADR 0004: Deterministic generation first, LLM as an optional fallback

* Status: Accepted
* Date: 2026-10-08

## Context

Most rule sentences in approval workflows follow a handful of shapes ("X greater than N should require R approval",
"reject when Y is one of ..."). Calling an LLM for each of them costs money and latency, is non-deterministic,
needs network access and secrets, and is hard to test.

## Decision

`CompositeRuleGenerator` always runs `PatternRuleGenerator` - a hand-written, offline grammar with alias-based field
mapping - first. Only if it cannot interpret the text, and only if `LLM_PROVIDER` is `openai` or `anthropic`, is
`LlmRuleGenerator` consulted. The default provider is `offline`, so the repository runs and its tests pass without
keys or network.

## Consequences

* Common sentences are handled instantly, for free and identically every time; unit tests cover the phrasings.
* The grammar is honest about its limits: unknown fields and unparseable text produce errors, never guesses.
* The LLM path still exists for unusual phrasing and is held to the same validation gate.
* Two generators must be maintained; the grammar is intentionally small and documented in
  `docs/ai-rule-generation.md`.
