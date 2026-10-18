using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

/// <summary>
/// A read-only MASTER view for reviewing large Book Intelligence surveys:
/// one identity -> one dossier -> grouped relationship claims and evidence.
/// Does NOT mutate or approve raw AI findings and preserves legacy audit history.
/// </summary>
public static class CharacterDossierReviewService
{
    public sealed record RelatedPerson(
        string OtherKey, string OtherChineseName, string OtherVietnameseName,
        string RelationshipLabel, IReadOnlyList<IntelligenceRelationshipSuggestion> Claims,
        IReadOnlyList<Guid> EvidenceIds, bool HasConflictingAddressing,
        bool HasConflictingTimeline)
    {
        public int PendingClaims => Claims.Count(c => c.Status is
            IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater);
        public bool NeedsReview => !Claims.Any(c => c.Status == IntelligenceReviewStatus.Accepted) &&
            PendingClaims > 0 || HasConflictingAddressing || HasConflictingTimeline;
        public bool HasUnverifiedDirection => Claims.Any(c =>
            RelationshipIntelligenceQualityGate.RequiresDirection(
                RelationshipLabelNormalizer.Normalize(c.Relationship).RelationType) &&
            c.ConfirmedDirection == RelationshipIntelligenceQualityGate.Direction.Unverified);
        public string ReviewStatus => HasUnverifiedDirection ? "Chưa xác minh chiều" :
            HasConflictingAddressing || HasConflictingTimeline ? "Có xung đột" :
            NeedsReview ? "Chờ duyệt" : "Đã xử lý";
        public string AddressingPreview => string.Join(" / ", Claims.Select(c =>
            $"{c.SelfReference} → {c.AddresseeReference}; {c.ReverseSelfReference} → {c.ReverseAddresseeReference}")
            .Distinct(StringComparer.Ordinal).Take(3));
        public string Display => OtherVietnameseName + " (" + OtherChineseName + ") · " +
            RelationshipLabel + " · " + EvidenceIds.Count + " chứng cứ · " + ReviewStatus;
    }

    public sealed record Dossier(
        string IdentityKey, Guid Id, string ChineseName, string VietnameseName,
        IReadOnlyList<string> Aliases, CharacterGender Gender, string PrimaryPronoun,
        string SecondaryPronoun, bool IsCommitted, bool IdentityAmbiguous,
        IReadOnlyList<IntelligenceCharacterSuggestion> CharacterSuggestions,
        IReadOnlyList<RelatedPerson> Relations, IReadOnlyList<Guid> EvidenceIds)
    {
        public int PendingCharacters => CharacterSuggestions.Count(x => x.Status is
            IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater);
        public int PendingRelations => Relations.Count(x => x.NeedsReview);
        public int RawRelationshipClaims => Relations.Sum(r => r.Claims.Count);
        public bool HasUnresolvedRelations => Relations.Any(r => r.NeedsReview ||
            r.HasConflictingAddressing || r.HasConflictingTimeline || r.HasUnverifiedDirection);
        public bool NeedsReview => !IsCommitted || PendingCharacters > 0 ||
            HasUnresolvedRelations || IdentityAmbiguous;
        public string ReviewStatus => IdentityAmbiguous ? "Mơ hồ" :
            NeedsReview ? "Pending" : "Accepted";
        public string Display => VietnameseName + " (" + ChineseName + ")";
        public string Description => Relations.Count + " quan hệ · " +
            PendingRelations + " chờ duyệt · " + EvidenceIds.Count + " chứng cứ";
    }

    private sealed class Working(string key)
    {
        public string Key { get; } = key;
        public CharacterProfile? Profile { get; set; }
        public CandidateIdentity? Candidate { get; set; }
        public string Chinese { get; set; } = "";
        public string Vietnamese { get; set; } = "";
        public bool Ambiguous { get; set; }
        public HashSet<string> Aliases { get; } = new(StringComparer.Ordinal);
        public List<IntelligenceCharacterSuggestion> Suggestions { get; } = [];
        public List<(string OtherKey, IntelligenceRelationshipSuggestion Claim)> Claims { get; } = [];
        public HashSet<Guid> Evidence { get; } = [];
    }

    public static IReadOnlyList<Dossier> Build(BookIntelligenceState state, ProjectResources resources)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(resources);
        var records = new Dictionary<string, Working>(StringComparer.Ordinal);
        var names = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        Working Get(string key)
        {
            if (!records.TryGetValue(key, out Working? row))
                records.Add(key, row = new Working(key));
            return row;
        }
        void Name(string? name, string key)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            string label = name.Trim();
            if (!names.TryGetValue(label, out HashSet<string>? keys))
                names.Add(label, keys = new HashSet<string>(StringComparer.Ordinal));
            keys.Add(key);
        }

        foreach (CharacterProfile profile in resources.Characters.Profiles)
        {
            string key = "P:" + profile.Id.ToString("D");
            Working row = Get(key);
            row.Profile = profile;
            row.Chinese = profile.ChineseName;
            row.Vietnamese = profile.VietnameseName;
            Name(profile.ChineseName, key);
            foreach (string alias in profile.Aliases.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                row.Aliases.Add(alias);
                Name(alias, key);
            }
        }
        foreach (CandidateIdentity identity in state.CandidateIdentities.Where(x =>
            x.Status != CandidateIdentityStatus.Rejected))
        {
            string key = identity.Status == CandidateIdentityStatus.ConfirmedIdentityMerge &&
                identity.ProposedExistingCharacterId is Guid target &&
                resources.Characters.Profiles.Any(p => p.Id == target)
                ? "P:" + target.ToString("D")
                : "C:" + identity.Id.ToString("D");
            Working row = Get(key);
            row.Candidate ??= identity;
            if (string.IsNullOrWhiteSpace(row.Chinese)) row.Chinese = identity.ChineseName;
            if (string.IsNullOrWhiteSpace(row.Vietnamese)) row.Vietnamese = identity.VietnameseName;
            Name(identity.ChineseName, key);
            foreach (string alias in identity.Aliases.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                row.Aliases.Add(alias);
                Name(alias, key);
            }
            foreach (Guid id in identity.EvidenceHistory) row.Evidence.Add(id);
            if (identity.Status == CandidateIdentityStatus.IdentityConflict) row.Ambiguous = true;
        }
        string Resolve(string name)
        {
            name = name.Trim();
            if (names.TryGetValue(name, out HashSet<string>? matches) && matches.Count == 1)
                return matches.First();
            string key = "N:" + name;
            Working row = Get(key);
            row.Chinese = name;
            if (matches is { Count: > 1 }) row.Ambiguous = true;
            Name(name, key);
            return key;
        }
        foreach (IntelligenceCharacterSuggestion finding in state.Characters)
        {
            string key = finding.CandidateIdentityId != Guid.Empty &&
                state.CandidateIdentities.Any(x => x.Id == finding.CandidateIdentityId &&
                    x.Status != CandidateIdentityStatus.Rejected)
                ? state.CandidateIdentities.Where(x => x.Id == finding.CandidateIdentityId)
                    .Select(x => x.Status == CandidateIdentityStatus.ConfirmedIdentityMerge &&
                        x.ProposedExistingCharacterId is Guid target &&
                        resources.Characters.Profiles.Any(p => p.Id == target)
                        ? "P:" + target.ToString("D")
                        : "C:" + x.Id.ToString("D")).First()
                : Resolve(finding.ChineseName);
            Working row = Get(key);
            row.Suggestions.Add(finding);
            if (string.IsNullOrWhiteSpace(row.Chinese)) row.Chinese = finding.ChineseName;
            if (string.IsNullOrWhiteSpace(row.Vietnamese)) row.Vietnamese = finding.VietnameseName;
            Name(finding.ChineseName, key);
            foreach (string alias in finding.Aliases.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                row.Aliases.Add(alias);
                Name(alias, key);
            }
            foreach (Guid id in finding.EvidenceIds) row.Evidence.Add(id);
        }
        // An orphan relation endpoint is an unresolved identity, never a
        // fabricated CharacterProfile and never silently merged by Vietnamese name.
        foreach (IntelligenceRelationshipSuggestion claim in state.Relationships)
        {
            string fromKey = Resolve(claim.SpeakerChineseName);
            string toKey = Resolve(claim.ListenerChineseName);
            Get(fromKey).Claims.Add((toKey, claim));
            if (fromKey != toKey) Get(toKey).Claims.Add((fromKey, claim));
            foreach (Guid id in claim.EvidenceIds)
            {
                Get(fromKey).Evidence.Add(id);
                Get(toKey).Evidence.Add(id);
            }
        }

        // Resolve orphan names once more, without ever promoting ambiguous aliases.
        foreach (KeyValuePair<string, HashSet<string>> pair in names.Where(x => x.Value.Count > 1))
            foreach (string key in pair.Value)
                Get(key).Ambiguous = true;

        return records.Values
            // Relationship-only names are not silently upgraded to characters.
            // They remain visible as nested counterpart warnings and in raw audit,
            // but do NOT inflate the main "one dossier per detected character" list.
            .Where(x => x.Profile is not null || x.Candidate is not null ||
                x.Suggestions.Count > 0)
            .Select(row =>
            {
                var relations = row.Claims
                    .GroupBy(c => c.OtherKey + "\u001f" +
                        RelationshipLabelNormalizer.Normalize(c.Claim.Relationship).Key,
                        StringComparer.Ordinal)
                    .Select(group =>
                    {
                        IntelligenceRelationshipSuggestion[] claims = group.Select(x => x.Claim)
                            .DistinctBy(x => x.Id).OrderBy(x => x.RevealedAtChapter ?? int.MaxValue).ToArray();
                        Working other = records[group.First().OtherKey];
                        // Missing addressing and unknown chapter markers are not
                        // contradictions. Only two DIFFERENT non-empty claims for
                        // the same direction/field are marked as conflicting.
                        bool ConflictingAddress(int slot)
                        {
                            string Safe(string value) =>
                                RelationshipIntelligenceQualityGate.SafeAddressingDraft(
                                    RelationshipAddressingReview.VietnameseDraft(value));
                            return claims.Select(c =>
                            {
                                bool forward = row.Key ==
                                    ResolveKeyWithoutChanges(c.SpeakerChineseName, names);
                                return slot switch
                                {
                                    0 => Safe(forward ? c.SelfReference : c.ReverseSelfReference),
                                    1 => Safe(forward ? c.AddresseeReference : c.ReverseAddresseeReference),
                                    2 => Safe(forward ? c.ReverseSelfReference : c.SelfReference),
                                    _ => Safe(forward ? c.ReverseAddresseeReference : c.AddresseeReference)
                                };
                            }).Where(x => !string.IsNullOrWhiteSpace(x))
                              .Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
                        }
                        bool conflictingAddressing = Enumerable.Range(0, 4).Any(ConflictingAddress);
                        bool DifferentKnownChapter(Func<IntelligenceRelationshipSuggestion, int?> selector) =>
                            claims.Select(selector).Where(x => x.HasValue).Distinct().Skip(1).Any();
                        bool conflictingTimeline = DifferentKnownChapter(x => x.ValidFromChapter) ||
                            DifferentKnownChapter(x => x.ValidToChapter) ||
                            DifferentKnownChapter(x => x.RevealedAtChapter);
                        return new RelatedPerson(other.Key, other.Chinese,
                            string.IsNullOrWhiteSpace(other.Vietnamese) ? other.Chinese : other.Vietnamese,
                            RelationshipLabelNormalizer.Display(claims[0].Relationship),
                            claims, claims.SelectMany(x => x.EvidenceIds).Distinct().ToArray(),
                            conflictingAddressing, conflictingTimeline);
                    })
                    .OrderByDescending(x => x.PendingClaims).ThenBy(x => x.OtherVietnameseName,
                        StringComparer.Ordinal).ToArray();
                IntelligenceCharacterSuggestion? suggestion = row.Suggestions.OrderBy(x =>
                    x.Status is IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater ? 0 : 1)
                    .FirstOrDefault();
                return new Dossier(row.Key, StableId(row.Key), row.Chinese,
                    !string.IsNullOrWhiteSpace(row.Vietnamese) ? row.Vietnamese : row.Chinese,
                    row.Aliases.Order(StringComparer.Ordinal).ToArray(),
                    row.Profile?.Gender ?? suggestion?.Gender ?? CharacterGender.Unknown,
                    row.Profile?.PrimaryNarrativePronoun ?? suggestion?.PrimaryNarrativePronoun ?? "",
                    row.Profile?.SecondaryNarrativePronoun ?? suggestion?.SecondaryNarrativePronoun ?? "",
                    row.Profile is not null, row.Ambiguous, row.Suggestions.ToArray(), relations,
                    row.Evidence.ToArray());
            })
            .OrderByDescending(x => x.NeedsReview).ThenBy(x => x.VietnameseName, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ResolveKeyWithoutChanges(string name,
        Dictionary<string, HashSet<string>> index) =>
        index.TryGetValue(name.Trim(), out HashSet<string>? keys) && keys.Count == 1
            ? keys.First() : "N:" + name.Trim();

    private static Guid StableId(string key)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
