# Import without silent text loss — audit & completion plan

Status: VCR + SCA implemented on `codex/import-no-silent-loss-hotfix`. Review the limitations and rerun the full Windows test suite before merge.

Implementation details and verification evidence: [VERIFIED-CHAPTER-RECONSTRUCTION.md](VERIFIED-CHAPTER-RECONSTRUCTION.md).

## Confirmed sources of loss

1. ImportPreviewEditor.AutomaticallyResolveDuplicates used HashSet<int> and silently excluded every subsequent occurrence of the same chapter number, without comparing its body.
2. SafeBulkImportService imports only ImportableChapters; ValidateExactCoverage() proves the preview partitions the source, not that imported chapters cover all intended source characters.
3. External QT Text Toolkit 1.1 also chooses the longest chapter for equal (part, chapter) numbers, without semantic comparison. It is a separate tool; this branch cannot repair its binaries.
4. Detection can mistake chapter-like lines inside ordinary prose as headings, creating spurious duplicates and truncated chapters.
5. Existing tests asserted that silently ignoring later duplicate numbers was successful.

## Immediate fix in this branch

- Preserve both same-number chapters and block import until the user resolves the duplicate.
- Allow manual exclusion, but mark excluded-content divergence as an explicit warning and preserve its original source in the source rescue archive.
- Update tests to reject the old auto-skip behavior.

## Mandatory follow-up before production completion

### Phase A: header reliability

- Only recognize heading candidates from standalone heading lines and valid structural layouts. Add negative test cases where prose contains '第2章', time, quotes, repeated numbering and bracketed book titles.
- Classify same-number candidates as: exact duplicate, overlapped/copy, chapter continuation, false heading, ambiguous. Never resolve by chapter number alone or by length alone.
- Keep source order and explicit volume identity. Do not globally conflate chapter '2' from different parts/volumes.

### Phase B: full coverage preflight (must be blocking)

- In preview, show counts for selected body UTF-16 units, heading units, excluded/duplicate body units, unclassified source units, volume metadata, and source rescue units. These totals must reconcile exactly with the decoded original, preserving offsets.
- For body content of any excluded candidate, require a per-item user resolution: merge into target chapter, mark as exact duplicate with byte-equal normalization proof, or explicit discard with source rescue and reason. No implicit exclusion.
- If the result cannot represent intended source coverage unambiguously, Import button must remain disabled.
- Preserve all source spans with provenance through selection, split, merge, ordering, and save. All decisions must survive reopening the preview.

### Phase C: post-commit verification

- Before import record source hash and plan fingerprint.
- After import, reopen all chapters, verify each chapter.OriginalText against the exact content span chosen in plan (SHA256 and full equality), and concatenate actual segment.SourceText for exact equality to OriginalText.
- For excluded bytes require an explicit disposition ledger and verified rescue. Report imported, user-excluded, undecided and total source text. Block completion status on any mismatch.
- Perform same verification for existing chapters marked AlreadyImported; do not silently accept matching chapter count.
- Tests covering interrupted chunk commit, rollback, resume, multibyte Chinese source, mixed newline, bracket headings, repeats, deleted/history collision, large (500+ chapters).

### UI acceptance

- If chapter 2 appears 4 times with different bodies, show 4 candidates, no default exclusion, import disabled with clear Chinese-text preview and source offsets.
- If candidate heading is false and its span is inside chapter 1, user can merge back safely and see a before/after delta.
- 0 warnings is allowed only when no unresolved excluded body text remains.
- Source rescue is disaster recovery, not evidence that text was imported successfully.

### Test commands

dotnet restore QuickTranslator.AiEditor.slnx --locked-mode
dotnet test tests/QuickTranslator.AiEditor.Tests/QuickTranslator.AiEditor.Tests.csproj
dotnet test tests/QuickTranslator.AiEditor.Wpf.Tests/QuickTranslator.AiEditor.Wpf.Tests.csproj

Run on Windows/.NET 10 with test fixtures that contain neutral, non-sensitive dummy Chinese text.
