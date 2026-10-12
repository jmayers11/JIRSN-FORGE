# ADR 0049: V3.25 Horizon lenses and universal questions

Status: Accepted for V3.25

## Context

Forge has a factory horizon, program/contract views, work-center and machine projections, order pages, workforce views, historical schedule comparison and a deterministic Analyst. The risk is that a “past” lens accidentally reads today's approved schedule, or that each lens invents its own counts and access rules.

## Decision

Keep the seven lenses as entry points into their owning read models: Factory, Program, Contract, Work center, Machine, Order and Workforce. The Horizon page links to each lens; each destination continues to apply its source service's filters and authorization. The “What changed?” comparison stays the temporal source of truth: `HorizonHistoryService` asks Planning for the approved schedule in force at each instant. Add direct comparison windows for the last eight hours, last seven days and next two weeks, and retain the user-editable instants.

Route plain-language questions from the global search into the existing Forge Analyst parser and shared engines. The chosen question remains visible and Forge returns its normal interpreted intent, facts, evidence traces and access-filtered result. No new parallel question parser or generative answer path is introduced.

## Consequences

No schema change is needed. The older projection comes from approved schedule history, while events and evidence remain their recorded facts. The current lens links are navigation into existing scoped pages rather than a new aggregate table; counts should be compared at the same scope and instant. Universal questions inherit Analyst's supported intents and will state ambiguities or unsupported requests rather than guessing. `+2w` future comparison holds the current approved version unless a newer version is already effective by the selected moment.
