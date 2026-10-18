namespace QuickTranslator.AiEditor.Core.BookIntelligence;

/// <summary>
/// Stage 2: validates AI relationship HYPOTHESES, never claims semantic certainty
/// from mere name co-occurrence. Stage 3: sanitizes unsupported addressing.
/// This gate runs only on new AI results; existing user-approved data is unchanged.
/// </summary>
public static class RelationshipIntelligenceQualityGate
{
    public enum Direction { Unverified, SpeakerHasRole, ListenerHasRole }
    public sealed record Decision(bool HasCoLocatedNames, bool HasRelevantCue,
        bool RequiresDirection, bool CanOfferForReview, string Message);

    private static readonly string[] KinshipCues =
        ["父", "母", "爹", "娘", "爸", "妈", "哥", "姐", "弟", "妹", "亲生", "兄", "姐妹", "儿", "女儿", "孩子"];
    private static readonly string[] MarriageCues =
        ["夫妻", "丈夫", "妻子", "老婆", "老公", "夫君", "娘子", "结婚", "婚", "夫妇"];
    private static readonly string[] RomanticCues =
        ["恋", "男友", "女友", "情侣", "恋人", "交往", "男朋友", "女朋友"];
    private static readonly string[] TeacherCues =
        ["师父", "师尊", "徒", "老师", "学生", "先生", "弟子"];
    private static readonly string[] RankCues =
        ["上司", "下属", "主人", "仆", "师兄", "长老", "宗主", "少爷", "小姐", "管事", "领导"];

    public static bool RequiresDirection(KnowledgeRelationType type) => type is
        KnowledgeRelationType.Parent or KnowledgeRelationType.Child or
        KnowledgeRelationType.BiologicalFather or KnowledgeRelationType.BiologicalMother or
        KnowledgeRelationType.BiologicalChild or KnowledgeRelationType.AdoptiveParent or
        KnowledgeRelationType.AdoptiveChild or KnowledgeRelationType.Grandfather or
        KnowledgeRelationType.Grandmother or KnowledgeRelationType.Grandchild or
        KnowledgeRelationType.Teacher or KnowledgeRelationType.Disciple or
        KnowledgeRelationType.Superior or KnowledgeRelationType.Subordinate;

    public static Decision Assess(IntelligenceRelationshipSuggestion suggestion,
        IReadOnlyDictionary<Guid, CorpusEvidence> evidence)
    {
        RelationshipLabelNormalizer.Result label = RelationshipLabelNormalizer.Normalize(suggestion.Relationship);
        if (label.Classification != RelationshipLabelNormalizer.Kind.Stable)
            return new(false, false, false, false,
                "Nhãn quan hệ chưa chuẩn hoặc chỉ là sự kiện/cảm xúc; cần đối chiếu thủ công.");
        var excerpts = suggestion.EvidenceIds.Where(evidence.ContainsKey).Select(id => evidence[id])
            .Where(e => e.IsCurrent).ToArray();
        bool coLocated = excerpts.Any(e => e.Excerpt.Contains(suggestion.SpeakerChineseName, StringComparison.Ordinal)
            && e.Excerpt.Contains(suggestion.ListenerChineseName, StringComparison.Ordinal));
        string[] cues = label.RelationType switch
        {
            KnowledgeRelationType.Spouse => MarriageCues,
            KnowledgeRelationType.RomanticPartner => RomanticCues,
            KnowledgeRelationType.Teacher or KnowledgeRelationType.Disciple => TeacherCues,
            KnowledgeRelationType.Superior or KnowledgeRelationType.Subordinate => RankCues,
            _ => KinshipCues
        };
        bool relevantCue = excerpts.Any(e =>
            e.Excerpt.Contains(suggestion.SpeakerChineseName, StringComparison.Ordinal) &&
            e.Excerpt.Contains(suggestion.ListenerChineseName, StringComparison.Ordinal) &&
            cues.Any(cue => e.Excerpt.Contains(cue, StringComparison.Ordinal)));
        bool directional = RequiresDirection(label.RelationType);
        string message = !coLocated
            ? "Hai tên chỉ được dẫn ở các chứng cứ rời nhau, chưa chứng minh quan hệ."
            : !relevantCue
                ? "Hai tên xuất hiện cùng đoạn nhưng chưa có tín hiệu quan hệ tương ứng; không coi là chứng minh."
                : directional && suggestion.ConfirmedDirection == Direction.Unverified
                    ? "Quan hệ có chiều. Chưa xác minh ai là cha/mẹ, con, thầy, trò hoặc cấp trên."
                    : "Có tín hiệu liên quan nhưng vẫn cần người dùng xác nhận ngữ nghĩa.";
        return new(coLocated, relevantCue, directional, coLocated && relevantCue, message);
    }

    public static string SafeAddressingDraft(string? raw)
    {
        string value = (raw ?? "").Trim();
        // Chinese labels, third-person nouns or generic first-person placeholders
        // cannot be installed as a speaker/listener-specific Vietnamese rule.
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => c >= '\u3400' && c <= '\u9fff'))
            return "";
        string folded = value.ToLowerInvariant();
        if (folded is "tôi" or "ta" or "mình" or "người yêu" or "bạn trai" or "bạn gái" or
            "boyfriend" or "girlfriend" or "wife" or "husband" or "me" or "you")
            return "";
        return value;
    }

    public static void ProtectUnverifiedNewSuggestion(IntelligenceRelationshipSuggestion item,
        IReadOnlyDictionary<Guid, CorpusEvidence> evidence)
    {
        Decision check = Assess(item, evidence);
        if (!check.CanOfferForReview || check.RequiresDirection)
        {
            item.Status = IntelligenceReviewStatus.ReviewLater;
            item.Confidence = IntelligenceConfidence.Ambiguous;
        }
        string[] raw = [item.SelfReference, item.AddresseeReference,
            item.ReverseSelfReference, item.ReverseAddresseeReference];
        string[] safe = raw.Select(SafeAddressingDraft).ToArray();
        if (!raw.SequenceEqual(safe))
        {
            item.Reason = AppendNote(item.Reason, "Xưng hô AI chưa kiểm chứng: " +
                string.Join(" / ", raw.Select(x => string.IsNullOrWhiteSpace(x) ? "(trống)" : x)) +
                ". Không dùng làm quy tắc dịch.");
            item.SelfReference = safe[0];
            item.AddresseeReference = safe[1];
            item.ReverseSelfReference = safe[2];
            item.ReverseAddresseeReference = safe[3];
            if (item.Status == IntelligenceReviewStatus.Pending)
                item.Status = IntelligenceReviewStatus.ReviewLater;
        }
        item.Reason = AppendNote(item.Reason, "Quality Gate: " + check.Message);
    }

    // Unlike unverified AI drafts, explicit human-reviewed Vietnamese self
    // references such as "tôi" or "ta" are valid in the right dialogue context.
    public static bool MayInstallAddressing(string self, string call)
    {
        static bool IsVietnameseAddressing(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Any(c => c >= '\u3400' && c <= '\u9fff')) return false;
            string folded = value.Trim().ToLowerInvariant();
            return folded is not ("boyfriend" or "girlfriend" or "male friend" or
                "female friend" or "wife" or "husband" or "me" or "you");
        }
        return IsVietnameseAddressing(self) && IsVietnameseAddressing(call);
    }

    private static string AppendNote(string reason, string note) =>
        reason.Contains(note, StringComparison.Ordinal) ? reason : reason + "\n" + note;
}
