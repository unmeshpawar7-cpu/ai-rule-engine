# Rule DSL

Rules are data, not code: a JSON condition tree plus a JSON action list, validated against the rule set's fact
schema before they are stored. See [ADR 0001](adr/0001-json-condition-tree-over-scripting.md) for why.

## Fact schema

Each rule set declares the facts it understands. Conditions may only reference declared fields, and execution
input is validated against the schema before evaluation.

```json
{
  "fields": [
    { "path": "document.quantity", "type": "Number", "required": true, "aliases": ["qty", "units"] },
    { "path": "supplier.riskRating", "type": "String", "aliases": ["supplier risk"] }
  ]
}
```

| Property | Meaning |
|---|---|
| `path` | Dotted path into the facts document (`document.quantity` reads `{ "document": { "quantity": 25 } }`). Lookup is case-insensitive per segment. |
| `type` | `String`, `Number` (compared as `decimal`), `Boolean`, `Date` (ISO-8601, compared in UTC), `StringList` |
| `required` | Missing or `null` values fail input validation (422) |
| `aliases` | Phrases the natural-language generator maps to this field |

Limits: at most 200 fields; a path may not be both a value and a parent of another path.

## Condition tree

Every node is exactly one of:

```jsonc
{ "all": [ <node>, ... ] }      // every child matches (an empty group never matches and is rejected on write)
{ "any": [ <node>, ... ] }      // at least one child matches
{ "not": <node> }
{ "field": "document.amount", "operator": "GreaterThanOrEqual", "value": 50000, "ignoreCase": false }
```

Example:

```json
{
  "all": [
    { "field": "document.department", "operator": "Equals", "value": "IT", "ignoreCase": true },
    { "field": "document.amount", "operator": "GreaterThanOrEqual", "value": 50000 },
    { "not": { "field": "supplier.riskRating", "operator": "In", "value": ["High", "Critical"] } }
  ]
}
```

Unknown properties, unknown operators and unknown fields are rejected, never ignored.

### Operators

| Operator | Field types | Value | Semantics |
|---|---|---|---|
| `Equals`, `NotEquals` | String, Number, Boolean, Date | scalar | typed equality (`10` equals `10.0`; `"2026-03-01"` equals `"2026-03-01T00:00:00Z"`) |
| `GreaterThan`, `GreaterThanOrEqual`, `LessThan`, `LessThanOrEqual` | Number, Date | scalar | typed ordering |
| `Between` | Number, Date | `[low, high]` | inclusive; `low <= high` is enforced on write |
| `In`, `NotIn` | String, Number | non-empty array | membership with typed equality |
| `Contains` | String, StringList | string | substring, or list membership for `StringList` |
| `StartsWith`, `EndsWith` | String | string | prefix / suffix |
| `Matches` | String | regex string | .NET regex, compiled on write, max length 256, evaluated with a timeout (100 ms default) |
| `IsNull`, `IsNotNull` | any | none | presence test |

Rules of thumb:

* A missing or `null` fact only satisfies `IsNull`; every other operator returns false and says why in the trace.
* A value that cannot be coerced to the field type never matches (the trace note explains it). Numeric and boolean
  strings (`"12"`, `"true"`) are accepted for Number/Boolean fields.
* Strings compare ordinally; set `ignoreCase: true` for case-insensitive comparison (String/StringList only).

### Limits

| Option (`RuleEngine:*`) | Default | Enforced |
|---|---|---|
| `MaxConditionDepth` | 10 | on write and on evaluation |
| `MaxConditionNodes` | 200 | on write and on evaluation |
| `MaxActionsPerRule` | 20 | on write |
| `MaxRegexLength` | 256 | on write |
| `RegexTimeoutMs` | 100 | on write (compile) and evaluation |

## Actions

```json
[
  { "type": "RequireApproval", "parameters": { "role": "Manager", "reason": "Quantity above 10" } },
  { "type": "AddTag", "parameters": { "tag": "large-order" } }
]
```

| Type | Parameters | Executor |
|---|---|---|
| `SetValue` | `field` (declared path), `value` (field type) | applied during evaluation so later rules can read it; recorded on execution |
| `RequireApproval` | `role`, optional `reason` | recorded; decision becomes `ApprovalRequired` |
| `AddTag` | `tag` (letters, digits, `_ . : -`) | recorded; returned in `tags` |
| `Reject` | `reason` | recorded; decision becomes `Rejected` (wins over approval) |
| `Notify` | `channel`, `template` | POSTs to `Webhooks:NotificationChannels:<channel>`; `Skipped` if not configured |
| `TriggerWorkflow` | `workflow` | POSTs to `Webhooks:Workflows:<workflow>`; `Skipped` if not configured |

Webhook failures that may succeed later (network errors, timeouts, HTTP 408/429/5xx) are retried by the
`ActionDispatcher` with exponential backoff and equal jitter (`ActionMaxAttempts`, `ActionBaseDelayMs`,
`ActionMaxDelayMs`). Other 4xx responses fail immediately. Each executor implements `IActionExecutor`.

## Evaluation semantics

* Rules run in priority order (lower first), then by name, then by id: fully deterministic.
* `FirstMatch` stops after the first matching rule (later rules appear in the trace with `evaluated: false`);
  `AllMatches` evaluates every rule and concatenates their actions in order.
* Only the current published version of active rules is executed. Simulation can use drafts.

## Trace

Every execution and simulation returns one trace per rule with the evaluated tree:

```json
{
  "ruleName": "Large quantity needs manager approval", "versionNumber": 1, "versionStatus": "Published",
  "priority": 10, "evaluated": true, "matched": true,
  "condition": { "kind": "leaf", "result": true, "field": "document.quantity",
                 "operator": "GreaterThan", "expected": "10", "actual": "25" }
}
```

`expected` and `actual` are raw JSON. `actual` is omitted when the fact was absent, and `note` explains
non-obvious outcomes (missing fact, uncoercible value, regex timeout).

## Validation error codes

Write endpoints return 422 with `errors[] = { field: <JSON path>, message, code }`, for example
`{ "field": "condition.all[1].field", "code": "UNKNOWN_FIELD" }`.

| Code | Meaning |
|---|---|
| `CONDITION_NOT_OBJECT`, `NODE_SHAPE`, `EMPTY_GROUP`, `UNKNOWN_PROPERTY` | structural problems |
| `DEPTH_LIMIT`, `NODE_LIMIT` | limits exceeded |
| `UNKNOWN_FIELD`, `UNKNOWN_OPERATOR`, `OPERATOR_TYPE_MISMATCH` | schema / operator problems |
| `VALUE_REQUIRED`, `VALUE_NOT_ALLOWED`, `VALUE_TYPE`, `VALUE_ARRAY_REQUIRED`, `BETWEEN_ARITY`, `BETWEEN_ORDER` | value problems |
| `REGEX_INVALID`, `REGEX_TOO_LONG` | unsafe or broken regex |
| `ACTIONS_NOT_ARRAY`, `ACTIONS_EMPTY`, `ACTIONS_LIMIT`, `ACTION_NOT_OBJECT`, `UNKNOWN_ACTION_TYPE`, `ACTION_PARAMETER_MISSING`, `ACTION_PARAMETER_INVALID` | action problems |
| `CONFLICT` (warning) | overlaps an active rule at the same priority with contradictory actions |
| `UNKNOWN_ACTION_PARAMETER`, `IGNORECASE_IGNORED` (warnings) | harmless but suspicious input |

### Conflict detection

`ConflictDetector` is a static analysis over conjunctive conditions (leaves and nested `all`). Per field it
intersects numeric/date ranges and allowed value sets; two rules may overlap unless a shared field has disjoint
constraints. If they may overlap, have the same priority and prescribe contradictory actions (`Reject` vs
`RequireApproval`, or `SetValue` of the same field to different values), a `CONFLICT` warning is returned. Rules
using `any`/`not` are skipped: the detector can miss conflicts, but it does not invent them for the rules it analyses.
