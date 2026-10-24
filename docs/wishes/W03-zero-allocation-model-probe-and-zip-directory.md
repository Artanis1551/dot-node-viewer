# Wish W03 — Unified Zero-Allocation Model Probe and ZIP Directory Seam

**Status:** Accepted  
**Depends on:** Stage **S01** (ModelProbe), Stage **S08** (ZipDirectory)  
**Suggested branch when promoted:** `refactor/zero-allocation-model-probe`  

## Goal
Unify model probing and archive inspection in `DotNodeViewer.Core` by replacing `System.IO.Compression.ZipArchive` inside `ModelProbe` with `ZipDirectory`. Expose high-level inspection queries on `ModelProbe` and memoize parsed archive metadata so that ZIP containers are parsed exactly once rather than twice during format detection and reading.

## Background & Friction

In the current codebase:
1. `ModelProbe` was authored in Stage S01 using BCL `System.IO.Compression.ZipArchive` to read zip entry names non-destructively for format detection.
2. `ZipDirectory` was authored in Stage S08 in pure C# over `RandomAccessSource` to provide zero-allocation, seek-based ZIP directory inspection with ZIP64 support.
3. As a result, the codebase maintains two completely separate ZIP directory parsers.
4. During format detection, `ModelFormatRegistry.TryDetect` causes `ModelProbe.ZipEntries` to allocate BCL `ZipArchive` instances and instantiate a `string[]`.
5. Once detected, the corresponding format reader (`PyTorchFormatReader`, `PyTorchPt2FormatReader`, or `KerasFormatReader`) creates a `RandomAccessSource` and calls `ZipDirectory.Read(source)`, parsing the central directory, headers, and entries a second time from scratch.
6. Detectors leak string inspection loops (`probe.ZipEntries.Any(...)`) instead of using focused container query primitives.

```mermaid
flowchart TD
    subgraph Current["Current: Dual ZIP Directory Parsing"]
        Stream1[Model Stream] --> Probe1[ModelProbe]
        Probe1 -.allocates.-> BCL[BCL ZipArchive]
        BCL --> Entries[string[] ZipEntries]
        Entries --> Detectors1[Detectors (LINQ scans)]
        Detectors1 --> Reader1[Format Reader]
        Reader1 --> RA1[RandomAccessSource]
        RA1 --> ZipDir1[ZipDirectory.Read]
    end

    subgraph Deepened["Deepened: Unified Probing Seam"]
        Stream2[Model Stream] --> RA2[RandomAccessSource]
        RA2 --> Probe2[ModelProbe]
        Probe2 --> ZipDir2[ZipDirectory (Internal)]
        Probe2 --> Query[probe.HasZipEntry / probe.MatchesMagic]
        Query --> Detectors2[Detectors (Direct Lookups)]
        Detectors2 --> Reader2[Format Reader]
        Probe2 -.memoized ZipDirectory.-> Reader2
    end
```

## Proposed Architecture

1. **Internalize `ZipDirectory` in `ModelProbe`:**
   - Use `ZipDirectory.TryRead` over `RandomAccessSource` inside `ModelProbe` when a ZIP signature is detected, completely removing `System.IO.Compression.ZipArchive` from the probe path.
2. **Container Query Primitives:**
   - Expose efficient O(1) probe methods:
     - `bool HasZipEntry(string entryPath)`
     - `bool HasZipEntryEndingWith(string suffix)`
     - `bool TryGetZipEntry(string entryPath, out ZipEntry entry)`
3. **Memoized Archive Sharing with Readers:**
   - Store the parsed `ZipDirectory?` on `ModelProbe`.
   - Pass the probe or its memoized `ZipDirectory` into `ModelReadOptions` or a parsing context so `PyTorchFormatReader`, `PyTorchPt2FormatReader`, and `KerasFormatReader` reuse the already-parsed directory without re-reading central directory records from disk.
4. **Zero-Allocation Probing:**
   - Detectors perform fast dictionary lookups rather than enumerating and searching strings.
5. **Headless TUnit Tests:**
   - Add comprehensive tests in `DotNodeViewer.Core.Tests` verifying single-pass ZIP parsing, non-destructive seeking, and detection accuracy across all ZIP-based models (.pt, .pt2, .keras).
