using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Tests;

public sealed class CharacterDossierReviewTests
{
    private static ProjectResources Resources(Guid id, params CharacterProfile[] characters) => new()
    {
        ProjectId = id, Characters = new CharacterProject { Id = id, Profiles = characters.ToList() }
    };

    [Fact]
    public void Twenty_character_candidates_produce_twenty_dossiers_even_with_hundreds_of_claims()
    {
        Guid id = Guid.NewGuid();
        var state = new BookIntelligenceState { ProjectId = id };
        for (int i = 0; i < 20; i++)
        {
            string chinese = "林" + i.ToString("D2");
            var identity = new CandidateIdentity
            {
                ChineseName = chinese, VietnameseName = "Nhân vật " + i
            };
            state.CandidateIdentities.Add(identity);
            state.Characters.Add(new IntelligenceCharacterSuggestion
            {
                ChineseName = chinese, VietnameseName = identity.VietnameseName,
                CandidateIdentityId = identity.Id, EvidenceIds = [Guid.NewGuid()]
            });
        }
        for (int i = 0; i < 263; i++)
        {
            state.Relationships.Add(new IntelligenceRelationshipSuggestion
            {
                SpeakerChineseName = "林00", ListenerChineseName = "林01",
                Relationship = i % 2 == 0 ? "siblings" : "sibling",
                EvidenceIds = [Guid.NewGuid()], Status = IntelligenceReviewStatus.ReviewLater,
                SelfReference = i % 3 == 0 ? "anh" : "em"
            });
        }
        IReadOnlyList<CharacterDossierReviewService.Dossier> result =
            CharacterDossierReviewService.Build(state, Resources(id));
        Assert.Equal(20, result.Count);
        var first = Assert.Single(result.Where(x => x.ChineseName == "林00"));
        Assert.Single(first.Relations);
        Assert.Equal(263, first.Relations[0].Claims.Count);
        Assert.True(first.Relations[0].HasConflictingAddressing);
        Assert.True(first.Relations[0].NeedsReview);
        // Read-model never removes, approves or changes raw evidence.
        Assert.Equal(263, state.Relationships.Count);
        Assert.All(state.Relationships, x => Assert.Equal(IntelligenceReviewStatus.ReviewLater, x.Status));
    }

    [Fact]
    public void Unique_verified_alias_and_canonical_name_are_one_dossier()
    {
        Guid id = Guid.NewGuid();
        var state = new BookIntelligenceState { ProjectId = id };
        CandidateIdentity identity = new()
        {
            ChineseName = "林哲", VietnameseName = "Lâm Triết", Aliases = ["林少"]
        };
        state.CandidateIdentities.Add(identity);
        state.Characters.Add(new IntelligenceCharacterSuggestion
        {
            CandidateIdentityId = identity.Id, ChineseName = "林哲",
            VietnameseName = "Lâm Triết", Aliases = ["林少"], EvidenceIds = [Guid.NewGuid()]
        });
        state.Relationships.Add(new IntelligenceRelationshipSuggestion
        {
            SpeakerChineseName = "林少", ListenerChineseName = "苏雨",
            Relationship = "spouse", EvidenceIds = [Guid.NewGuid()]
        });
        var dossiers = CharacterDossierReviewService.Build(state, Resources(id));
        var dossier = Assert.Single(dossiers);
        Assert.Equal("林哲", dossier.ChineseName);
        Assert.Equal("Lâm Triết", dossier.VietnameseName);
        Assert.Single(dossier.Relations);
        Assert.Equal("苏雨", dossier.Relations[0].OtherChineseName);
        Assert.Contains("林少", dossier.Aliases);
    }

    [Fact]
    public void Ambiguous_same_alias_does_not_silently_merge_distinct_people()
    {
        Guid id = Guid.NewGuid();
        var state = new BookIntelligenceState { ProjectId = id };
        state.CandidateIdentities.Add(new CandidateIdentity
            { ChineseName = "林哲", Aliases = ["阿林"] });
        state.CandidateIdentities.Add(new CandidateIdentity
            { ChineseName = "林枫", Aliases = ["阿林"] });
        state.Relationships.Add(new IntelligenceRelationshipSuggestion
        {
            SpeakerChineseName = "阿林", ListenerChineseName = "苏雨",
            Relationship = "sibling", EvidenceIds = [Guid.NewGuid()]
        });
        var rows = CharacterDossierReviewService.Build(state, Resources(id));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, x => Assert.True(x.IdentityAmbiguous));
        Assert.DoesNotContain(rows, x => x.Relations.Any());
        Assert.Single(state.Relationships);
    }

    [Fact]
    public void Previously_approved_character_is_retained_not_created_twice()
    {
        Guid id = Guid.NewGuid();
        var profile = new CharacterProfile
        {
            ChineseName = "苏雨", VietnameseName = "Tô Vũ", Aliases = ["小雨"]
        };
        var state = new BookIntelligenceState { ProjectId = id };
        state.CandidateIdentities.Add(new CandidateIdentity
        {
            ChineseName = "苏雨", Aliases = ["小雨"],
            Status = CandidateIdentityStatus.ConfirmedIdentityMerge,
            ProposedExistingCharacterId = profile.Id
        });
        var rows = CharacterDossierReviewService.Build(state, Resources(id, profile));
        Assert.Single(rows);
        Assert.True(rows[0].IsCommitted);
        Assert.False(rows[0].NeedsReview);
    }

    [Fact]
    public void Existing_approved_name_and_unapproved_identical_primary_name_share_one_flagged_display_dossier()
    {
        Guid project = Guid.NewGuid();
        var profile = new CharacterProfile
        { ChineseName = "林哲", VietnameseName = "Lâm Triết" };
        var state = new BookIntelligenceState { ProjectId = project };
        var candidate = new CandidateIdentity
        {
            ChineseName = "林哲", VietnameseName = "Lâm Triết chưa duyệt",
            Status = CandidateIdentityStatus.Candidate
        };
        state.CandidateIdentities.Add(candidate);
        state.Characters.Add(new IntelligenceCharacterSuggestion
        {
            CandidateIdentityId = candidate.Id, ChineseName = "林哲",
            VietnameseName = "Lâm Triết", EvidenceIds = [Guid.NewGuid()]
        });
        var rows = CharacterDossierReviewService.Build(state, Resources(project, profile));
        var dossier = Assert.Single(rows);
        Assert.True(dossier.IsCommitted);
        Assert.True(dossier.IdentityAmbiguous);
        Assert.True(dossier.NeedsReview);
        Assert.Single(dossier.CharacterSuggestions);
        Assert.Equal(CandidateIdentityStatus.Candidate, candidate.Status); // display-only, not approved
    }

    [Fact]
    public void Conflicting_timeline_is_grouped_without_erasing_historical_claims()
    {
        Guid id = Guid.NewGuid();
        var state = new BookIntelligenceState { ProjectId = id };
        state.CandidateIdentities.AddRange(
        [
            new CandidateIdentity { ChineseName = "林哲" },
            new CandidateIdentity { ChineseName = "苏雨" }
        ]);
        state.Relationships.Add(new IntelligenceRelationshipSuggestion
        {
            SpeakerChineseName = "林哲", ListenerChineseName = "苏雨",
            Relationship = "spouse", ValidFromChapter = 200, EvidenceIds = [Guid.NewGuid()]
        });
        state.Relationships.Add(new IntelligenceRelationshipSuggestion
        {
            SpeakerChineseName = "苏雨", ListenerChineseName = "林哲",
            Relationship = "married", ValidFromChapter = 300, EvidenceIds = [Guid.NewGuid()]
        });
        var rows = CharacterDossierReviewService.Build(state, Resources(id));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, d =>
        {
            var r = Assert.Single(d.Relations);
            Assert.Equal(2, r.Claims.Count);
            Assert.True(r.HasConflictingTimeline);
        });
    }
}
