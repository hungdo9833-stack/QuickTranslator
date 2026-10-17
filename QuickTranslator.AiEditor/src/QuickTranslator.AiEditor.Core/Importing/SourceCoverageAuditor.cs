using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace QuickTranslator.AiEditor.Core.Importing;

public enum SourceCoverageStatus
{
    ImportedContent,
    ImportedHeading,
    SourceMetadata,
    VerifiedDuplicate,
    ExplicitlyExcluded,
    Unresolved
}

public sealed record SourceCoverageEntry(
    string SourceId,
    SourceSpan Span,
    int Utf16Length,
    string Sha256,
    SourceCoverageStatus Status,
    Guid? PreviewItemId,
    Guid? TargetChapterId,
    string Decision,
    string Evidence);

public sealed record SourceCoverageSummary(
    int TotalUtf16,
    int ImportedContentUtf16,
    int ImportedHeadingUtf16,
    int MetadataUtf16,
    int VerifiedDuplicateUtf16,
    int ExplicitlyExcludedUtf16,
    int UnresolvedUtf16)
{
    public bool IsFullyAccounted => TotalUtf16 == ImportedContentUtf16 + ImportedHeadingUtf16 + MetadataUtf16 +
        VerifiedDuplicateUtf16 + ExplicitlyExcludedUtf16 + UnresolvedUtf16;
    public bool IsVerifiedComplete => IsFullyAccounted && ExplicitlyExcludedUtf16 == 0 && UnresolvedUtf16 == 0;
    public bool IsPartialImport => IsFullyAccounted && !IsVerifiedComplete;
}

public sealed record SourceCoverageAudit(
    ImmutableArray<SourceCoverageEntry> Entries,
    SourceCoverageSummary Summary,
    ImmutableArray<string> BlockingIssues)
{
    public bool CanImport => BlockingIssues.IsEmpty;
    public bool CanClaimVerifiedComplete => CanImport && Summary.IsVerifiedComplete;
}

public static class SourceCoverageAuditor
{
    public static SourceCoverageAudit Audit(RawSourceSnapshot source, ImmutableArray<ImportPreviewItem> items,
        ImmutableArray<ChapterDuplicateEvidence> duplicates)
    {
        var entries = ImmutableArray.CreateBuilder<SourceCoverageEntry>();
        var blocking = ImmutableArray.CreateBuilder<string>();
        var exactDuplicateTargets = duplicates.Where(x => x.Kind == ChapterDuplicateKind.ExactDuplicate && x.IsAutomaticallyVerifiable)
            .GroupBy(x => x.SecondItemId).ToDictionary(x => x.Key, x => x.First().FirstItemId);

        foreach (ImportPreviewItem item in items.OrderBy(x => x.Span.StartUtf16))
        {
            if (item.HeadingSpan is not null && item.ContentSpan.StartUtf16 > item.Span.StartUtf16)
            {
                var heading = new SourceSpan(item.Span.StartUtf16, item.ContentSpan.StartUtf16 - item.Span.StartUtf16);
                Add(heading, HeadingStatus(item), item, "Dòng tiêu đề và newline vật lý",
                    item.Decision.ToString());
            }

            SourceSpan content = item.HeadingSpan is null ? item.Span : item.ContentSpan;
            if (content.LengthUtf16 > 0)
            {
                SourceCoverageStatus status = ContentStatus(item);
                string decision = status switch
                {
                    SourceCoverageStatus.ImportedContent => "Nhập vào ChapterDocument.OriginalText",
                    SourceCoverageStatus.VerifiedDuplicate => "Không nhập lặp; đối chiếu SHA-256 tuyệt đối",
                    SourceCoverageStatus.ExplicitlyExcluded => "Người dùng chủ động loại; vẫn lưu source rescue",
                    SourceCoverageStatus.SourceMetadata => "Giữ trong source rescue dưới dạng metadata",
                    _ => "Chưa có quyết định an toàn"
                };
                string evidence = exactDuplicateTargets.TryGetValue(item.Id, out Guid original)
                    ? $"Trùng nguyên văn với preview item {original:D}." : item.Decision.ToString();
                Add(content, status, item, decision, evidence);
            }

            if (item.HeadingSpan is null && item.ContentSpan.LengthUtf16 == 0 && item.Span.LengthUtf16 > 0)
                Add(item.Span, ContentStatus(item), item, "Vùng vật lý", item.Decision.ToString());
        }

        ValidatePartition(source, entries.ToImmutable());
        foreach (ChapterDuplicateEvidence duplicate in duplicates.Where(x => x.Kind != ChapterDuplicateKind.ExactDuplicate))
            if (items.First(x => x.Id == duplicate.FirstItemId).Included && items.First(x => x.Id == duplicate.SecondItemId).Included)
                blocking.Add($"Trùng chưa giải quyết ({duplicate.Kind}): {duplicate.Reason}");
        if (entries.Any(x => x.Status == SourceCoverageStatus.Unresolved))
            blocking.Add("Nguồn còn vùng Unresolved; chưa thể bắt đầu import an toàn.");

        SourceCoverageEntry[] all = entries.ToArray();
        var summary = new SourceCoverageSummary(source.Text.Length,
            Sum(SourceCoverageStatus.ImportedContent), Sum(SourceCoverageStatus.ImportedHeading),
            Sum(SourceCoverageStatus.SourceMetadata), Sum(SourceCoverageStatus.VerifiedDuplicate),
            Sum(SourceCoverageStatus.ExplicitlyExcluded), Sum(SourceCoverageStatus.Unresolved));
        if (!summary.IsFullyAccounted) throw new InvalidDataException("Coverage Ledger không đối chiếu đủ độ dài nguồn.");
        return new(entries.ToImmutable(), summary, blocking.Distinct(StringComparer.Ordinal).ToImmutableArray());

        int Sum(SourceCoverageStatus status) => all.Where(x => x.Status == status).Sum(x => x.Utf16Length);

        void Add(SourceSpan span, SourceCoverageStatus status, ImportPreviewItem item, string decision, string evidence)
        {
            if (span.LengthUtf16 == 0) return;
            string value = source.Text.Substring(span.StartUtf16, span.LengthUtf16);
            Guid? targetChapterId = status is SourceCoverageStatus.ImportedContent or SourceCoverageStatus.ImportedHeading
                ? item.ChapterId
                : status == SourceCoverageStatus.VerifiedDuplicate && exactDuplicateTargets.TryGetValue(item.Id, out Guid originalId)
                    ? items.First(x => x.Id == originalId).ChapterId
                    : null;
            entries.Add(new(source.RawSha256, span, span.LengthUtf16,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))), status, item.Id,
                targetChapterId,
                decision, evidence));
        }

        SourceCoverageStatus HeadingStatus(ImportPreviewItem item) => item.Kind switch
        {
            PlannedRegionKind.Volume => SourceCoverageStatus.SourceMetadata,
            PlannedRegionKind.Unclassified => SourceCoverageStatus.Unresolved,
            _ when exactDuplicateTargets.ContainsKey(item.Id) => SourceCoverageStatus.VerifiedDuplicate,
            _ when item.Included && item.Decision == CandidateDecision.Accepted => SourceCoverageStatus.ImportedHeading,
            _ when item.ExplicitlyExcluded => SourceCoverageStatus.ExplicitlyExcluded,
            _ => SourceCoverageStatus.Unresolved
        };

        SourceCoverageStatus ContentStatus(ImportPreviewItem item) => item.Kind switch
        {
            PlannedRegionKind.Volume => SourceCoverageStatus.SourceMetadata,
            PlannedRegionKind.Unclassified when item.StructureKind is SourceStructureKind.WorkTitle or
                SourceStructureKind.Metadata or SourceStructureKind.TableOfContents or SourceStructureKind.AggregatePost =>
                SourceCoverageStatus.SourceMetadata,
            PlannedRegionKind.Unclassified => ClassifyFreeRegion(item),
            _ when exactDuplicateTargets.ContainsKey(item.Id) => SourceCoverageStatus.VerifiedDuplicate,
            _ when item.Included && item.Decision == CandidateDecision.Accepted => SourceCoverageStatus.ImportedContent,
            _ when item.ExplicitlyExcluded => SourceCoverageStatus.ExplicitlyExcluded,
            _ => SourceCoverageStatus.Unresolved
        };

        SourceCoverageStatus ClassifyFreeRegion(ImportPreviewItem item)
        {
            string value = source.Text.Substring(item.Span.StartUtf16, item.Span.LengthUtf16);
            if (string.IsNullOrWhiteSpace(value)) return SourceCoverageStatus.SourceMetadata;
            bool metadata = value.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(line => line.StartsWith("作者", StringComparison.Ordinal) || line.StartsWith("书名", StringComparison.Ordinal) ||
                             line.StartsWith("書名", StringComparison.Ordinal) || line.StartsWith("版权", StringComparison.Ordinal) ||
                             line.StartsWith("版權", StringComparison.Ordinal));
            return metadata ? SourceCoverageStatus.SourceMetadata : SourceCoverageStatus.Unresolved;
        }
    }

    private static void ValidatePartition(RawSourceSnapshot source, ImmutableArray<SourceCoverageEntry> entries)
    {
        if (source.Text.Length == 0)
        {
            if (!entries.IsEmpty) throw new InvalidDataException("Nguồn rỗng không được có Coverage Ledger.");
            return;
        }
        int expected = 0;
        foreach (SourceCoverageEntry entry in entries.OrderBy(x => x.Span.StartUtf16))
        {
            if (entry.Span.StartUtf16 != expected || entry.Span.LengthUtf16 <= 0 || entry.Span.EndUtf16 > source.Text.Length)
                throw new InvalidDataException("Coverage Ledger bị hở, chồng lấn hoặc vượt nguồn.");
            string value = source.Text.Substring(entry.Span.StartUtf16, entry.Span.LengthUtf16);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Coverage Ledger có hash span không khớp nguồn.");
            expected = entry.Span.EndUtf16;
        }
        if (expected != source.Text.Length) throw new InvalidDataException("Coverage Ledger không phủ hết nguồn.");
    }
}
