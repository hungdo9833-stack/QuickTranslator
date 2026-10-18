using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Tests;

public sealed class RelationshipAddressingReviewTests
{
    [Theory]
    [InlineData("老婆", "vợ")]
    [InlineData("老公", "chồng")]
    [InlineData("妈妈", "mẹ")]
    [InlineData("爸爸", "bố")]
    [InlineData("哥哥", "anh")]
    [InlineData("妹妹", "em gái")]
    [InlineData("师父", "sư phụ")]
    public void Converts_only_literal_addressing_with_clear_vietnamese_equivalent(
        string source, string expected)
    {
        Assert.Equal(expected, RelationshipAddressingReview.VietnameseDraft(source));
        Assert.Contains(source, RelationshipAddressingReview.ConversionNote(source));
        Assert.Contains(expected, RelationshipAddressingReview.ConversionNote(source));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Missing_AI_values_remain_empty_without_guessing(string value)
    {
        Assert.Equal("", RelationshipAddressingReview.VietnameseDraft(value));
        Assert.Equal("AI chưa đề xuất.", RelationshipAddressingReview.ConversionNote(value));
    }

    [Theory]
    [InlineData("自己", "自己")]
    [InlineData("咱", "咱")]
    [InlineData("我", "我")]
    [InlineData("đại ca", "đại ca")]
    public void Ambiguous_or_existing_vietnamese_values_are_not_replaced(
        string source, string expected)
    {
        Assert.Equal(expected, RelationshipAddressingReview.VietnameseDraft(source));
    }

    [Fact]
    public void Hints_never_infer_a_missing_speaker_pronoun_or_reverse_direction()
    {
        var item = new IntelligenceRelationshipSuggestion
        {
            SpeakerChineseName = "林哲", ListenerChineseName = "苏雨",
            Relationship = "Vợ chồng", AddresseeReference = "老婆",
            ReverseAddresseeReference = "老公"
        };
        Assert.False(RelationshipAddressingReview.HasCompleteForwardAddressing(item));
        Assert.Equal("", RelationshipAddressingReview.VietnameseDraft(item.SelfReference));
        Assert.Equal("vợ", RelationshipAddressingReview.VietnameseDraft(item.AddresseeReference));
        Assert.Equal("", RelationshipAddressingReview.VietnameseDraft(item.ReverseSelfReference));
        Assert.Equal("chồng", RelationshipAddressingReview.VietnameseDraft(item.ReverseAddresseeReference));
        Assert.Contains("Không suy", RelationshipAddressingReview.ManualReviewHint("Vợ chồng"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unverified_prefix_is_not_persisted_as_a_user_entered_relationship_name()
    {
        Assert.Equal("chị em", RelationshipAddressingReview.EditableLabel("Cần xác minh quan hệ: chị em"));
        Assert.Equal("Vợ chồng", RelationshipAddressingReview.EditableLabel("Vợ chồng"));
    }
}
