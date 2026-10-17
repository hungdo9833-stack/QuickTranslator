using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace QuickTranslator.AiEditor.Core.Importing;

public sealed class ChapterDetector
{
    private static readonly IChapterTitleRecognizer[] Recognizers =
        [new ChineseOrdinalHeadingRecognizer(), new LatinNumberedHeadingRecognizer(), new SpecialChapterHeadingRecognizer()];

    public ImmutableImportPlan Detect(string source, string sourceName = "clipboard") => Detect(RawSourceReader.FromText(source, sourceName));
    public ImmutableImportPlan Detect(RawSourceSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSnapshot(source);
        ImmutableArray<SourceLine> lines = UnicodeLineScanner.Scan(source);
        var candidates = ImmutableArray.CreateBuilder<HeadingCandidate>();
        for (int index = 0; index < lines.Length; index++)
        {
            foreach (IChapterTitleRecognizer recognizer in Recognizers)
            {
                if (!recognizer.TryRecognize(lines[index].Content, out HeadingRecognition recognition)) continue;
                candidates.Add(CandidateClassifier.Classify(lines[index], index, lines, recognition));
                break;
            }
        }
        ImmutableArray<HeadingCandidate> candidateArray = candidates.ToImmutable();
        var issues = ChapterSequenceAnalyzer.Analyze(candidateArray, source.Text.Length).ToBuilder();
        ImmutableArray<PlannedSourceRegion> regions = BoundaryPlanner.Plan(source, lines, candidateArray);
        AddSecondaryScanIssues(source, candidateArray, regions, issues);
        PlannedSourceRegion? prefix = regions.FirstOrDefault(x => x.Kind == PlannedRegionKind.Unclassified);
        if (prefix is not null && candidateArray.Any(x => x.Decision != CandidateDecision.Rejected))
            issues.Add(new(DetectionIssueCode.UnclassifiedSource, DetectionIssueSeverity.Information, prefix.Span,
                "Vùng trước tiêu đề đầu tiên được giữ nguyên dưới dạng chưa phân loại.", []));
        ImmutableArray<SourceStructureRegion> structure = SourceStructureAnalyzer.Analyze(source, candidateArray, regions);
        var plan = new ImmutableImportPlan(source, lines, candidateArray, regions, issues.ToImmutable(), structure);
        plan.ValidateExactCoverage();
        return plan;
    }

    private static void AddSecondaryScanIssues(RawSourceSnapshot source, ImmutableArray<HeadingCandidate> candidates,
        ImmutableArray<PlannedSourceRegion> regions, ImmutableArray<DetectionIssue>.Builder issues)
    {
        foreach (HeadingCandidate candidate in candidates.Where(x => x.Decision == CandidateDecision.Rejected))
        {
            PlannedSourceRegion? containing = regions.FirstOrDefault(x => x.Kind is PlannedRegionKind.Chapter or PlannedRegionKind.SpecialChapter &&
                x.BodySpan.StartUtf16 <= candidate.HeadingSpan.StartUtf16 && x.BodySpan.EndUtf16 >= candidate.HeadingSpan.EndUtf16);
            if (containing is not null)
                issues.Add(new(DetectionIssueCode.RejectedHeadingInBody, DetectionIssueSeverity.Information,
                    candidate.HeadingSpan, "Dòng giống tiêu đề đã bị từ chối và vẫn nằm nguyên trong thân chương.", []));
        }

        int[] lengths = regions.Where(x => x.Kind is PlannedRegionKind.Chapter or PlannedRegionKind.SpecialChapter)
            .Select(x => x.BodySpan.LengthUtf16).Where(x => x > 0).Order().ToArray();
        if (lengths.Length >= 5)
        {
            int median = lengths[lengths.Length / 2];
            long threshold = Math.Max(20_000L, median * 4L);
            foreach (PlannedSourceRegion region in regions.Where(x => x.BodySpan.LengthUtf16 > threshold))
                issues.Add(new(DetectionIssueCode.SuspiciousLongChapter, DetectionIssueSeverity.Warning, region.Span,
                    "Chương dài bất thường; secondary scan không tự cắt mà yêu cầu kiểm tra ranh giới.",
                    region.CandidateIndex is int index ? [index] : []));
        }

        foreach (HeadingCandidate candidate in candidates.Where(x => x.Kind == ChapterHeadingKind.Volume &&
                     x.RawHeading.IndexOfAny(['-', '–', '—', '至', '到']) >= 0))
            issues.Add(new(DetectionIssueCode.AggregateChapterGroup, DetectionIssueSeverity.Information,
                candidate.HeadingSpan, "Nhãn chứa khoảng chương được xem là nhóm bài/quyển, không phải một chương đơn.", []));
    }

    private static void ValidateSnapshot(RawSourceSnapshot source)
    {
        if (source.OriginalBytes.IsDefault) throw new InvalidDataException("Snapshot thiếu raw bytes.");
        string rawHash = Convert.ToHexString(SHA256.HashData(source.OriginalBytes.AsSpan()));
        if (!string.Equals(rawHash, source.RawSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Raw source hash không khớp snapshot.");
        byte[] textBytes;
        try { textBytes = new UTF8Encoding(false, true).GetBytes(source.Text); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("Snapshot chứa UTF-16 không hợp lệ.", ex); }
        string textHash = Convert.ToHexString(SHA256.HashData(textBytes));
        if (!string.Equals(textHash, source.TextSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Text hash không khớp snapshot.");
    }
}
