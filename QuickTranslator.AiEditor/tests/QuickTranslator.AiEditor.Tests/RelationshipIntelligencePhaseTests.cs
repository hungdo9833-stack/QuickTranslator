using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Tests;

public sealed class RelationshipIntelligencePhaseTests
{
    private static CorpusEvidence Ev(string passage) => new()
    {
        ChapterId = Guid.NewGuid(), Excerpt = passage, IsCurrent = true,
        SourceSha256 = "fixture", LengthUtf16 = passage.Length
    };

    private static IntelligenceRelationshipSuggestion Candidate(CorpusEvidence ev,
        string relation, string self = "", string call = "") => new()
    {
        SpeakerChineseName = "林哲", ListenerChineseName = "苏雨",
        Relationship = relation, EvidenceIds = [ev.Id],
        SelfReference = self, AddresseeReference = call,
        Status = IntelligenceReviewStatus.Pending
    };

    [Fact]
    public void Same_excerpt_and_role_cue_are_separate_from_unverified_directed_role()
    {
        CorpusEvidence evidence = Ev("林哲对苏雨说，你是我的女儿。");
        var item = Candidate(evidence, "mother-daughter");
        var map = new Dictionary<Guid, CorpusEvidence> { [evidence.Id] = evidence };
        RelationshipIntelligenceQualityGate.Decision result =
            RelationshipIntelligenceQualityGate.Assess(item, map);
        Assert.True(result.HasCoLocatedNames);
        Assert.True(result.HasRelevantCue);
        Assert.True(result.RequiresDirection);
        RelationshipIntelligenceQualityGate.ProtectUnverifiedNewSuggestion(item, map);
        Assert.Equal(IntelligenceReviewStatus.ReviewLater, item.Status);
        Assert.Equal(RelationshipIntelligenceQualityGate.Direction.Unverified, item.ConfirmedDirection);
    }

    [Fact]
    public void Mere_name_cooccurrence_does_not_prove_a_marriage()
    {
        var evidence = Ev("林哲与苏雨路过山门，两人分别离开。");
        var item = Candidate(evidence, "spouse");
        RelationshipIntelligenceQualityGate.ProtectUnverifiedNewSuggestion(item,
            new Dictionary<Guid, CorpusEvidence> { [evidence.Id] = evidence });
        Assert.Equal(IntelligenceReviewStatus.ReviewLater, item.Status);
        Assert.Contains("chưa có tín hiệu", item.Reason);
    }

    [Fact]
    public void Raw_Chinese_and_generic_nouns_do_not_enter_translation_addressing()
    {
        var evidence = Ev("林哲问苏雨：老婆，你来了。");
        var item = Candidate(evidence, "spouse", "我", "老婆");
        item.ReverseSelfReference = "boyfriend";
        item.ReverseAddresseeReference = "em";
        RelationshipIntelligenceQualityGate.ProtectUnverifiedNewSuggestion(item,
            new Dictionary<Guid, CorpusEvidence> { [evidence.Id] = evidence });
        Assert.Equal("", item.SelfReference);
        Assert.Equal("", item.AddresseeReference);
        Assert.Equal("", item.ReverseSelfReference);
        Assert.Equal("em", item.ReverseAddresseeReference);
        Assert.Contains("我 / 老婆 / boyfriend / em", item.Reason);
        Assert.False(RelationshipIntelligenceQualityGate.MayInstallAddressing("我", "em"));
        Assert.True(RelationshipIntelligenceQualityGate.MayInstallAddressing("anh", "em"));
    }

    [Fact]
    public void Direction_must_be_verified_before_graph_fact_can_be_created()
    {
        var state = new BookIntelligenceState { ProjectId = Guid.NewGuid() };
        state.CandidateIdentities.Add(new CandidateIdentity { ChineseName = "林哲" });
        state.CandidateIdentities.Add(new CandidateIdentity { ChineseName = "苏雨" });
        var resources = new ProjectResources { ProjectId = state.ProjectId,
            Characters = new CharacterProject { Id = state.ProjectId } };
        var item = new IntelligenceRelationshipSuggestion
        {
            SpeakerChineseName = "林哲", ListenerChineseName = "苏雨",
            Relationship = "Mẹ – con trai (kiểm tra chiều)",
            EvidenceIds = [Guid.NewGuid()]
        };
        Assert.Null(RelationshipKnowledgeService.AddOrAggregateDirect(state, resources, item));
        item.ConfirmedDirection = RelationshipIntelligenceQualityGate.Direction.ListenerHasRole;
        var relation = RelationshipKnowledgeService.AddOrAggregateDirect(state, resources, item,
            reverseDirection: true);
        Assert.NotNull(relation);
        Assert.Equal(KnowledgeRelationType.Parent, relation!.RelationType);
        Assert.Equal(state.CandidateIdentities[1].Id, relation.SubjectCharacterId);
        Assert.Equal(state.CandidateIdentities[0].Id, relation.TargetCharacterId);
        Assert.Equal(KnowledgeRelationshipStatus.Proposed, relation.Status);
    }

    [Fact]
    public void Many_claims_for_the_same_pair_form_one_review_case_without_erasing_history()
    {
        var state = new BookIntelligenceState { ProjectId = Guid.NewGuid() };
        var first = Candidate(Ev("林哲和苏雨是兄妹。"), "sibling");
        var second = Candidate(Ev("苏雨向林哲喊哥哥。"), "sister");
        second.SpeakerChineseName = "苏雨"; second.ListenerChineseName = "林哲";
        second.Status = IntelligenceReviewStatus.ReviewLater;
        state.Relationships.AddRange([first, second]);
        var resources = new ProjectResources { ProjectId = state.ProjectId,
            Characters = new CharacterProject { Id = state.ProjectId } };
        var pair = Assert.Single(RelationshipPairReviewService.Build(state, resources));
        Assert.Equal(2, pair.Claims.Count);
        Assert.Equal(2, pair.EvidenceIds.Count);
        Assert.True(pair.HasConflictingClaims);
        Assert.Equal(2, state.Relationships.Count);
    }

    [Fact]
    public void Accumulated_unapproved_character_names_are_only_passed_when_relevant()
    {
        var project = Guid.NewGuid();
        var state = new BookIntelligenceState { ProjectId = project };
        state.CandidateIdentities.Add(new CandidateIdentity
        { ChineseName = "林哲", VietnameseName = "Lâm Triết", Aliases = ["林少"] });
        state.CandidateIdentities.Add(new CandidateIdentity
        { ChineseName = "苏雨", VietnameseName = "Tô Vũ" });
        var resources = new ProjectResources { ProjectId = project,
            Characters = new CharacterProject { Id = project } };
        string prompt = AdaptiveBookIntelligenceAnalyzer.BuildPrompt(
            [Ev("林少抬头看着山门。")], resources, state);
        Assert.Contains("ỨNG VIÊN CHƯA DUYỆT: 林哲", prompt);
        Assert.DoesNotContain("ỨNG VIÊN CHƯA DUYỆT: 苏雨", prompt);
    }
    [Fact]
    public void Reversed_spouse_claims_share_one_pending_suggestion_and_never_auto_create_graph_facts()
    {
        Guid project = Guid.NewGuid(), chapter = Guid.NewGuid();
        const string text = "林哲看着苏雨，说道：老婆，我们是夫妻。";
        var ev = Ev(text);
        ev.ChapterId = chapter;
        var state = new BookIntelligenceState { ProjectId = project };
        state.Chapters.Add(new CorpusChapterIndex
        {
            ChapterId = chapter, ChapterNumber = 1, SourceSha256 = ev.SourceSha256,
            SourceLengthUtf16 = text.Length, Evidence = [ev]
        });
        var resources = new ProjectResources { ProjectId = project,
            Characters = new CharacterProject { Id = project } };
        var response = new AnalyzerResponse();
        response.Relationships.Add(new RelationshipFinding
        {
            SpeakerChineseName = "林哲", ListenerChineseName = "苏雨",
            Relationship = "spouse", SelfReference = "我", AddresseeReference = "老婆",
            EvidenceIds = [ev.Id], Reason = "夫妻"
        });
        response.Relationships.Add(new RelationshipFinding
        {
            SpeakerChineseName = "苏雨", ListenerChineseName = "林哲",
            Relationship = "married", ReverseSelfReference = "我",
            ReverseAddresseeReference = "老婆", EvidenceIds = [ev.Id], Reason = "夫妻"
        });
        AdaptiveBookIntelligenceAnalyzer.ApplyValidatedResponse(state, response, [ev], resources);
        var candidate = Assert.Single(state.Relationships);
        Assert.Equal("Vợ chồng", candidate.Relationship);
        Assert.Empty(state.RelationshipGraph);
        Assert.Equal(IntelligenceReviewStatus.ReviewLater, candidate.Status);
        Assert.Contains("我 / 老婆", candidate.Reason);
    }

}
