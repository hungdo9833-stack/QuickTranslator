using System.Collections.Immutable;

namespace QuickTranslator.AiEditor.Core.Importing;

internal static class SourceStructureAnalyzer
{
    public static ImmutableArray<SourceStructureRegion> Analyze(
        RawSourceSnapshot source,
        ImmutableArray<HeadingCandidate> candidates,
        ImmutableArray<PlannedSourceRegion> physicalRegions)
    {
        var result = ImmutableArray.CreateBuilder<SourceStructureRegion>(physicalRegions.Length);
        foreach (PlannedSourceRegion region in physicalRegions)
        {
            HeadingCandidate? candidate = region.CandidateIndex is int index ? candidates[index] : null;
            var evidence = ImmutableArray.CreateBuilder<CandidateEvidence>();
            SourceStructureKind kind;
            double confidence;
            string label;

            if (candidate is not null)
            {
                label = candidate.RawHeading;
                confidence = candidate.Confidence;
                kind = region.Kind switch
                {
                    PlannedRegionKind.Chapter or PlannedRegionKind.SpecialChapter => SourceStructureKind.Chapter,
                    PlannedRegionKind.Volume when LooksLikeAggregateRange(candidate.RawHeading) => SourceStructureKind.AggregatePost,
                    PlannedRegionKind.Volume => SourceStructureKind.Volume,
                    _ => SourceStructureKind.Unclassified
                };
                evidence.AddRange(candidate.Evidence);
                if (kind == SourceStructureKind.AggregatePost)
                    evidence.Add(new("chapter-range", "Nhãn phần chứa khoảng chương; đây là nhóm bài, không phải một chương.", 0.20));
            }
            else
            {
                string text = source.Text.Substring(region.Span.StartUtf16, region.Span.LengthUtf16);
                (kind, confidence, label) = ClassifyFreeRegion(text, region.Span.StartUtf16 == 0);
                evidence.Add(new("free-region", "Vùng không có tiêu đề chương đã xác nhận được giữ độc lập.", confidence));
            }

            result.Add(new(kind, region.Span, region.CandidateIndex,
                candidate?.VolumeNumber, candidate?.ChapterNumber, label, confidence, evidence.ToImmutable()));
        }
        return result.ToImmutable();
    }

    private static bool LooksLikeAggregateRange(string value)
    {
        ReadOnlySpan<char> text = value.AsSpan();
        int open = text.IndexOfAny("(（[【".AsSpan());
        if (open < 0) return false;
        int dash = text[(open + 1)..].IndexOfAny("-–—至到".AsSpan());
        return dash >= 0 && text[(open + 1)..].ToString().Any(char.IsDigit);
    }

    private static (SourceStructureKind Kind, double Confidence, string Label) ClassifyFreeRegion(string text, bool atStart)
    {
        string[] nonBlank = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (nonBlank.Length == 0) return (SourceStructureKind.Unclassified, 0.35, "Vùng trống");
        if (nonBlank.Any(line => line.StartsWith("作者", StringComparison.Ordinal) ||
                                 line.StartsWith("书名", StringComparison.Ordinal) ||
                                 line.StartsWith("書名", StringComparison.Ordinal) ||
                                 line.StartsWith("简介", StringComparison.Ordinal) ||
                                 line.StartsWith("簡介", StringComparison.Ordinal) ||
                                 line.StartsWith("版权", StringComparison.Ordinal) ||
                                 line.StartsWith("版權", StringComparison.Ordinal)))
            return (SourceStructureKind.Metadata, 0.82, "Metadata nguồn");
        if (atStart && nonBlank.Length == 1 && nonBlank[0].Length <= 80 &&
            nonBlank[0].IndexOfAny(['。', '！', '？', '；']) < 0)
            return (SourceStructureKind.WorkTitle, 0.58, nonBlank[0]);
        return (SourceStructureKind.Unclassified, 0.35, "Vùng chưa phân loại");
    }
}
