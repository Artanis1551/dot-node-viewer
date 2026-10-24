# Architecture Decision Records (ADRs)

This directory contains the **Architecture Decision Records (ADRs)** for `dot-node-viewer`. Each record documents an architectural decision, its context, accepted rationale, and technical consequences.

All agents and contributors must follow the protocol outlined in [`AGENTS.md`](../../AGENTS.md) when consulting or authoring ADRs.

---

## Index of Architectural Decisions

| ADR | Title | Status | Stage | One-Sentence Brief |
|---|---|:---:|:---:|---|
| [ADR 0001](0001-hybrid-parsing-and-encapsulation.md) | Hybrid Format Parsing & Strict Schema Encapsulation | Accepted | S00 | Combines mature AOT-compatible parsing libraries with custom zero-allocation span readers while strictly encapsulating all format-specific types behind `internal` and wrapping all format errors in `ModelParseException`. |
| [ADR 0002](0002-immutable-record-ir.md) | Pure Immutable C# Record Unified Graph IR | Accepted | S00 | Defines the Unified Graph IR as pure, thread-safe, immutable C# records and closed discriminated unions in `DotNodeViewer.Core.Ir` without any secondary FlatBuffers/Protobuf IR schemas or UI layout coupling. |
| [ADR 0003](0003-lazy-weight-slices.md) | Zero-Allocation Topology Scanning & Lazy Weight Slicing | Accepted | S00 | Forbids eager byte allocations for tensor weights during stream parsing by recording lightweight `TensorDataSlice` descriptors that seek and slice payloads on demand via `TensorDataLoader`. |
| [ADR 0004](0004-tunit.md) | Adoption of TUnit as Solution-Wide Test Framework | Accepted | S00 | Adopts TUnit as the sole test framework across the solution for NativeAOT alignment, strongly typed `[MethodDataSource]` data-driven tests, and native asynchronous assertions. |
| [ADR 0005](0005-avalonia-version.md) | Pinning Avalonia 11.3.12 & Avalonia 12 Migration Boundary | Accepted | S00 | Pins Avalonia `11.3.12` for all UI development across Stages S00–S12 while enforcing forward-compatible compiled bindings and isolating platform calls ahead of the Stage S13 Avalonia 12 migration. |
| [ADR 0006](0006-ir-v1.md) | Frozen v1 Unified Graph IR & Format Abstraction Contract | Accepted | S01 | Freezes the public Unified Graph IR records, format detector/reader abstractions, and golden topology snapshot schemas as the v1 contract across all subsequent model format implementations. |
| [ADR 0007](0007-isolated-model-test-ci-jobs-and-pipeline-reporting.md) | Isolated Model Test CI Jobs and Pipeline Inspection Reporting | Accepted | S04 | Isolates explicit neural network model tests into format-specific CI jobs and generates rich pipeline inspection reports across terminal logs and GitHub Actions step summaries. |
| [ADR 0008](0008-delegate-subgraph-expansion-and-ethos-u.md) | On-Demand Delegate Subgraph Expansion & Arm Ethos-U Command Stream Decoding | Accepted | W01 | Defines on-demand delegate subgraph expansion via `IDelegateSubgraphExpander` and decodes Vela Ethos-U command streams into synthesized subgraphs with inferred dataflow edges. |

---

## How to Author a New ADR

1. Create a new Markdown file named `NNNN-<kebab-case-slug>.md` using the next sequential 4-digit number.
2. Structure the document with sections:
   - `# ADR NNNN: <Title>`
   - Metadata list: `- **Status:** Proposed | Accepted | Deprecated | Superseded by ADR XXXX`, `- **Date:** YYYY-MM-DD`, `- **Stage:** Sxx`
   - `## Context`: Problem statement, constraints, alternatives evaluated, and motivations.
   - `## Decision`: Concrete architectural choices, guardrails, and implementation rules.
   - `## Consequences`: Positive outcomes, operational tradeoffs, and downstream requirements.
3. Update this index table (`docs/adr/README.md`) with a concise, one-sentence brief.
4. Record the new ADR in `docs/roadmap/PROGRESS.md` under the active stage's hand-off entry.
