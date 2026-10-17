using System.Collections.Immutable;

namespace QuickTranslator.AiEditor.Core.Importing;

public enum SourceTextEncoding { Auto, Utf8, Gbk, Gb18030, Utf16Le, Utf16Be }
public enum SourceNewLineStyle { None, Lf, CrLf, Cr, Mixed }
public enum ChapterHeadingKind { Chapter, Volume, SpecialChapter }
public enum CandidateDecision { Accepted, NeedsReview, Rejected }
public enum DetectionIssueSeverity { Information, Warning, Error }
public enum DetectionIssueCode
{
    NoChapterHeadings,
    MissingChapterNumbers,
    DuplicateChapterNumber,
    ReversedChapterNumber,
    NumberJump,
    AmbiguousHeading,
    UnclassifiedSource,
    RejectedHeadingInBody,
    SuspiciousLongChapter,
    AggregateChapterGroup
}
public enum PlannedRegionKind { Unclassified, Chapter, Volume, SpecialChapter }
public enum SourceStructureKind { WorkTitle, Volume, TableOfContents, AggregatePost, Chapter, Metadata, Unclassified }

public readonly record struct SourceSpan(int StartUtf16, int LengthUtf16)
{
    public int EndUtf16 => checked(StartUtf16 + LengthUtf16);
}

public sealed record RawSourceSnapshot(
    string SourceName,
    string Text,
    ImmutableArray<byte> OriginalBytes,
    string EncodingName,
    string RawSha256,
    string TextSha256,
    SourceNewLineStyle NewLineStyle,
    bool EncodingWasAutoDetected = false,
    bool EncodingDetectionUncertain = false,
    ImmutableArray<string> EncodingAlternatives = default);

public sealed record SourceLine(
    int Number,
    SourceSpan ContentSpan,
    SourceSpan TerminatorSpan,
    SourceSpan FullSpan,
    string Content)
{
    public bool IsBlank => string.IsNullOrWhiteSpace(Content);
}

public sealed record CandidateEvidence(string Code, string Description, double Weight);

public sealed record HeadingCandidate(
    int LineNumber,
    SourceSpan HeadingSpan,
    string RawHeading,
    ChapterHeadingKind Kind,
    int? Number,
    string NumberText,
    string Title,
    string Marker,
    string Recognizer,
    double Confidence,
    CandidateDecision Decision,
    ImmutableArray<CandidateEvidence> Evidence)
{
    public int? VolumeNumber => Kind == ChapterHeadingKind.Volume ? Number : null;
    public int? ChapterNumber => Kind == ChapterHeadingKind.Volume ? null : Number;
}

public sealed record SourceStructureRegion(
    SourceStructureKind Kind,
    SourceSpan Span,
    int? CandidateIndex,
    int? VolumeNumber,
    int? ChapterNumber,
    string Label,
    double Confidence,
    ImmutableArray<CandidateEvidence> Evidence);

public sealed record DetectionIssue(
    DetectionIssueCode Code,
    DetectionIssueSeverity Severity,
    SourceSpan Span,
    string Message,
    ImmutableArray<int> CandidateIndexes);

public sealed record PlannedSourceRegion(
    PlannedRegionKind Kind,
    SourceSpan Span,
    SourceSpan? HeadingSpan,
    SourceSpan BodySpan,
    int? CandidateIndex,
    int? ChapterNumber,
    string Title,
    string? VolumeTitle)
{
    public string? VolumeIdentity { get; init; }
}

public sealed class ImmutableImportPlan
{
    public ImmutableImportPlan(RawSourceSnapshot source, ImmutableArray<SourceLine> lines,
        ImmutableArray<HeadingCandidate> candidates, ImmutableArray<PlannedSourceRegion> regions,
        ImmutableArray<DetectionIssue> issues, ImmutableArray<SourceStructureRegion> structure = default)
    {
        Source = source; Lines = lines; Candidates = candidates; Regions = regions; Issues = issues;
        Structure = structure.IsDefault ? [] : structure;
    }

    public RawSourceSnapshot Source { get; }
    public ImmutableArray<SourceLine> Lines { get; }
    public ImmutableArray<HeadingCandidate> Candidates { get; }
    public ImmutableArray<PlannedSourceRegion> Regions { get; }
    public ImmutableArray<DetectionIssue> Issues { get; }
    public ImmutableArray<SourceStructureRegion> Structure { get; }
    public IEnumerable<PlannedSourceRegion> Chapters => Regions.Where(x => x.Kind is PlannedRegionKind.Chapter or PlannedRegionKind.SpecialChapter);
    public string Slice(SourceSpan span) => Source.Text.Substring(span.StartUtf16, span.LengthUtf16);
    public string ReconstructSource() => string.Concat(Regions.Select(x => Slice(x.Span)));
    public void ValidateExactCoverage()
    {
        int sourceLength = Source.Text.Length;
        if (sourceLength == 0)
        {
            if (!Regions.IsEmpty) throw new InvalidDataException("Nguồn rỗng không được có vùng import.");
            return;
        }
        if (Regions.IsEmpty) throw new InvalidDataException("Import plan không phủ nguồn.");
        int expectedStart = 0;
        foreach (PlannedSourceRegion region in Regions)
        {
            ValidateSpan(region.Span, sourceLength, "vùng nguồn");
            if (region.Span.LengthUtf16 <= 0 || region.Span.StartUtf16 != expectedStart)
                throw new InvalidDataException("Các vùng import bị hở, chồng lấn hoặc rỗng.");
            if (region.HeadingSpan is SourceSpan heading)
            {
                ValidateSpan(heading, sourceLength, "tiêu đề");
                if (heading.StartUtf16 < region.Span.StartUtf16 || heading.EndUtf16 > region.Span.EndUtf16)
                    throw new InvalidDataException("Tiêu đề nằm ngoài vùng import.");
            }
            ValidateSpan(region.BodySpan, sourceLength, "nội dung");
            if (region.BodySpan.StartUtf16 < region.Span.StartUtf16 || region.BodySpan.EndUtf16 > region.Span.EndUtf16)
                throw new InvalidDataException("Nội dung nằm ngoài vùng import.");
            expectedStart = region.Span.EndUtf16;
        }
        if (expectedStart != sourceLength || ReconstructSource() != Source.Text)
            throw new InvalidDataException("Không thể tái tạo chính xác nguyên văn từ import plan.");
    }

    private static void ValidateSpan(SourceSpan span, int sourceLength, string name)
    {
        if (span.StartUtf16 < 0 || span.LengthUtf16 < 0 || span.EndUtf16 > sourceLength)
            throw new InvalidDataException($"Offset {name} không hợp lệ.");
    }
}
