using QuickTranslator.AiEditor.Core.Importing;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ChapterDetectionGoldenTests
{
    public static IEnumerable<object?[]> GoldenHeadings()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ChapterDetection", "golden-headings.tsv");
        foreach (string line in File.ReadLines(path))
        {
            string[] fields = line.Split('\t');
            yield return [Enum.Parse<ChapterHeadingKind>(fields[0]), string.IsNullOrEmpty(fields[1]) ? null : int.Parse(fields[1]), fields[2]];
        }
    }

    [Theory]
    [MemberData(nameof(GoldenHeadings))]
    public void Recognizes_real_world_heading_forms(ChapterHeadingKind kind, int? number, string heading)
    {
        ImmutableImportPlan plan = new ChapterDetector().Detect(heading + "\r\n正文。😀\r\n");
        HeadingCandidate candidate = Assert.Single(plan.Candidates.Where(x => x.Decision != CandidateDecision.Rejected));
        Assert.Equal(kind, candidate.Kind);
        Assert.Equal(number, candidate.Number);
        Assert.Equal(heading, candidate.RawHeading);
        Assert.NotEmpty(candidate.Recognizer);
        Assert.NotEmpty(candidate.Evidence);
        Assert.InRange(candidate.Confidence, 0, 1);
    }

    [Theory]
    [InlineData("第二节课是语文课，学生都回到教室。")]
    [InlineData("第三節課結束後，大家走出教室。")]
    [InlineData("第二节课结束是半个小时的活动时间。")]
    [InlineData("第三节课，马军他们班的语文老师走进了教室。")]
    public void Chinese_school_lessons_are_not_chapter_headings(string lessonLine)
    {
        string source = "第1章 开始\n第一段正文。\n" + lessonLine +
            "\n后续正文。\n第2章 继续\n第二章正文。";
        ImmutableImportPlan plan = new ChapterDetector().Detect(source);
        Assert.Equal(new int?[] { 1, 2 }, plan.Chapters.Select(x => x.ChapterNumber).ToArray());
        Assert.Contains(lessonLine, plan.Slice(plan.Chapters.First().BodySpan), StringComparison.Ordinal);
        Assert.Equal(source, plan.ReconstructSource());
    }

    [Fact]
    public void Numbered_sections_without_school_lesson_word_still_work()
    {
        const string source = "第1节 开始\n正文A。\n第2節 下一节\n正文B。";
        ImmutableImportPlan plan = new ChapterDetector().Detect(source);
        Assert.Equal(new int?[] { 1, 2 }, plan.Chapters.Select(x => x.ChapterNumber).ToArray());
    }

    [Fact]
    public void Supports_volume_resets_without_cross_volume_sequence_errors()
    {
        const string source = "第一卷 山门\n第一章 初见\n甲。\n第二章 入门\n乙。\n第二卷 中州\n第一章 重逢\n丙。\n第二章 风云\n丁。";
        ImmutableImportPlan plan = new ChapterDetector().Detect(source);
        Assert.Equal(new int?[] { 1, 2, 1, 2 }, plan.Candidates.Where(x => x.Kind == ChapterHeadingKind.Chapter && x.Decision != CandidateDecision.Rejected).Select(x => x.Number));
        Assert.DoesNotContain(plan.Issues, x => x.Code is DetectionIssueCode.DuplicateChapterNumber or DetectionIssueCode.ReversedChapterNumber);
        Assert.Equal(source, plan.ReconstructSource());
        Assert.Equal("山门", plan.Chapters.ElementAt(0).VolumeTitle);
        Assert.Equal("中州", plan.Chapters.ElementAt(2).VolumeTitle);
    }

    [Fact]
    public void Reports_gap_duplicate_and_reversed_numbers_without_renumbering()
    {
        const string source = "第1章 A\n正文\n第3章 B\n正文\n第3章 C\n正文\n第2章 D\n正文";
        ImmutableImportPlan plan = new ChapterDetector().Detect(source);
        Assert.Equal(new int?[] { 1, 3, 3, 2 }, plan.Candidates.Where(x => x.Decision != CandidateDecision.Rejected).Select(x => x.Number));
        Assert.Contains(plan.Issues, x => x.Code == DetectionIssueCode.MissingChapterNumbers);
        Assert.Contains(plan.Issues, x => x.Code == DetectionIssueCode.NumberJump);
        Assert.Contains(plan.Issues, x => x.Code == DetectionIssueCode.DuplicateChapterNumber);
        Assert.Contains(plan.Issues, x => x.Code == DetectionIssueCode.ReversedChapterNumber);
    }

    [Fact]
    public void Rejects_body_lines_that_only_resemble_headings()
    {
        const string source = "第一章 起点\n他说：“第二章就要开始了。”\n第三章的内容让他吃惊。\n她在纸上写着第九章 风雪。\n第二章 再见\n正文。";
        ImmutableImportPlan plan = new ChapterDetector().Detect(source);
        Assert.Equal(new int?[] { 1, 2 }, plan.Candidates.Where(x => x.Kind == ChapterHeadingKind.Chapter && x.Decision != CandidateDecision.Rejected).Select(x => x.Number));
        Assert.Contains(plan.Candidates, x => x.RawHeading.StartsWith("第三章的内容", StringComparison.Ordinal) && x.Decision == CandidateDecision.Rejected);
        Assert.Equal(2, plan.Chapters.Count());
    }
}
