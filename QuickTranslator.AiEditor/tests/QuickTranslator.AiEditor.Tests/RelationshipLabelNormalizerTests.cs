using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Tests;

public sealed class RelationshipLabelNormalizerTests
{
    [Theory]
    [InlineData("sibling", "siblings")]
    [InlineData("brother-sister", "sister-brother")]
    [InlineData("mother-son", "mother and son")]
    [InlineData("married", "spouse")]
    [InlineData("boyfriend", "girlfriend")]
    [InlineData("lover", "boyfriend/girlfriend")]
    public void English_synonyms_resolve_to_the_same_canonical_relation(string a, string b)
    {
        RelationshipLabelNormalizer.Result left = RelationshipLabelNormalizer.Normalize(a);
        RelationshipLabelNormalizer.Result right = RelationshipLabelNormalizer.Normalize(b);
        Assert.Equal(RelationshipLabelNormalizer.Kind.Stable, left.Classification);
        Assert.Equal(left.Key, right.Key);
        Assert.Equal(left.Display, right.Display);
        Assert.Equal(left, RelationshipLabelNormalizer.Normalize(left.Display));
        Assert.DoesNotContain("sibling", left.Display, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("father's request to give money", RelationshipLabelNormalizer.Kind.Event)]
    [InlineData("romantic interaction", RelationshipLabelNormalizer.Kind.Event)]
    [InlineData("romantic interest", RelationshipLabelNormalizer.Kind.Uncertain)]
    [InlineData("never heard of this label", RelationshipLabelNormalizer.Kind.Unknown)]
    public void Non_relations_cannot_be_treated_as_confirmed_stable_facts(
        string raw, RelationshipLabelNormalizer.Kind expected)
    {
        Assert.Equal(expected, RelationshipLabelNormalizer.Normalize(raw).Classification);
    }

    [Fact]
    public void Legacy_pending_synonyms_merge_evidence_without_touching_confirmed_items()
    {
        Guid firstEvidence = Guid.NewGuid(), secondEvidence = Guid.NewGuid();
        var one = new IntelligenceRelationshipSuggestion
        {
            SpeakerChineseName = "甲", ListenerChineseName = "乙",
            Relationship = "sibling", EvidenceIds = [firstEvidence]
        };
        var duplicate = new IntelligenceRelationshipSuggestion
        {
            SpeakerChineseName = "甲", ListenerChineseName = "乙",
            Relationship = "siblings", EvidenceIds = [secondEvidence]
        };
        var confirmed = new IntelligenceRelationshipSuggestion
        {
            SpeakerChineseName = "甲", ListenerChineseName = "丙",
            Relationship = "married", Status = IntelligenceReviewStatus.Accepted,
            EvidenceIds = [Guid.NewGuid()]
        };
        var state = new BookIntelligenceState
        {
            Relationships = [one, duplicate, confirmed]
        };

        Assert.True(RelationshipLabelNormalizer.NormalizePending(state));
        Assert.Equal("Anh/chị/em ruột (chưa rõ vai vế)", one.Relationship);
        Assert.Equal(IntelligenceReviewStatus.Merged, duplicate.Status);
        Assert.Contains(firstEvidence, one.EvidenceIds);
        Assert.Contains(secondEvidence, one.EvidenceIds);
        Assert.Equal("married", confirmed.Relationship);
        Assert.Equal(IntelligenceReviewStatus.Accepted, confirmed.Status);
        Assert.False(RelationshipLabelNormalizer.NormalizePending(state));
    }

    [Fact]
    public void Conflicting_addressing_is_not_silently_merged()
    {
        var a = new IntelligenceRelationshipSuggestion
        {
            SpeakerChineseName = "甲", ListenerChineseName = "乙",
            Relationship = "sibling", SelfReference = "huynh",
            AddresseeReference = "muội", EvidenceIds = [Guid.NewGuid()]
        };
        var b = new IntelligenceRelationshipSuggestion
        {
            SpeakerChineseName = "甲", ListenerChineseName = "乙",
            Relationship = "siblings", SelfReference = "muội",
            AddresseeReference = "huynh", EvidenceIds = [Guid.NewGuid()]
        };
        var eventItem = new IntelligenceRelationshipSuggestion
        {
            SpeakerChineseName = "甲", ListenerChineseName = "丙",
            Relationship = "father's request to give money", EvidenceIds = [Guid.NewGuid()]
        };
        var state = new BookIntelligenceState { Relationships = [a, b, eventItem] };

        Assert.True(RelationshipLabelNormalizer.NormalizePending(state));
        Assert.Equal(IntelligenceReviewStatus.Pending, a.Status);
        Assert.Equal(IntelligenceReviewStatus.ReviewLater, b.Status);
        Assert.Equal(IntelligenceReviewStatus.ReviewLater, eventItem.Status);
        Assert.Single(a.EvidenceIds);
        Assert.Single(b.EvidenceIds);
        Assert.Contains("xưng hô", b.Reason);
    }
}
