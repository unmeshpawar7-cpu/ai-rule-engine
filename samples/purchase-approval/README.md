# Sample: purchase approval

| File | Use |
|---|---|
| `ruleset.json` | Body for `POST /api/rulesets` (fact schema with aliases used by the NL generator) |
| `rules/*.json` | Bodies for `POST /api/rulesets/{id}/rules` |
| `tests/*.tests.json` | Each element is a body for `POST /api/rules/{id}/tests` |
| `facts/*.json` | Bodies for `POST /api/rulesets/{id}/execute` and `/simulate` |
| `nl-prompts.json` | Texts for `POST /api/rulesets/{id}/rules/generate` with the outcome the offline grammar produces |

`facts/invalid-missing-quantity.json` is deliberately invalid and returns 422 with per-field errors.
See `docs/api-examples.md` for a complete curl walkthrough.
