# Design assistant (plan Phase 8)

An optional assistant on the project's Assistant page explains results and drafts work for the engineer. It uses the
Claude Messages API with tool use. It is **off unless enabled** (`Assistant:Enabled=true` and `Assistant:ApiKey`);
when off, the page says so, the project page shows no Assistant link, the assistant endpoints answer 404, and
everything else works unchanged. The whole test suite runs with it off (8.5).

## What it can do (8.1–8.4)

The tool gateway (`AssistantTools`) is the only thing the model can call. Every tool reads stored data for the project
in the request, or proposes a draft. Inputs are checked against strict JSON schemas (unknown properties and values
are refused); the project is never taken from the model.

| Tool | Does |
|---|---|
| get_project_overview | rules, rate list, connection point, field progress, latest runs and whether they pass |
| list_sites | transformer and mini-sub sites with ids |
| get_load_summary | loads by class, three-phase count, overrides with reasons |
| list_design_runs | runs of one kind |
| get_design_result | one section of a stored result: summary, checks, failed checks, traced values (value, formula id, clause, inputs), costs, issues, options, assumptions |
| get_assumptions | the assumptions register |
| draft_design_run | proposes LV, MV or option-search parameters; nothing runs until the engineer confirms (8.2) |
| draft_report_section | writes a draft narrative section; the engineer edits and approves it (8.4) |

There is no calculation tool: explanations come from the traceability records only (8.3), and the system prompt
requires every number to come from a tool result with its formula and clause.

A confirmed draft starts through the same code and checks as the run form. Report sections (introduction, site
description, design approach, options considered, conclusions) can also be written by hand on the Documents page;
only **approved** sections are printed in the design report, and approving one makes the documents stale. The
assistant cannot replace a section the engineer wrote or one that is approved.

## What it cannot do (8.6)

- No tool changes the connection point, loads, buildings, routes, rules, the assumptions register or sign-off.
- Text from the field or imports (site notes, override reasons, names) is returned as data with a note that it is not
  an instruction, and the system prompt says so too.
- Tests (`AssistantTests`) script a model that tries `sign_off`, `update_connection_point`, extra properties, bad
  enum values, another project's run and another project's site; all are refused and nothing changes. A load override
  reason and a site note carrying an injected instruction come back only as data.

## Settings

| Setting | Default | |
|---|---|---|
| `Assistant:Enabled` | false | the feature flag |
| `Assistant:ApiKey` | — | Anthropic API key (set as a secret, e.g. `Assistant__ApiKey`) |
| `Assistant:Model` | claude-sonnet-5-5 | |
| `Assistant:MaxTurns` | 8 | model calls per message |
| `Assistant:MaxTokens` | 2000 | |

## API

- `GET /api/assistant/status`
- `POST /api/projects/{id}/assistant/messages` {conversationId?, message} → reply, tool calls, new drafts
- `GET …/assistant/drafts`, `POST …/assistant/drafts/{did}/confirm`, `POST …/assistant/drafts/{did}/reject`
- `GET /api/projects/{id}/report-sections`, `PUT …/report-sections/{key}` {text, version}, `POST …/report-sections/{key}/approve`
