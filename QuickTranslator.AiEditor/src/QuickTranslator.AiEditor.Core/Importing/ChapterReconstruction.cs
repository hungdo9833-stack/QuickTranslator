using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace QuickTranslator.AiEditor.Core.Importing;

public enum ChapterDuplicateKind
{
    None,
    ExactDuplicate,
    ContainedDuplicate,
    PartialOverlap,
    Continuation,
    ConflictingVersion
}

public sealed record ChapterDuplicateEvidence(
    Guid FirstItemId,
    Guid SecondItemId,
    ChapterDuplicateKind Kind,
    string FirstSha256,
    string SecondSha256,
    int OverlapUtf16,
    bool IsAutomaticallyVerifiable,
    string Reason);

public static class ChapterReconstructionAnalyzer
{
    public static ImmutableArray<ChapterDuplicateEvidence> Analyze(RawSourceSnapshot source,
        ImmutableArray<ImportPreviewItem> items)
    {
        var evidence = ImmutableArray.CreateBuilder<ChapterDuplicateEvidence>();
        foreach (IGrouping<(string Volume, int Number), ImportPreviewItem> group in items
                     .Where(x => x.IsChapter && x.Number is > 0)
                     .GroupBy(x => (x.VolumeIdentity ?? "", x.Number!.Value)))
        {
            ImportPreviewItem[] values = group.OrderBy(x => x.Span.StartUtf16).ToArray();
            if (values.Length < 2) continue;
            for (int left = 0; left < values.Length - 1; left++)
            for (int right = left + 1; right < values.Length; right++)
                evidence.Add(Compare(source, values[left], values[right]));
        }
        return evidence.ToImmutable();
    }

    private static ChapterDuplicateEvidence Compare(RawSourceSnapshot source, ImportPreviewItem first,
        ImportPreviewItem second)
    {
        string left = source.Text.Substring(first.ContentSpan.StartUtf16, first.ContentSpan.LengthUtf16);
        string right = source.Text.Substring(second.ContentSpan.StartUtf16, second.ContentSpan.LengthUtf16);
        string leftHash = Hash(left), rightHash = Hash(right);
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            string firstHeading = first.HeadingSpan is SourceSpan firstSpan
                ? source.Text.Substring(firstSpan.StartUtf16, firstSpan.LengthUtf16) : "";
            string secondHeading = second.HeadingSpan is SourceSpan secondSpan
                ? source.Text.Substring(secondSpan.StartUtf16, secondSpan.LengthUtf16) : "";
            bool sameHeading = string.Equals(firstHeading, secondHeading, StringComparison.Ordinal);
            return Result(ChapterDuplicateKind.ExactDuplicate, left.Length, sameHeading,
                sameHeading
                    ? "SHA-256, heading và nguyên văn UTF-16 giống hoàn toàn."
                    : "Thân chương giống tuyệt đối nhưng heading khác; cần chọn bản tiêu đề, không tự loại.");
        }

        if (left.Length > 0 && right.Length > 0 &&
            (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal)))
            return Result(ChapterDuplicateKind.ContainedDuplicate, Math.Min(left.Length, right.Length), true,
                "Một bản nguyên văn nằm trọn trong bản còn lại; chưa tự loại vì có thể là phiên bản rút gọn.");

        int forward = LongestSuffixPrefix(left, right);
        int reverse = LongestSuffixPrefix(right, left);
        int overlap = Math.Max(forward, reverse);
        int meaningful = Math.Max(4, Math.Min(left.Length, right.Length) / 20);
        if (overlap >= meaningful)
            return Result(ChapterDuplicateKind.PartialOverlap, overlap, false,
                $"Phát hiện giao nhau nguyên văn {overlap:N0} UTF-16 ở cuối/đầu hai bản.");

        bool adjacent = first.Span.EndUtf16 == second.Span.StartUtf16;
        bool continuationMarker = HasContinuationMarker(first.Title) || HasContinuationMarker(second.Title);
        if (adjacent && continuationMarker)
            return Result(ChapterDuplicateKind.Continuation, 0, false,
                "Hai phần liền kề vật lý và tiêu đề có tín hiệu tiếp nối; cần xác nhận trước khi ghép.");

        return Result(ChapterDuplicateKind.ConflictingVersion, 0, false,
            "Cùng số chương/quyển nhưng nội dung khác và chưa chứng minh được quan hệ.");

        ChapterDuplicateEvidence Result(ChapterDuplicateKind kind, int overlap, bool verified, string reason) =>
            new(first.Id, second.Id, kind, leftHash, rightHash, overlap, verified, reason);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // KMP scan: longest prefix of right that is also a suffix of left, O(n + m), without a sentinel character.
    private static int LongestSuffixPrefix(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        int[] prefix = new int[right.Length];
        for (int index = 1; index < right.Length; index++)
        {
            int matched = prefix[index - 1];
            while (matched > 0 && right[index] != right[matched]) matched = prefix[matched - 1];
            if (right[index] == right[matched]) matched++;
            prefix[index] = matched;
        }
        int state = 0;
        foreach (char value in left)
        {
            while (state > 0 && (state == right.Length || value != right[state])) state = prefix[state - 1];
            if (state < right.Length && value == right[state]) state++;
        }
        return state;
    }

    private static bool HasContinuationMarker(string title) =>
        title.Contains('上') || title.Contains('下') || title.Contains('续') || title.Contains('續') ||
        title.Contains("前篇", StringComparison.Ordinal) || title.Contains("后篇", StringComparison.Ordinal) ||
        title.Contains("後篇", StringComparison.Ordinal);
}
