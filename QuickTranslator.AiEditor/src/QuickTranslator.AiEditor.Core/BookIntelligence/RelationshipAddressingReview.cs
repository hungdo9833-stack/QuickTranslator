namespace QuickTranslator.AiEditor.Core.BookIntelligence;

/// <summary>
/// UI-only review hints for Gemini's partially populated Chinese addressing fields.
/// Conversions are intentionally limited to unambiguous literal family/title terms.
/// They are NOT evidence of speaker age, reciprocal address, or timeline validity.
/// </summary>
public static class RelationshipAddressingReview
{
    private static readonly Dictionary<string, string> LiteralAddressing = new(StringComparer.Ordinal)
    {
        ["老婆"] = "vợ",
        ["老公"] = "chồng",
        ["妻子"] = "vợ",
        ["丈夫"] = "chồng",
        ["爸爸"] = "bố",
        ["父亲"] = "cha",
        ["妈妈"] = "mẹ",
        ["母亲"] = "mẹ",
        ["哥哥"] = "anh",
        ["姐姐"] = "chị",
        ["弟弟"] = "em trai",
        ["妹妹"] = "em gái",
        ["儿子"] = "con trai",
        ["女儿"] = "con gái",
        ["师父"] = "sư phụ",
        ["师傅"] = "sư phụ",
        ["徒弟"] = "đệ tử"
    };

    public static string VietnameseDraft(string? original)
    {
        string value = (original ?? "").Trim();
        return LiteralAddressing.TryGetValue(value, out string? translated)
            ? translated : value;
    }

    public static string ConversionNote(string? original)
    {
        string value = (original ?? "").Trim();
        string translated = VietnameseDraft(value);
        return string.IsNullOrEmpty(value)
            ? "AI chưa đề xuất."
            : string.Equals(value, translated, StringComparison.Ordinal)
                ? "Giữ nguyên đề xuất AI; chưa đủ dữ kiện để tự Việt hóa."
                : $"AI: {value} → bản nháp tiếng Việt: {translated} (cần đối chiếu chứng cứ).";
    }

    public static bool HasCompleteForwardAddressing(IntelligenceRelationshipSuggestion suggestion) =>
        !string.IsNullOrWhiteSpace(suggestion.SelfReference) &&
        !string.IsNullOrWhiteSpace(suggestion.AddresseeReference);

    public static string ManualReviewHint(string? relation)
    {
        string label = (relation ?? "").Trim().ToLowerInvariant();
        if (label.Contains("chị em") || label.Contains("anh em") || label.Contains("sibling") ||
            label.Contains("兄妹") || label.Contains("姐弟"))
            return "Anh/chị/em: phải xác định ai lớn tuổi hơn, cách xưng hô và ngữ cảnh; không tự điền anh/chị/em khi thiếu chứng cứ.";
        if (label.Contains("cha con") || label.Contains("mẹ con") ||
            label.Contains("father") || label.Contains("mother") || label.Contains("parent"))
            return "Cha/mẹ – con: cần xác định chiều quan hệ từ lời thoại; không tự điền bố/mẹ/con khi chưa biết người nói.";
        if (label.Contains("vợ chồng") || label.Contains("spouse") || label.Contains("husband"))
            return "Vợ chồng: cách gọi có thể là anh/em hoặc vợ/chồng tùy lời thoại. Không suy ra tự xưng từ nhãn quan hệ.";
        return "Chỉ xác nhận xưng hô khi chứng cứ thể hiện rõ. Quan hệ đúng không đồng nghĩa đã có quy tắc xưng hô cho dịch.";
    }

    public static string EditableLabel(string? current)
    {
        string text = (current ?? "").Trim();
        const string unverified = "Cần xác minh quan hệ: ";
        return text.StartsWith(unverified, StringComparison.OrdinalIgnoreCase)
            ? text[unverified.Length..].Trim()
            : text;
    }
}
