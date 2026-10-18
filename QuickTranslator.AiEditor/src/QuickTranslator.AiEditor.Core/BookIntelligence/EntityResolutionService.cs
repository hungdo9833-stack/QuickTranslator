using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public static class EntityResolutionService
{
    public static CandidateIdentity Resolve(BookIntelligenceState state, ProjectResources resources,
        string chineseName, string vietnameseName, IEnumerable<string> aliases, IEnumerable<Guid> evidenceIds)
    {
        string[] names = new[] { chineseName }.Concat(aliases).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        Guid[] evidence = evidenceIds.Distinct().ToArray();
        CandidateIdentity[] matches = state.CandidateIdentities.Where(c =>
            names.Contains(c.ChineseName, StringComparer.Ordinal) || c.Aliases.Any(names.Contains)).ToArray();
        CandidateIdentity identity;
        if (matches.Length == 1) identity = matches[0];
        else if (matches.Length > 1)
        {
            identity = matches[0];
            identity.Status = CandidateIdentityStatus.IdentityConflict;
            identity.Conflicts.Add($"Tên/bí danh giao nhau với candidate: {string.Join(", ", matches.Skip(1).Select(x => x.Id))}");
        }
        else
        {
            identity = new CandidateIdentity { ChineseName = chineseName.Trim(), VietnameseName = vietnameseName.Trim() };
            state.CandidateIdentities.Add(identity);
        }

        foreach (string alias in names.Where(x => x != identity.ChineseName && !identity.Aliases.Contains(x, StringComparer.Ordinal)))
            identity.Aliases.Add(alias);
        foreach (Guid id in evidence.Where(x => !identity.EvidenceHistory.Contains(x))) identity.EvidenceHistory.Add(id);
        AddBackwardEvidence(state, identity);

        CharacterProfile[] owners = resources.Characters.Profiles.Where(p => names.Any(n =>
            p.ChineseName == n || p.Aliases.Contains(n, StringComparer.Ordinal))).ToArray();
        if (owners.Length == 1 && identity.Status != CandidateIdentityStatus.ConfirmedIdentityMerge)
        {
            identity.ProposedExistingCharacterId = owners[0].Id;
            identity.Status = CandidateIdentityStatus.ProposedAliasLink;
        }
        else if (owners.Length > 1)
        {
            identity.Status = CandidateIdentityStatus.IdentityConflict;
            identity.Conflicts.Add("Bí danh đang thuộc nhiều hồ sơ Characters; cần người dùng quyết định.");
        }
        return identity;
    }

    public static Guid? ResolveStableId(BookIntelligenceState state, ProjectResources resources, string name)
    {
        CharacterProfile[] profiles = resources.Characters.Profiles.Where(p => p.ChineseName == name ||
            p.Aliases.Contains(name, StringComparer.Ordinal)).ToArray();
        if (profiles.Length == 1) return profiles[0].Id;
        CandidateIdentity[] candidates = state.CandidateIdentities.Where(c => c.ChineseName == name ||
            c.Aliases.Contains(name, StringComparer.Ordinal)).ToArray();
        return candidates.Length == 1 ? candidates[0].Id : null;
    }

    private static void AddBackwardEvidence(BookIntelligenceState state, CandidateIdentity identity)
    {
        string[] names = new[] { identity.ChineseName }.Concat(identity.Aliases).ToArray();
        foreach (CorpusEvidence evidence in state.Evidence.Where(e => e.IsCurrent &&
            names.Any(n => e.Excerpt.Contains(n, StringComparison.Ordinal))))
            if (!identity.EvidenceHistory.Contains(evidence.Id)) identity.EvidenceHistory.Add(evidence.Id);
    }
}

public static class RelationshipKnowledgeService
{
    public static KnowledgeRelationship? AddOrAggregateDirect(BookIntelligenceState state, ProjectResources resources,
        IntelligenceRelationshipSuggestion suggestion, bool reverseDirection = false)
    {
        Guid? subject = EntityResolutionService.ResolveStableId(state, resources,
            reverseDirection ? suggestion.ListenerChineseName : suggestion.SpeakerChineseName);
        Guid? target = EntityResolutionService.ResolveStableId(state, resources,
            reverseDirection ? suggestion.SpeakerChineseName : suggestion.ListenerChineseName);
        if (subject is null || target is null || subject == target) return null;
        RelationshipLabelNormalizer.Result normalized = RelationshipLabelNormalizer.Normalize(suggestion.Relationship);
        // An event, attraction, or unrecognized label must never enter the
        // knowledge graph as a confirmed social/romantic relationship.
        if (normalized.Classification != RelationshipLabelNormalizer.Kind.Stable) return null;
        KnowledgeRelationType type = normalized.RelationType;
        // Never manufacture directional facts from AI's arbitrary speaker/listener
        // ordering; a human must explicitly identify who occupies which role.
        if (RelationshipIntelligenceQualityGate.RequiresDirection(type) &&
            suggestion.ConfirmedDirection == RelationshipIntelligenceQualityGate.Direction.Unverified)
            return null;
        KnowledgeRelationship? existing = state.RelationshipGraph.FirstOrDefault(x =>
            x.SubjectCharacterId == subject && x.TargetCharacterId == target && x.RelationType == type &&
            x.Status != KnowledgeRelationshipStatus.Rejected);
        if (existing is null)
        {
            int? firstChapter = FirstChapter(state, suggestion.EvidenceIds);
            existing = new KnowledgeRelationship
            {
                SubjectCharacterId = subject.Value, TargetCharacterId = target.Value, RelationType = type,
                RelationQualifier = suggestion.Relationship, OriginalDescription = suggestion.Relationship,
                DiscoveryMethod = RelationshipDiscoveryMethod.DirectAi,
                Status = suggestion.Confidence == IntelligenceConfidence.Ambiguous
                    ? KnowledgeRelationshipStatus.Ambiguous : KnowledgeRelationshipStatus.Proposed,
                EvidenceIds = suggestion.EvidenceIds.Distinct().ToList(),
                FirstObservedChapter = firstChapter,
                ValidFromChapter = suggestion.ValidFromChapter,
                ValidToChapter = suggestion.ValidToChapter,
                RevealedAtChapter = suggestion.RevealedAtChapter ?? firstChapter
            };
            state.RelationshipGraph.Add(existing);
        }
        else
            foreach (Guid id in suggestion.EvidenceIds.Where(id => !existing.EvidenceIds.Contains(id))) existing.EvidenceIds.Add(id);
        suggestion.ValidFromChapter ??= existing.ValidFromChapter;
        suggestion.ValidToChapter ??= existing.ValidToChapter;
        suggestion.RevealedAtChapter ??= existing.RevealedAtChapter;
        suggestion.KnowledgeRelationshipId = existing.Id;
        return existing;
    }

    public static KnowledgeRelationType ParseType(string value)
    {
        RelationshipLabelNormalizer.Result normalized = RelationshipLabelNormalizer.Normalize(value);
        if (normalized.Classification == RelationshipLabelNormalizer.Kind.Stable)
            return normalized.RelationType;
        string v = value.Trim().ToLowerInvariant();
        if (v.Contains("亲生父") || v.Contains("生父") || v.Contains("biological father")) return KnowledgeRelationType.BiologicalFather;
        if (v.Contains("亲生母") || v.Contains("生母") || v.Contains("biological mother")) return KnowledgeRelationType.BiologicalMother;
        if (v.Contains("养父") || v.Contains("养母") || v.Contains("adoptive parent")) return KnowledgeRelationType.AdoptiveParent;
        if (v.Contains("同父同母") || v.Contains("full sibling")) return KnowledgeRelationType.FullSibling;
        if (v.Contains("同父异母") || v.Contains("paternal half")) return KnowledgeRelationType.PaternalHalfSibling;
        if (v.Contains("同母异父") || v.Contains("maternal half")) return KnowledgeRelationType.MaternalHalfSibling;
        if (v.Contains("结义") || v.Contains("sworn")) return KnowledgeRelationType.SwornSibling;
        if (v.Contains("兄") || v.Contains("弟") || v.Contains("姐") || v.Contains("妹") || v.Contains("sibling")) return KnowledgeRelationType.UnknownSibling;
        if (v.Contains("夫妻") || v.Contains("丈夫") || v.Contains("妻子") || v.Contains("spouse")) return KnowledgeRelationType.Spouse;
        if (v.Contains("恋") || v.Contains("romantic")) return KnowledgeRelationType.RomanticPartner;
        if (v.Contains("师父") || v.Contains("teacher")) return KnowledgeRelationType.Teacher;
        if (v.Contains("徒弟") || v.Contains("disciple")) return KnowledgeRelationType.Disciple;
        if (v.Contains("父") || v.Contains("母") || v.Contains("parent")) return KnowledgeRelationType.Parent;
        if (v.Contains("子") || v.Contains("女") || v.Contains("child")) return KnowledgeRelationType.Child;
        if (v.Contains("上司") || v.Contains("superior")) return KnowledgeRelationType.Superior;
        if (v.Contains("下属") || v.Contains("subordinate")) return KnowledgeRelationType.Subordinate;
        return KnowledgeRelationType.Social;
    }

    private static int? FirstChapter(BookIntelligenceState state, IEnumerable<Guid> evidenceIds)
    {
        var ids = evidenceIds.ToHashSet();
        return state.Chapters.Where(c => c.Evidence.Any(e => ids.Contains(e.Id)))
            .Select(c => (int?)c.ChapterNumber).OrderBy(x => x).FirstOrDefault();
    }
}
