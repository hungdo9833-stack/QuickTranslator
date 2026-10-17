using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Importing;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ChapterDetectionSafetyTests
{
    [Fact]
    public void Scanner_preserves_crlf_lf_cr_and_utf16_offsets()
    {
        const string text = "甲😀\r\n乙é\n\r丙";
        RawSourceSnapshot source = RawSourceReader.FromText(text);
        ImmutableArray<SourceLine> lines = UnicodeLineScanner.Scan(source);
        Assert.Equal(new[] { "甲😀", "乙é", "", "丙" }, lines.Select(x => x.Content));
        Assert.Equal(new[] { "\r\n", "\n", "\r", "" }, lines.Select(x => text.Substring(x.TerminatorSpan.StartUtf16, x.TerminatorSpan.LengthUtf16)));
        Assert.Equal(text, string.Concat(lines.Select(x => text.Substring(x.FullSpan.StartUtf16, x.FullSpan.LengthUtf16))));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Partitions_every_character_and_keeps_heading_and_body_offsets(string newline)
    {
        string source = $"版权声明{newline}第一卷 风起{newline}序章{newline}天地初开。{newline}第一章 开始{newline}甲😀。{newline}第二章 继续{newline}乙{newline}番外 婚礼{newline}终";
        ImmutableImportPlan plan = new ChapterDetector().Detect(source);
        plan.ValidateExactCoverage();
        Assert.Equal(source, plan.ReconstructSource());
        Assert.Equal(0, plan.Regions[0].Span.StartUtf16);
        Assert.Equal(source.Length, plan.Regions[^1].Span.EndUtf16);
        foreach (PlannedSourceRegion region in plan.Regions.Where(x => x.CandidateIndex is not null))
        {
            HeadingCandidate candidate = plan.Candidates[region.CandidateIndex!.Value];
            SourceLine line = plan.Lines[candidate.LineNumber - 1];
            Assert.Equal(candidate.RawHeading, plan.Slice(region.HeadingSpan!.Value));
            Assert.Equal(line.FullSpan.EndUtf16, region.BodySpan.StartUtf16);
        }
    }

    [Fact]
    public void No_heading_returns_one_unclassified_region_without_loss()
    {
        const string source = "普通正文。\n他说这里没有章节标题。";
        ImmutableImportPlan plan = new ChapterDetector().Detect(source);
        PlannedSourceRegion region = Assert.Single(plan.Regions);
        Assert.Equal(PlannedRegionKind.Unclassified, region.Kind);
        Assert.Equal(source, plan.Slice(region.Span));
        Assert.Contains(plan.Issues, x => x.Code == DetectionIssueCode.NoChapterHeadings);
    }

    [Fact]
    public void Empty_source_has_valid_empty_plan()
    {
        ImmutableImportPlan plan = new ChapterDetector().Detect("");
        Assert.Empty(plan.Lines); Assert.Empty(plan.Regions); Assert.Empty(plan.Candidates);
        plan.ValidateExactCoverage(); Assert.Equal("", plan.ReconstructSource());
    }

    [Fact]
    public void Invalid_utf16_is_rejected_instead_of_replaced() =>
        Assert.Throws<ArgumentException>(() => RawSourceReader.FromText("第一章\n\ud800"));

    [Fact]
    public void Snapshot_hash_tampering_is_rejected()
    {
        RawSourceSnapshot source = RawSourceReader.FromText("第一章\n正文");
        Assert.Throws<InvalidDataException>(() => new ChapterDetector().Detect(source with { Text = source.Text + "改" }));
        Assert.Throws<InvalidDataException>(() => new ChapterDetector().Detect(source with { RawSha256 = new string('0', 64) }));
    }

    [Fact]
    public void Utf8_bom_and_raw_hash_are_explicit()
    {
        byte[] bytes = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("第一章\r\n正文")];
        RawSourceSnapshot source = RawSourceReader.Decode(bytes, SourceTextEncoding.Auto);
        Assert.Equal("第一章\r\n正文", source.Text);
        Assert.Equal("UTF-8 BOM", source.EncodingName);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), source.RawSha256);
        Assert.Equal(bytes, source.OriginalBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Auto_detects_utf16_bom_without_replacement_and_preserves_raw_bytes(bool bigEndian)
    {
        const string text = "第一章 标题\r\n正文😀。";
        var encoding = new UnicodeEncoding(bigEndian, true, true);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];

        RawSourceSnapshot source = RawSourceReader.Decode(bytes);

        Assert.Equal(text, source.Text);
        Assert.Equal(bytes, source.OriginalBytes);
        Assert.Contains(bigEndian ? "BE" : "LE", source.EncodingName, StringComparison.Ordinal);
        Assert.Equal(text, new ChapterDetector().Detect(source).ReconstructSource());
    }

    [Fact]
    public void Auto_detects_legacy_chinese_without_replacement_and_keeps_raw_bytes()
    {
        byte[] gbk = [0xd6, 0xd0, 0xce, 0xc4];
        RawSourceSnapshot automatic = RawSourceReader.Decode(gbk);
        Assert.Equal("中文", automatic.Text);
        Assert.True(automatic.EncodingWasAutoDetected);
        Assert.Contains("GB", automatic.EncodingName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\uFFFD', automatic.Text);
        Assert.Equal(gbk, automatic.OriginalBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(gbk)), automatic.RawSha256);
        Assert.Equal("中文", RawSourceReader.Decode(gbk, SourceTextEncoding.Gbk).Text);
        Assert.Equal("中文", RawSourceReader.Decode(gbk, SourceTextEncoding.Gb18030).Text);
    }

    [Fact]
    public async Task Explicit_file_decoding_keeps_raw_bytes_and_source_name()
    {
        string directory = Path.Combine(Path.GetTempPath(), "qtaie-m1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "chinese-gbk.txt");
        byte[] bytes = [0xd6, 0xd0, 0xce, 0xc4, 0x0d, 0x0a];
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            RawSourceSnapshot source = await RawSourceReader.ReadFileAsync(path, SourceTextEncoding.Gbk);
            Assert.Equal("chinese-gbk.txt", source.SourceName);
            Assert.Equal("中文\r\n", source.Text);
            Assert.Equal(SourceNewLineStyle.CrLf, source.NewLineStyle);
            Assert.Equal(bytes, source.OriginalBytes);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Invalid_legacy_bytes_fail_without_replacement() =>
        Assert.Throws<SourceDecodingException>(() => RawSourceReader.Decode([0x81], SourceTextEncoding.Gb18030));

    [Fact]
    public void Ambiguous_heading_is_kept_for_review_with_reasons()
    {
        string source = "第一章 开始\n正文。\n第九章 的内容在这里被详细讲述，而且这一整行以句号结束。\n继续正文。\n第二章 结束\n正文。";
        ImmutableImportPlan plan = new ChapterDetector().Detect(source);
        HeadingCandidate ambiguous = Assert.Single(plan.Candidates.Where(x => x.Number == 9));
        Assert.Equal(CandidateDecision.NeedsReview, ambiguous.Decision);
        Assert.Contains(ambiguous.Evidence, x => x.Weight < 0);
        Assert.Contains(plan.Issues, x => x.Code == DetectionIssueCode.AmbiguousHeading);
    }

    [Fact]
    public void Very_short_and_very_long_chapters_are_not_dropped()
    {
        string longBody = new('中', 100_000);
        string source = "第一章 短\n甲\n第二章 长\n" + longBody;
        ImmutableImportPlan plan = new ChapterDetector().Detect(source);
        Assert.Equal(2, plan.Chapters.Count());
        Assert.Equal(source, plan.ReconstructSource());
        Assert.Contains(longBody, plan.Slice(plan.Chapters.Last().BodySpan), StringComparison.Ordinal);
    }

    [Fact]
    public void Seeded_property_roundtrip_preserves_all_source_text()
    {
        var random = new Random(731);
        for (int trial = 0; trial < 100; trial++)
        {
            string newline = (trial % 3) switch { 0 => "\r\n", 1 => "\n", _ => "\r" };
            string source = "前置😀é" + newline + string.Concat(Enumerable.Range(1, random.Next(2, 35))
                .Select(i => $"第{i}章 标题{i}{newline}正文𠀀😀é{new string('字', random.Next(1, 80))}{newline}"));
            ImmutableImportPlan plan = new ChapterDetector().Detect(source);
            plan.ValidateExactCoverage();
            Assert.Equal(source, plan.ReconstructSource());
        }
    }

    [Fact]
    public void Synthetic_five_thousand_chapters_are_planned_without_loss()
    {
        string source = string.Concat(Enumerable.Range(1, 5_000).Select(i => $"第{i}章 标题{i}\n原文。\n"));
        ImmutableImportPlan plan = new ChapterDetector().Detect(source);
        Assert.Equal(5_000, plan.Chapters.Count());
        Assert.Empty(plan.Issues.Where(x => x.Severity == DetectionIssueSeverity.Error));
        Assert.Equal(source, plan.ReconstructSource());
    }
}
