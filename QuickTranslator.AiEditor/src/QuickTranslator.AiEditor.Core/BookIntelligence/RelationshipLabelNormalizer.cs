namespace QuickTranslator.AiEditor.Core.BookIntelligence;

/// <summary>
/// Conservative interpretation of AI relationship labels. This does not infer
/// ages, biological ties, or speaker/listener direction from a generic label.
/// Only known stable labels enter the relationship graph automatically.
/// </summary>
public static class RelationshipLabelNormalizer
{
    public enum Kind { Stable, Uncertain, Event, Unknown }

    public sealed record Result(string Display, string Key, Kind Classification,
        KnowledgeRelationType RelationType);

    private static readonly Dictionary<string, (string Display, string Key, KnowledgeRelationType Type)> Stable =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sibling"] = ("Anh/chị/em ruột (chưa rõ vai vế)", "sibling", KnowledgeRelationType.UnknownSibling),
            ["siblings"] = ("Anh/chị/em ruột (chưa rõ vai vế)", "sibling", KnowledgeRelationType.UnknownSibling),
            ["brother-sister"] = ("Anh/chị/em ruột (chưa rõ vai vế)", "sibling", KnowledgeRelationType.UnknownSibling),
            ["sister-brother"] = ("Anh/chị/em ruột (chưa rõ vai vế)", "sibling", KnowledgeRelationType.UnknownSibling),
            ["brother"] = ("Anh/em trai (chưa rõ vai vế)", "brother", KnowledgeRelationType.UnknownSibling),
            ["sister"] = ("Chị/em gái (chưa rõ vai vế)", "sister", KnowledgeRelationType.UnknownSibling),
            ["full sibling"] = ("Anh/chị/em ruột cùng cha mẹ", "full-sibling", KnowledgeRelationType.FullSibling),
            ["half sibling"] = ("Anh/chị/em cùng cha hoặc mẹ", "half-sibling", KnowledgeRelationType.UnknownSibling),
            ["mother-son"] = ("Mẹ – con trai (kiểm tra chiều)", "mother-son", KnowledgeRelationType.Parent),
            ["mother and son"] = ("Mẹ – con trai (kiểm tra chiều)", "mother-son", KnowledgeRelationType.Parent),
            ["mother and daughter"] = ("Mẹ – con gái (kiểm tra chiều)", "mother-daughter", KnowledgeRelationType.Parent),
            ["mother-daughter"] = ("Mẹ – con gái (kiểm tra chiều)", "mother-daughter", KnowledgeRelationType.Parent),
            ["father-son"] = ("Cha – con trai (kiểm tra chiều)", "father-son", KnowledgeRelationType.Parent),
            ["father and son"] = ("Cha – con trai (kiểm tra chiều)", "father-son", KnowledgeRelationType.Parent),
            ["father-daughter"] = ("Cha – con gái (kiểm tra chiều)", "father-daughter", KnowledgeRelationType.Parent),
            ["father and daughter"] = ("Cha – con gái (kiểm tra chiều)", "father-daughter", KnowledgeRelationType.Parent),
            ["parent"] = ("Cha/mẹ – con (kiểm tra chiều)", "parent", KnowledgeRelationType.Parent),
            ["parent-child"] = ("Cha/mẹ – con (kiểm tra chiều)", "parent", KnowledgeRelationType.Parent),
            ["child"] = ("Con – cha/mẹ (kiểm tra chiều)", "child", KnowledgeRelationType.Child),
            ["married"] = ("Vợ chồng", "spouse", KnowledgeRelationType.Spouse),
            ["married couple"] = ("Vợ chồng", "spouse", KnowledgeRelationType.Spouse),
            ["spouse"] = ("Vợ chồng", "spouse", KnowledgeRelationType.Spouse),
            ["husband-wife"] = ("Vợ chồng", "spouse", KnowledgeRelationType.Spouse),
            ["boyfriend"] = ("Người yêu", "partner", KnowledgeRelationType.RomanticPartner),
            ["girlfriend"] = ("Người yêu", "partner", KnowledgeRelationType.RomanticPartner),
            ["lover"] = ("Người yêu", "partner", KnowledgeRelationType.RomanticPartner),
            ["boyfriend/girlfriend"] = ("Người yêu", "partner", KnowledgeRelationType.RomanticPartner),
            ["girlfriend/boyfriend"] = ("Người yêu", "partner", KnowledgeRelationType.RomanticPartner),
            ["romantic partner"] = ("Người yêu", "partner", KnowledgeRelationType.RomanticPartner),
            ["teacher-student"] = ("Thầy/cô – học trò (kiểm tra chiều)", "teacher-student", KnowledgeRelationType.Teacher),
            ["student-teacher"] = ("Học trò – thầy/cô (kiểm tra chiều)", "student-teacher", KnowledgeRelationType.Disciple),
            ["teacher"] = ("Thầy/cô", "teacher", KnowledgeRelationType.Teacher),
            ["disciple"] = ("Đệ tử", "disciple", KnowledgeRelationType.Disciple),
            ["superior-subordinate"] = ("Cấp trên – cấp dưới", "superior-subordinate", KnowledgeRelationType.Superior),
            ["subordinate-superior"] = ("Cấp dưới – cấp trên", "subordinate-superior", KnowledgeRelationType.Subordinate),
            ["superior"] = ("Cấp trên", "superior", KnowledgeRelationType.Superior),
            ["subordinate"] = ("Cấp dưới", "subordinate", KnowledgeRelationType.Subordinate),
            ["同父同母"] = ("Anh/chị/em ruột cùng cha mẹ", "full-sibling", KnowledgeRelationType.FullSibling),
            ["兄妹"] = ("Anh/chị/em ruột (chưa rõ vai vế)", "sibling", KnowledgeRelationType.UnknownSibling),
            ["姐弟"] = ("Anh/chị/em ruột (chưa rõ vai vế)", "sibling", KnowledgeRelationType.UnknownSibling),
            ["兄弟姐妹"] = ("Anh/chị/em ruột (chưa rõ vai vế)", "sibling", KnowledgeRelationType.UnknownSibling),
            ["兄弟"] = ("Anh/em trai (chưa rõ vai vế)", "brother", KnowledgeRelationType.UnknownSibling),
            ["姐妹"] = ("Chị/em gái (chưa rõ vai vế)", "sister", KnowledgeRelationType.UnknownSibling),
            ["母子"] = ("Mẹ – con trai (kiểm tra chiều)", "mother-son", KnowledgeRelationType.Parent),
            ["父子"] = ("Cha – con trai (kiểm tra chiều)", "father-son", KnowledgeRelationType.Parent),
            ["母女"] = ("Mẹ – con gái (kiểm tra chiều)", "mother-daughter", KnowledgeRelationType.Parent),
            ["父女"] = ("Cha – con gái (kiểm tra chiều)", "father-daughter", KnowledgeRelationType.Parent),
            ["夫妻"] = ("Vợ chồng", "spouse", KnowledgeRelationType.Spouse),
            ["师徒"] = ("Thầy trò (kiểm tra chiều)", "teacher-student", KnowledgeRelationType.Teacher),
            ["上下级"] = ("Cấp trên – cấp dưới", "superior-subordinate", KnowledgeRelationType.Superior),
        };

    private static readonly HashSet<string> UncertainLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "romantic interest", "crush", "potential romantic partner", "attraction",
        "admiration", "friendship?", "possible sibling"
    };

    private static readonly HashSet<string> EventLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "father's request to give money", "request to give money", "giving money",
        "money transfer", "romantic interaction", "argument", "fight", "conversation",
        "asked for help", "helped", "met", "first meeting"
    };

    public static Result Normalize(string? raw)
    {
        string value = (raw ?? "").Trim();
        string key = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
        if (Stable.TryGetValue(key, out var mapped))
            return new(mapped.Display, mapped.Key, Kind.Stable, mapped.Type);

        // Already-normalized labels must remain stable on subsequent app launches.
        foreach (var mapping in Stable.Values)
            if (string.Equals(value, mapping.Display, StringComparison.OrdinalIgnoreCase))
                return new(mapping.Display, mapping.Key, Kind.Stable, mapping.Type);

        if (key.StartsWith("sự kiện/ngữ cảnh", StringComparison.OrdinalIgnoreCase))
            return new(value, "event:" + key, Kind.Event, KnowledgeRelationType.Unknown);
        if (key.StartsWith("tín hiệu chưa xác nhận", StringComparison.OrdinalIgnoreCase))
            return new(value, "uncertain:" + key, Kind.Uncertain, KnowledgeRelationType.Unknown);
        if (key.StartsWith("cần xác minh quan hệ:", StringComparison.OrdinalIgnoreCase))
            return new(value, "unknown:" + key, Kind.Unknown, KnowledgeRelationType.Unknown);
        if (UncertainLabels.Contains(key))
            return new("Tín hiệu chưa xác nhận: " + value, "uncertain:" + key, Kind.Uncertain, KnowledgeRelationType.Unknown);
        if (EventLabels.Contains(key) || key.Contains("request to ", StringComparison.Ordinal) ||
            key.Contains("asked to ", StringComparison.Ordinal) ||
            key.Contains("interaction", StringComparison.Ordinal) ||
            key.Contains("gave money", StringComparison.Ordinal))
            return new("Sự kiện/ngữ cảnh (không phải quan hệ): " + value,
                "event:" + key, Kind.Event, KnowledgeRelationType.Unknown);

        // Preserve unknown labels for manual review. Never create a stable relationship
        // or infer an addressing rule merely because a text happens to contain 'father'.
        return new(string.IsNullOrWhiteSpace(value) ? "Quan hệ chưa xác định"
                : "Cần xác minh quan hệ: " + value,
            "unknown:" + key, Kind.Unknown, KnowledgeRelationType.Unknown);
    }

    public static string Display(string? raw) => Normalize(raw).Display;

    /// <summary>
    /// Migrate only reviewable suggestions. Accepted/rejected user decisions and
    /// their relationship graph are never silently altered. Duplicate suggestions
    /// are retained as Merged for audit; their evidence is copied onto the survivor.
    /// </summary>
    public static bool NormalizePending(BookIntelligenceState state)
    {
        bool changed = false;
        foreach (IntelligenceRelationshipSuggestion item in state.Relationships)
        {
            if (item.Status is not (IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater))
                continue;

            Result classification = Normalize(item.Relationship);
            if (classification.Classification != Kind.Stable)
            {
                if (item.Status == IntelligenceReviewStatus.Pending)
                {
                    item.Status = IntelligenceReviewStatus.ReviewLater;
                    changed = true;
                }
                continue;
            }

            if (!string.Equals(item.Relationship, classification.Display, StringComparison.Ordinal))
            {
                item.Reason = AppendOnce(item.Reason,
                    "Nhãn AI ban đầu: " + item.Relationship);
                item.Relationship = classification.Display;
                changed = true;
            }
        }

        var active = state.Relationships.Where(x => x.Status is
            IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater).ToArray();
        foreach (IGrouping<string, IntelligenceRelationshipSuggestion> group in active
            .Where(x => Normalize(x.Relationship).Classification == Kind.Stable)
            .GroupBy(x => string.Join("\u001f", x.SpeakerChineseName.Trim(), x.ListenerChineseName.Trim(),
                Normalize(x.Relationship).Key), StringComparer.Ordinal))
        {
            IntelligenceRelationshipSuggestion first = group.First();
            foreach (IntelligenceRelationshipSuggestion duplicate in group.Skip(1))
            {
                // Do not silently discard conflicting addressing/timeline claims.
                bool sameAddressing = string.Equals(first.SelfReference, duplicate.SelfReference, StringComparison.Ordinal)
                    && string.Equals(first.AddresseeReference, duplicate.AddresseeReference, StringComparison.Ordinal)
                    && string.Equals(first.ReverseSelfReference, duplicate.ReverseSelfReference, StringComparison.Ordinal)
                    && string.Equals(first.ReverseAddresseeReference, duplicate.ReverseAddresseeReference, StringComparison.Ordinal)
                    && first.ValidFromChapter == duplicate.ValidFromChapter
                    && first.ValidToChapter == duplicate.ValidToChapter
                    && first.RevealedAtChapter == duplicate.RevealedAtChapter;
                if (!sameAddressing)
                {
                    if (duplicate.Status == IntelligenceReviewStatus.Pending)
                    {
                        duplicate.Status = IntelligenceReviewStatus.ReviewLater;
                        changed = true;
                    }
                    string conflictNote =
                        "Cùng cặp nhân vật/loại quan hệ nhưng xưng hô hoặc thời điểm khác; cần đối chiếu, không tự gộp.";
                    if (!duplicate.Reason.Contains(conflictNote, StringComparison.Ordinal))
                    {
                        duplicate.Reason = AppendOnce(duplicate.Reason, conflictNote);
                        changed = true;
                    }
                    continue;
                }
                foreach (Guid id in duplicate.EvidenceIds.Where(id => !first.EvidenceIds.Contains(id)))
                    first.EvidenceIds.Add(id);
                first.Reason = AppendOnce(first.Reason, "Đã gộp chứng cứ từ đề xuất tương đương " + duplicate.Id);
                duplicate.Status = IntelligenceReviewStatus.Merged;
                duplicate.Reason = AppendOnce(duplicate.Reason,
                    "Trùng nhãn đã chuẩn hóa; chứng cứ được gộp vào đề xuất " + first.Id);
                changed = true;
            }
        }
        return changed;
    }

    private static string AppendOnce(string current, string note) =>
        current.Contains(note, StringComparison.Ordinal) ? current : current + "\n" + note;
}
