# ADR 0004: TUnit on Microsoft.Testing.Platform

- **Status:** Accepted
- **Date:** 2026-10-23
- **Stage:** S00

## Context

`dot-node-viewer` requires fast, parallel, AOT-friendly unit and golden-snapshot regression testing across `DotNodeViewer.Core.Tests`, `DotNodeViewer.Layout.Tests`, and `DotNodeViewer.App.Tests` on .NET 10.

## Decision

1. We standardize on **TUnit** running atop `Microsoft.Testing.Platform` (configured in `global.json` via `"test": { "runner": "Microsoft.Testing.Platform" }`).
2. All test projects (`DotNodeViewer.Core.Tests`, `DotNodeViewer.Layout.Tests`, `DotNodeViewer.App.Tests`) compile as self-contained test executables (`<OutputType>Exe</OutputType>`) using TUnit's source-generated test discovery (`[Test]`, `[Arguments]`, `[MethodDataSource]`) and fluent async assertions (`await Assert.That(...)`).

## Consequences

- Zero reflection-heavy test discovery overhead; tests run in parallel by default with fast startup.
- Manifest-driven golden topology tests (`ModelManifestTests`) use `[MethodDataSource]` for strongly typed, parameterized test execution across all staged neural network fixtures.

