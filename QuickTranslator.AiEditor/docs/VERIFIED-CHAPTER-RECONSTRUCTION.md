# Verified Chapter Reconstruction + Source Coverage Audit

## Pipeline

The import path now keeps four decisions separate:

1. `ChapterDetector` finds heading candidates and records evidence, confidence and source offsets.
2. `BoundaryPlanner` partitions the decoded source strictly in physical offset order. Chapter numbers never decide a physical end boundary.
3. `ChapterReconstructionAnalyzer` groups physical chapters by stable volume identity and original number, then classifies exact, contained, partial-overlap, continuation and conflicting versions.
4. `SourceCoverageAuditor` creates a contiguous ledger covering every decoded UTF-16 position. `SafeBulkImportService` persists the ledger in the source rescue archive and verifies actual chapter files after every commit.

The source snapshot and each plan retain SHA-256 fingerprints. Detection, comparison and logical ordering never rewrite source characters, whitespace, punctuation or newlines.

## False headings and ordering

`RejectFalseBoundary` removes a boundary by joining its complete physical span, including the heading line, back into the preceding chapter. It does not exclude or delete that text. Logical ordering happens only through `ChaptersInImportOrder`, after physical spans have been created and validated by exact source reconstruction.

Every physical volume heading receives a stable identity derived from its source offset. Repeated display names therefore remain distinct volumes. The existing Project schema still requires one active chapter number globally; preflight reports and blocks repeated numbers across volumes instead of renumbering them silently.

## Duplicate evidence

- Exact: SHA-256 plus full ordinal equality. Automatic exclusion is allowed only when both the body and raw heading are equal.
- Contained: one unchanged body occurs inside the other; reported for review.
- Partial overlap: a linear KMP suffix/prefix comparison records the exact overlap length.
- Continuation: adjacent physical parts plus an explicit continuation marker; reported for review.
- Conflicting version: same logical key without proven equality or continuation.

Normalization is not applied to persisted content. Ambiguous relationships block a verified import until the user merges, excludes explicitly or otherwise resolves them.

## Coverage and persistence

Ledger states are `ImportedContent`, `ImportedHeading`, `SourceMetadata`, `VerifiedDuplicate`, `ExplicitlyExcluded` and `Unresolved`. Ledger spans are contiguous, non-overlapping, individually hashed and must total the decoded source length.

`VerifiedComplete` requires all of the following:

- no blocking preview or ledger issue;
- no explicitly excluded or unresolved UTF-16 positions;
- a verified source-rescue archive containing the plan and ledger fingerprints;
- each committed chapter reopened from disk;
- exact `OriginalText` equality and SHA-256 equality;
- ordered `Segment.SourceText` concatenation exactly equal to `OriginalText`;
- a matching active manifest entry and no pending recovery decision.

An import with a deliberate exclusion may complete operationally but is reported as `PartialImport`; it is never reported as `VerifiedComplete`.

## Verification matrix

Automated coverage includes reversed physical order, false-boundary reconstruction, four conflicting copies of one number, exact copies, partial overlaps, repeated numbering across volumes, aggregate post labels, UTF-8/BOM/GBK/GB18030/UTF-16 LE/BE, CR/LF/CRLF, 500 and 5,000 chapter disk imports, fault injection, crash/resume and source-ledger accounting.

Benchmark command:

```powershell
dotnet run --project tools/QuickTranslator.AiEditor.ChapterDetector.Demo/QuickTranslator.AiEditor.ChapterDetector.Demo.csproj -c Release -- --synthetic 10000 3000
```

Measured on the implementation workstation with 10,000 chapters and 30,197,915 UTF-16 units: physical detection 322 ms, reconstruction plus coverage 724 ms, exact round trip true. Times are observations rather than pass/fail thresholds.

## Current limitations

- The manifest schema has no persisted volume identity or original chapter number. Importing repeated numbers from multiple volumes is blocked in preflight pending a separately reviewed backward-compatible migration.
- Partial overlap and continuation are suggestions; the application does not auto-merge ambiguous text.
- Contents and aggregate posts with unusual layouts may remain `Unresolved`; the raw source is retained and verified completion is blocked.
- A user must confirm a plausible false heading when surrounding evidence cannot reject it safely.
