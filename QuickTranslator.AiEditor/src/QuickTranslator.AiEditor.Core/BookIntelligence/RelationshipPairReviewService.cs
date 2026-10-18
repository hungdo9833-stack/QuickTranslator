using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

/// <summary>
/// Builds a non-destructive, auditable pair-level review view over legacy and new
/// relationship suggestions. A case may contain conflicting types/timelines;
/// it does not promote any AI claim or silently overwrite approved rules.
/// </summary>
public static class RelationshipPairReviewService
{
    public sealed record PairCase(
        string LeftName, string RightName,
        IReadOnlyList<IntelligenceRelationshipSuggestion> Claims,
        IReadOnlyList<Guid> EvidenceIds)
    {
        public int PendingClaims => Claims.Count(x => x.Status is
            IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater);
        public int DistinctLabels => Claims.Select(x =>
            RelationshipLabelNormalizer.Normalize(x.Relationship).Key).Distinct(StringComparer.Ordinal).Count();
        public string Summary => LeftName + " ⇄ " + RightName +
            " · " + Claims.Count + " kết luận, " + DistinctLabels +
            " loại, " + EvidenceIds.Count + " chứng cứ, " + PendingClaims + " cần duyệt";
        public bool HasConflictingClaims => DistinctLabels > 1 || Claims.Any(x =>
            x.Reason.Contains("xưng hô", StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<PairCase> Build(BookIntelligenceState state, ProjectResources resources)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(resources);

        string KeyFor(string name)
        {
            Guid? id = EntityResolutionService.ResolveStableId(state, resources, name);
            return id is Guid stable ? "I:" + stable.ToString("D") : "N:" + name.Trim();
        }

        return state.Relationships.GroupBy(x =>
        {
            string first = KeyFor(x.SpeakerChineseName);
            string second = KeyFor(x.ListenerChineseName);
            return string.CompareOrdinal(first, second) <= 0
                ? first + "\u001f" + second : second + "\u001f" + first;
        }, StringComparer.Ordinal)
        .Select(group =>
        {
            IntelligenceRelationshipSuggestion[] claims = group
                .OrderBy(x => x.RevealedAtChapter ?? int.MaxValue)
                .ThenBy(x => x.ValidFromChapter ?? int.MaxValue)
                .ThenBy(x => x.Id)
                .ToArray();
            string[] labels = [claims[0].SpeakerChineseName, claims[0].ListenerChineseName];
            Array.Sort(labels, StringComparer.Ordinal);
            Guid[] ids = claims.SelectMany(x => x.EvidenceIds).Distinct().ToArray();
            return new PairCase(labels[0], labels[1], claims, ids);
        })
        .OrderByDescending(x => x.PendingClaims)
        .ThenByDescending(x => x.Claims.Count)
        .ThenBy(x => x.LeftName, StringComparer.Ordinal)
        .ToArray();
    }
}
