using QuickTranslator.AiEditor.Core.Importing;

namespace QuickTranslator.AiEditor.Tests;

public sealed class VerifiedChapterReconstructionTests
{
    [Fact]
    public void Physical_boundaries_are_cut_by_offsets_before_logical_sort()
    {
        const string source = "第3章 三\n内容C\n第1章 一\n内容A\n第4章 四\n内容D\n第2章 二\n内容B";
        ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(source)).Current;

        Assert.Equal([3, 1, 4, 2], plan.Chapters.Select(x => x.Number));
        Assert.Equal([1, 2, 3, 4], plan.ChaptersInImportOrder.Select(x => x.Number));
        Assert.Equal("内容C\n", plan.Slice(plan.Chapters.First(x => x.Number == 3).ContentSpan));
        Assert.Equal("内容A\n", plan.Slice(plan.Chapters.First(x => x.Number == 1).ContentSpan));
        Assert.Equal("内容D\n", plan.Slice(plan.Chapters.First(x => x.Number == 4).ContentSpan));
        Assert.Equal("内容B", plan.Slice(plan.Chapters.First(x => x.Number == 2).ContentSpan));
        Assert.Equal(source, plan.ReconstructSource());
    }

    [Fact]
    public void Rejecting_false_heading_removes_boundary_but_preserves_heading_and_body_verbatim()
    {
        const string source = "第1章 开始\n内容A\n第2章 人物提到的故事\n内容B\n第2章 真正章节\n内容C";
        var editor = ImportPreviewEditor.Create(new ChapterDetector().Detect(source));
        ImportPreviewItem falseHeading = editor.Current.Chapters.ElementAt(1);

        editor.RejectFalseBoundary(falseHeading.Id);

        ImportPreviewPlan plan = editor.Current;
        Assert.Equal(2, plan.Chapters.Count());
        Assert.Contains("第2章 人物提到的故事\n内容B\n", plan.Slice(plan.Chapters.First().ContentSpan), StringComparison.Ordinal);
        Assert.Equal(source, plan.ReconstructSource());
    }

    [Fact]
    public void Aggregate_volume_range_is_not_classified_as_a_chapter()
    {
        const string source = "【第二部（11-20）】\n第11章 十一\n正文。\n第12章 十二\n正文。";
        ImmutableImportPlan detected = new ChapterDetector().Detect(source);

        Assert.Equal(2, detected.Chapters.Count());
        Assert.Contains(detected.Structure, x => x.Kind == SourceStructureKind.AggregatePost);
        Assert.Contains(detected.Issues, x => x.Code == DetectionIssueCode.AggregateChapterGroup);
        Assert.Equal(source, detected.ReconstructSource());
    }

    [Fact]
    public void Exact_duplicate_is_proved_by_hash_and_accounted_without_repeated_import()
    {
        const string source = "第2章 相同\n甲乙丙。\n第2章 相同\n甲乙丙。\n";
        ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(source)).Current;

        ChapterDuplicateEvidence duplicate = Assert.Single(plan.DuplicateEvidence);
        Assert.Equal(ChapterDuplicateKind.ExactDuplicate, duplicate.Kind);
        Assert.True(duplicate.IsAutomaticallyVerifiable);
        Assert.Equal(duplicate.FirstSha256, duplicate.SecondSha256);
        Assert.Single(plan.ImportableChapters);
        Assert.True(plan.CoverageAudit.Summary.VerifiedDuplicateUtf16 > 0);
        Assert.True(plan.CoverageAudit.CanClaimVerifiedComplete);
    }

    [Fact]
    public void Same_number_with_different_content_is_not_silently_resolved()
    {
        const string source = "第2章 一\n甲。\n第2章 二\n乙。\n第2章 三\n丙。\n第2章 四\n丁。";
        ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(source)).Current;

        Assert.Equal(4, plan.ImportableChapters.Count());
        Assert.Contains(plan.DuplicateEvidence, x => x.Kind == ChapterDuplicateKind.ConflictingVersion);
        Assert.True(plan.HasBlockingIssues);
        Assert.True(plan.CoverageAudit.BlockingIssues.Length > 0);
    }

    [Fact]
    public void Same_body_with_different_heading_is_not_auto_excluded()
    {
        const string source = "第2章 标题甲\n相同正文。\n第2章 标题乙\n相同正文。\n";
        ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(source)).Current;

        ChapterDuplicateEvidence duplicate = Assert.Single(plan.DuplicateEvidence);
        Assert.Equal(ChapterDuplicateKind.ExactDuplicate, duplicate.Kind);
        Assert.False(duplicate.IsAutomaticallyVerifiable);
        Assert.Equal(2, plan.ImportableChapters.Count());
        Assert.True(plan.HasBlockingIssues);
    }

    [Fact]
    public void Partial_overlap_is_detected_without_mutating_either_source_span()
    {
        const string source = "第8章 前\n段落一。\n段落二。\n段落三。\n第8章 后\n段落三。\n段落四。";
        ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(source)).Current;

        ChapterDuplicateEvidence overlap = Assert.Single(plan.DuplicateEvidence);
        Assert.Equal(ChapterDuplicateKind.PartialOverlap, overlap.Kind);
        Assert.True(overlap.OverlapUtf16 >= "段落三。\n".Length);
        Assert.Equal(source, plan.ReconstructSource());
    }

    [Fact]
    public void Repeated_numbers_in_different_volumes_keep_distinct_logical_identities()
    {
        string first = string.Concat(Enumerable.Range(1, 36).Select(i => $"第{i}章 一-{i}\n甲{i}。\n"));
        string second = string.Concat(Enumerable.Range(1, 287).Select(i => $"第{i}章 二-{i}\n乙{i}。\n"));
        string source = "第一部 正文\n" + first + "第二部 正文\n" + second;
        ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(source)).Current;

        Assert.Equal(323, plan.Chapters.Count());
        Assert.Empty(plan.DuplicateEvidence);
        Assert.Equal(323, plan.Chapters.Select(x => (x.VolumeIdentity, x.Number)).Distinct().Count());
        Assert.Equal(source, plan.ReconstructSource());
    }

    [Fact]
    public void Coverage_ledger_distinguishes_metadata_imported_duplicate_and_unresolved_source()
    {
        const string source = "作者：测试\n第一章 开始\n正文。\n第一章 开始\n正文。\n";
        ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(source)).Current;

        SourceCoverageSummary summary = plan.CoverageAudit.Summary;
        Assert.True(summary.IsFullyAccounted);
        Assert.Equal(source.Length, plan.CoverageAudit.Entries.Sum(x => x.Utf16Length));
        Assert.True(summary.MetadataUtf16 > 0);
        Assert.True(summary.ImportedContentUtf16 > 0);
        Assert.True(summary.VerifiedDuplicateUtf16 > 0);
    }

    [Fact]
    public void Explicit_exclusion_is_a_partial_import_decision_not_verified_completion()
    {
        const string source = "第2章 甲\n版本甲。\n第2章 乙\n版本乙。";
        var editor = ImportPreviewEditor.Create(new ChapterDetector().Detect(source));
        editor.SetIncluded(editor.Current.Chapters.Last().Id, false);

        Assert.True(editor.Current.CoverageAudit.CanImport);
        Assert.True(editor.Current.CoverageAudit.Summary.IsPartialImport);
        Assert.False(editor.Current.CoverageAudit.CanClaimVerifiedComplete);
        Assert.True(editor.Current.CoverageAudit.Summary.ExplicitlyExcludedUtf16 > 0);
    }

    [Fact]
    public void Unknown_nonmetadata_prefix_is_unresolved_and_blocks_safe_import()
    {
        const string source = "这段正文没有归属。\n第一章 开始\n正文。";
        ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(source)).Current;

        Assert.True(plan.CoverageAudit.Summary.UnresolvedUtf16 > 0);
        Assert.False(plan.CoverageAudit.CanImport);
        Assert.True(plan.HasBlockingIssues);
    }
}
