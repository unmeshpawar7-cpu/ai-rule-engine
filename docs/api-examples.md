# API examples

All responses use the envelope `{ "success": true, "data": ..., "message": null }`; errors use
`{ "success": false, "message": "...", "errors": [{ "field", "message", "code" }], "traceId": "..." }`.
Every list endpoint accepts `?page=1&pageSize=20` (max 100) and returns `items, page, pageSize, totalCount, totalPages`.

The examples assume `docker compose up` (Development environment, demo users seeded) and `jq`.

```bash
API=http://localhost:8080
token() { curl -s $API/api/auth/token -H 'Content-Type: application/json' \
  -d "{\"userName\":\"$1\",\"password\":\"$2\"}" | jq -r .data.accessToken; }
ADMIN=$(token admin 'Admin#2026!'); AUTHOR=$(token author 'Author#2026!')
EXECUTOR=$(token executor 'Executor#2026!'); AUDITOR=$(token auditor 'Auditor#2026!')
```

## 1. Create the rule set

```bash
RS=$(curl -s $API/api/rulesets -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' \
  -d @samples/purchase-approval/ruleset.json | jq -r .data.id)
```

## 2. Author a rule by hand (Draft)

```bash
RULE=$(curl -s $API/api/rulesets/$RS/rules -H "Authorization: Bearer $AUTHOR" -H 'Content-Type: application/json' \
  -d @samples/purchase-approval/rules/01-large-quantity-manager-approval.json | jq -r .data.rule.id)
```

Response (abridged): `201 Created`, `Location: /api/rules/{id}`, `data.rule.versions[0].status = "Draft"` and
`data.warnings` with any `CONFLICT` warnings.

## 3. ...or generate one from natural language

```bash
curl -s $API/api/rulesets/$RS/rules/generate -H "Authorization: Bearer $AUTHOR" -H 'Content-Type: application/json' \
  -d '{ "text": "Documents with quantity greater than 10 should require manager approval." }' | jq
```

```json
{
  "success": true,
  "data": {
    "outcome": "DraftSaved",
    "saved": true,
    "ruleId": "6f1d6f1e-3a9b-4f7e-9c56-2b0c1a7d4e11",
    "versionNumber": 1,
    "versionStatus": "Draft",
    "proposal": {
      "name": "document.quantity > 10 -> RequireApproval",
      "priority": 100,
      "condition": { "field": "document.quantity", "operator": "GreaterThan", "value": 10 },
      "actions": [ { "type": "RequireApproval", "parameters": { "role": "Manager" } } ],
      "explanation": "When document.quantity > 10, require approval from role 'Manager'.",
      "confidence": 0.95,
      "assumptions": [
        "'quantity' was mapped to field 'document.quantity' (by name).",
        "'Documents' was read as the scope of the rule and is not turned into a condition."
      ]
    },
    "errors": [],
    "warnings": [],
    "generator": "pattern",
    "promptVersion": "pattern-grammar/v1",
    "model": null,
    "attempts": 1,
    "latencyMs": 0
  },
  "message": "Draft created. Review, test and publish it explicitly."
}
```

Text the grammar cannot map (e.g. `"If colour is red then reject"`) returns 422 with
`"Unknown field 'colour'. Known fields: document.quantity, ..."` and stores nothing.

## 4. Simulate (drafts included, no side effects)

```bash
curl -s $API/api/rulesets/$RS/simulate -H "Authorization: Bearer $AUTHOR" -H 'Content-Type: application/json' \
  -d @samples/purchase-approval/facts/large-order.json | jq '.data | {decision, matchedRules, actions}'
```

## 5. Add a test case, run tests, publish, activate

```bash
curl -s $API/api/rules/$RULE/tests -H "Authorization: Bearer $AUTHOR" -H 'Content-Type: application/json' \
  -d "$(jq '.[0]' samples/purchase-approval/tests/large-quantity.tests.json)"
curl -s -X POST $API/api/rules/$RULE/tests/run -H "Authorization: Bearer $AUTHOR" | jq '.data | {total, passed, failed}'

curl -s $API/api/rules/$RULE/publish -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' -d '{ "versionNumber": 1 }'
curl -s -X POST $API/api/rules/$RULE/activate -H "Authorization: Bearer $ADMIN"
```

A failing test case makes `publish` return 422 with `errors[].code = "TEST_FAILED"`. Authors get 403 on publish.

## 6. Execute with an Idempotency-Key

```bash
curl -s -i $API/api/rulesets/$RS/execute -H "Authorization: Bearer $EXECUTOR" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: po-2026-00042' -d @samples/purchase-approval/facts/large-order.json
```

```json
{
  "success": true,
  "data": {
    "executionId": "0b8e2c55-91d4-4a43-8f0e-6b0f3c2d7a90",
    "ruleSetId": "3c0a7e52-1f0b-4c1d-9a55-0d6f2b8e9a13",
    "correlationId": "5a3e0f0c2b6d4c3e8a1f9b7d6e5c4b3a",
    "isSimulation": false,
    "replayed": false,
    "status": "Succeeded",
    "decision": "ApprovalRequired",
    "startedAtUtc": "2026-10-08T09:12:44.512Z",
    "durationMs": 6,
    "matchedRules": [
      { "ruleId": "6f1d...", "ruleName": "Large quantity needs manager approval", "versionId": "a1b2...", "versionNumber": 1, "versionStatus": "Published", "priority": 10 }
    ],
    "actions": [
      { "sequence": 1, "actionType": "RequireApproval", "parameters": { "role": "Manager", "reason": "Quantity above 10 units" },
        "status": "Succeeded", "attempts": 1, "message": "Approval required from role 'Manager': Quantity above 10 units", "durationMs": 0 },
      { "sequence": 2, "actionType": "AddTag", "parameters": { "tag": "large-order" },
        "status": "Succeeded", "attempts": 1, "message": "Tagged 'large-order'.", "durationMs": 0 }
    ],
    "tags": [ "large-order" ],
    "outputFacts": { "document": { "quantity": 25, "amount": 12500, "department": "Operations", "documentType": "PurchaseOrder", "tags": [ "urgent" ] }, "supplier": { "riskRating": "Medium", "preferred": true } },
    "trace": [
      { "ruleName": "Large quantity needs manager approval", "versionNumber": 1, "versionStatus": "Published", "priority": 10,
        "evaluated": true, "matched": true,
        "condition": { "kind": "leaf", "result": true, "field": "document.quantity", "operator": "GreaterThan", "expected": "10", "actual": "25" } }
    ]
  },
  "message": null
}
```

(Ids shortened.) Sending the same key and body again returns `200` with `"replayed": true`; the same key with a
different body returns `409`. Invalid facts:

```bash
curl -s $API/api/rulesets/$RS/execute -H "Authorization: Bearer $EXECUTOR" -H 'Content-Type: application/json' \
  -d @samples/purchase-approval/facts/invalid-missing-quantity.json
```

```json
{
  "success": false,
  "message": "Input facts do not satisfy the rule set's fact schema.",
  "errors": [
    { "field": "facts.document.quantity", "message": "Required fact is missing or null.", "code": "FACT_INVALID" },
    { "field": "facts.document.amount", "message": "Expected a value of type Number.", "code": "FACT_INVALID" }
  ],
  "traceId": "9f2c4e1a7b3d4c5e8f6a0b1c2d3e4f5a"
}
```

## 7. History and audit

```bash
curl -s "$API/api/executions?ruleSetId=$RS&page=1&pageSize=20" -H "Authorization: Bearer $AUDITOR" | jq '.data.totalCount'
curl -s "$API/api/audit-logs?entityId=$RULE" -H "Authorization: Bearer $AUDITOR" | jq '.data.items[] | {action, actor, occurredAtUtc}'
```

## Endpoint reference

Swagger UI is served at `/swagger` in Development (or with `Swagger__Enabled=true`), generated from the XML
comments on the controllers.
