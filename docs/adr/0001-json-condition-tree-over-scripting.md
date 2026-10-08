# ADR 0001: JSON condition tree instead of a scripting language

* Status: Accepted
* Date: 2026-10-08

## Context

Business users and an AI generator both author rules. Rules must be validated before storage, explained after
execution (which clause matched, with which values), diffed between versions, and executed safely in a multi-tenant
API. Candidate representations: an embedded scripting language (C# scripting, JavaScript, Lua), an expression
language (e.g. Dynamic LINQ strings), or a JSON tree with a closed operator set.

## Decision

Represent conditions as a JSON tree (`all` / `any` / `not` / leaf with `field`, `operator`, `value`) and actions as
a JSON list of typed actions. The operator set is closed and typed against the rule set's fact schema.

## Consequences

* Every rule can be statically validated: unknown fields, operator/type mismatches, bad values, unsafe regexes,
  depth/size limits. This is also what makes LLM output checkable.
* The evaluator is ~250 lines of pure code with no sandboxing concerns; evaluation cannot loop, allocate
  unboundedly or perform I/O. Regexes run with a timeout.
* Traces are natural: each node reports expected vs actual.
* Conflict detection over conjunctive conditions is possible because the structure is analysable.
* Expressiveness is limited by design: no arithmetic across fields (`amount / quantity > 100`), no custom
  functions. Adding them means adding typed operators, not code execution.
* JSON is verbose for humans; the natural-language generator and the Swagger examples compensate.
