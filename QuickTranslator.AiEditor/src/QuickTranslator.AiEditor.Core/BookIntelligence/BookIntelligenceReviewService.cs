using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed record IntelligenceCommitResult(ProjectResources Resources, BookIntelligenceState State);

public sealed class BookIntelligenceReviewService(ProjectSession session)
{
    private readonly ProjectResourceStore _resources = new(session);
    private readonly BookIntelligenceStore _state = new(session);

    public async Task<IntelligenceCommitResult> AcceptCharacterAsync(BookIntelligenceState state, Guid suggestionId,
        CharacterProfile edited, long expectedResourceRevision, CancellationToken cancellationToken = default)
    {
        IntelligenceCharacterSuggestion suggestion = state.Characters.Single(x => x.Id == suggestionId);
        EnsureReviewable(suggestion.Status);
        await EnsureEvidenceCurrentAsync(state, suggestion.EvidenceIds, cancellationToken).ConfigureAwait(false);
        ProjectResources resources = await LoadExpectedAsync(expectedResourceRevision, cancellationToken).ConfigureAwait(false);
        CharacterProfile profile = CharacterProjectStore.Clone(new CharacterProject { Profiles = [edited],
            Id = resources.ProjectId, Name = "validation" }).Profiles.Single();
        profile.Validate();
        if (resources.Characters.Profiles.Any(p => p.Id != profile.Id &&
            (p.VietnameseName.Equals(profile.VietnameseName, StringComparison.OrdinalIgnoreCase) ||
             HasNameCollision(p, profile.ChineseName, profile.Aliases))))
            throw new InvalidOperationException("Tên hoặc bí danh đã thuộc nhân vật hiện có. Hãy dùng Gộp vào nhân vật đã có.");
        state = await PrepareCommitAsync(state, ResourceCommitKind.Character, suggestion.Id,
            $"character:{profile.Id:D}:{profile.ChineseName}:{profile.VietnameseName}", expectedResourceRevision, cancellationToken).ConfigureAwait(false);
        suggestion = state.Characters.Single(x => x.Id == suggestionId);
        resources.Characters.Profiles.Add(profile);
        resources.Validate();
        resources = await _resources.SaveAsync(resources, cancellationToken).ConfigureAwait(false);
        suggestion.Status = IntelligenceReviewStatus.Accepted;
        CandidateIdentity? identity = state.CandidateIdentities.SingleOrDefault(x => x.Id == suggestion.CandidateIdentityId);
        if (identity is not null) { identity.ProposedExistingCharacterId = profile.Id; identity.Status = CandidateIdentityStatus.ConfirmedIdentityMerge; }
        CompleteCommit(state, suggestion.Id, resources.Revision);
        state = await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new(resources, state);
    }

    public async Task<IntelligenceCommitResult> MergeCharacterAsync(BookIntelligenceState state, Guid suggestionId,
        Guid targetCharacterId, long expectedResourceRevision, CancellationToken cancellationToken = default)
    {
        IntelligenceCharacterSuggestion suggestion = state.Characters.Single(x => x.Id == suggestionId);
        EnsureReviewable(suggestion.Status);
        await EnsureEvidenceCurrentAsync(state, suggestion.EvidenceIds, cancellationToken).ConfigureAwait(false);
        ProjectResources resources = await LoadExpectedAsync(expectedResourceRevision, cancellationToken).ConfigureAwait(false);
        CharacterProfile target = resources.Characters.Profiles.Single(x => x.Id == targetCharacterId);
        IEnumerable<string> proposedNames = new[] { suggestion.ChineseName }.Concat(suggestion.Aliases)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal);
        var addedAliases = new List<string>();
        foreach (string name in proposedNames)
        {
            CharacterProfile? owner = resources.Characters.Profiles.FirstOrDefault(p => p.Id != target.Id &&
                (p.ChineseName == name || p.Aliases.Contains(name, StringComparer.Ordinal)));
            if (owner is not null) throw new InvalidOperationException($"Tên {name} đã thuộc {owner.VietnameseName}; không thể gộp.");
            if (string.IsNullOrWhiteSpace(target.ChineseName)) target.ChineseName = name;
            else if (target.ChineseName != name && !target.Aliases.Contains(name, StringComparer.Ordinal))
            { target.Aliases.Add(name); addedAliases.Add(name); }
        }
        target.Validate();
        state = await PrepareCommitAsync(state, ResourceCommitKind.CharacterMerge, suggestion.Id,
            $"merge:{target.Id:D}:{string.Join('|', addedAliases.Order())}", expectedResourceRevision, cancellationToken).ConfigureAwait(false);
        suggestion = state.Characters.Single(x => x.Id == suggestionId);
        resources = await _resources.SaveAsync(resources, cancellationToken).ConfigureAwait(false);
        suggestion.Status = IntelligenceReviewStatus.Merged;
        suggestion.PossibleExistingCharacterId = target.Id;
        CandidateIdentity? identity = state.CandidateIdentities.SingleOrDefault(x => x.Id == suggestion.CandidateIdentityId);
        if (identity is not null)
        {
            identity.ProposedExistingCharacterId = target.Id;
            identity.Status = CandidateIdentityStatus.ConfirmedIdentityMerge;
            identity.MergeHistory.Add(new IdentityMergeRecord
            {
                CandidateIdentityId = identity.Id, TargetCharacterId = target.Id, AddedAliases = addedAliases,
                ResourceRevisionBefore = expectedResourceRevision, ResourceRevisionAfter = resources.Revision,
                CommittedAt = DateTimeOffset.UtcNow
            });
        }
        CompleteCommit(state, suggestion.Id, resources.Revision);
        state = await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new(resources, state);
    }

    public async Task<IntelligenceCommitResult> AcceptGlossaryAsync(BookIntelligenceState state, Guid suggestionId,
        string vietnamese, long expectedResourceRevision, CancellationToken cancellationToken = default)
    {
        IntelligenceGlossarySuggestion suggestion = state.Glossary.Single(x => x.Id == suggestionId);
        EnsureReviewable(suggestion.Status);
        await EnsureEvidenceCurrentAsync(state, suggestion.EvidenceIds, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(vietnamese)) throw new ArgumentException("Bản dịch Glossary không được để trống.");
        ProjectResources resources = await LoadExpectedAsync(expectedResourceRevision, cancellationToken).ConfigureAwait(false);
        if (suggestion.Scope != GlossaryKnowledgeScope.Global || suggestion.ContextDependent)
        {
            suggestion.Vietnamese = vietnamese.Trim();
            suggestion.Status = IntelligenceReviewStatus.Accepted;
            state = await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new(resources, state);
        }
        if (resources.Characters.Profiles.Any(p => p.ChineseName == suggestion.Chinese || p.Aliases.Contains(suggestion.Chinese, StringComparer.Ordinal)))
            throw new InvalidOperationException("Cụm này thuộc hồ sơ nhân vật; không lưu thành Glossary toàn cục.");
        ConfirmedTerm? existing = resources.Terms.SingleOrDefault(x => x.Chinese == suggestion.Chinese);
        if (existing is not null && existing.Vietnamese != vietnamese.Trim())
            throw new InvalidOperationException("Glossary đã có bản dịch khác; không ghi đè chỉnh sửa của người dùng.");
        state = await PrepareCommitAsync(state, ResourceCommitKind.Glossary, suggestion.Id,
            $"glossary:{suggestion.Chinese}:{vietnamese.Trim()}", expectedResourceRevision, cancellationToken).ConfigureAwait(false);
        suggestion = state.Glossary.Single(x => x.Id == suggestionId);
        if (existing is null) resources.Terms.Add(new(suggestion.Chinese, vietnamese.Trim()));
        resources = await _resources.SaveAsync(resources, cancellationToken).ConfigureAwait(false);
        suggestion.Vietnamese = vietnamese.Trim();
        suggestion.Status = IntelligenceReviewStatus.Accepted;
        CompleteCommit(state, suggestion.Id, resources.Revision);
        state = await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new(resources, state);
    }

    public async Task<IntelligenceCommitResult> AcceptRelationshipAsync(BookIntelligenceState state, Guid suggestionId,
        long expectedResourceRevision, CancellationToken cancellationToken = default)
    {
        IntelligenceRelationshipSuggestion suggestion = state.Relationships.Single(x => x.Id == suggestionId);
        EnsureReviewable(suggestion.Status);
        RelationshipLabelNormalizer.Kind classification =
            RelationshipLabelNormalizer.Normalize(suggestion.Relationship).Classification;
        if (classification is RelationshipLabelNormalizer.Kind.Event or RelationshipLabelNormalizer.Kind.Uncertain)
            throw new InvalidOperationException(
                "Đây là sự kiện/cảm xúc chưa xác nhận, không phải quan hệ ổn định. Hãy đối chiếu nguyên tác và sửa quan hệ trước khi duyệt.");
        await EnsureEvidenceCurrentAsync(state, suggestion.EvidenceIds, cancellationToken).ConfigureAwait(false);
        ProjectResources resources = await LoadExpectedAsync(expectedResourceRevision, cancellationToken).ConfigureAwait(false);
        RelationshipLabelNormalizer.Result parsed = RelationshipLabelNormalizer.Normalize(suggestion.Relationship);
        if (parsed.Classification == RelationshipLabelNormalizer.Kind.Stable &&
            RelationshipIntelligenceQualityGate.RequiresDirection(parsed.RelationType) &&
            suggestion.ConfirmedDirection == RelationshipIntelligenceQualityGate.Direction.Unverified)
            throw new InvalidOperationException(
                "Phải xác nhận ai giữ vai trò cha/mẹ, con, thầy/trò hoặc cấp trên trước khi duyệt quan hệ. " +
                "Thứ tự người nói/người nghe do AI trả về không phải chiều quan hệ.");

        KnowledgeRelationship? knowledge = null;
        if (parsed.Classification == RelationshipLabelNormalizer.Kind.Stable)
        {
            // Stage 2: only a human-approved canonical relation may enter the
            // trusted graph. Reverse if the second named person holds the role.
            knowledge = RelationshipKnowledgeService.AddOrAggregateDirect(
                state, resources, suggestion,
                reverseDirection: suggestion.ConfirmedDirection ==
                    RelationshipIntelligenceQualityGate.Direction.ListenerHasRole);
        }
        else if (suggestion.KnowledgeRelationshipId is Guid legacyId)
        {
            // Backwards compatibility: an explicitly human-approved, non-directional
            // legacy graph fact may still be confirmed. Never promote directional
            // old AI facts without verified role order.
            KnowledgeRelationship? legacy = state.RelationshipGraph.SingleOrDefault(x => x.Id == legacyId);
            if (legacy is not null &&
                !RelationshipIntelligenceQualityGate.RequiresDirection(legacy.RelationType))
                knowledge = legacy;
        }
        if (knowledge is not null)
        {
            suggestion.ValidFromChapter ??= knowledge.ValidFromChapter;
            suggestion.ValidToChapter ??= knowledge.ValidToChapter;
            suggestion.RevealedAtChapter ??= knowledge.RevealedAtChapter;
            knowledge.Status = KnowledgeRelationshipStatus.Confirmed;
            knowledge.DiscoveryMethod = RelationshipDiscoveryMethod.UserConfirmed;
            knowledge.ValidFromChapter = suggestion.ValidFromChapter;
            knowledge.ValidToChapter = suggestion.ValidToChapter;
            knowledge.RevealedAtChapter = suggestion.RevealedAtChapter;
        }
        if (!RelationshipIntelligenceQualityGate.MayInstallAddressing(
            suggestion.SelfReference, suggestion.AddresseeReference))
        {
            suggestion.Status = IntelligenceReviewStatus.Accepted;
            state = await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            _ = new KinshipInferenceEngine().Recalculate(state);
            state = await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new(resources, state);
        }
        CharacterProfile speaker = FindCharacter(resources, suggestion.SpeakerChineseName);
        CharacterProfile listener = FindCharacter(resources, suggestion.ListenerChineseName);
        CharacterRelationship? existing = speaker.Relationships.SingleOrDefault(x => x.EffectiveListenerCharacterId == listener.Id);
        if (existing is not null)
            throw new InvalidOperationException(existing.Locked
                ? "Quan hệ hiện có đang khóa; AI không được ghi đè."
                : "Quan hệ đã được người dùng chỉnh; hãy mở hồ sơ nhân vật để đối chiếu, không ghi đè tự động.");
        state = await PrepareCommitAsync(state, ResourceCommitKind.Relationship, suggestion.Id,
            $"relationship:{speaker.Id:D}:{listener.Id:D}:{suggestion.SelfReference}:{suggestion.AddresseeReference}",
            expectedResourceRevision, cancellationToken).ConfigureAwait(false);
        suggestion = state.Relationships.Single(x => x.Id == suggestionId);
        speaker.Relationships.Add(new CharacterRelationship
        {
            TargetCharacterId = listener.Id, SpeakerCharacterId = speaker.Id, ListenerCharacterId = listener.Id,
            Relationship = suggestion.Relationship, SelfReference = suggestion.SelfReference,
            AddresseeReference = suggestion.AddresseeReference, Enabled = true, Locked = false,
            AddressingNotes = "Đã xác nhận từ AI Book Intelligence: " + suggestion.Reason
        });
        if (RelationshipIntelligenceQualityGate.MayInstallAddressing(
            suggestion.ReverseSelfReference, suggestion.ReverseAddresseeReference))
        {
            CharacterRelationship? reverse = listener.Relationships.SingleOrDefault(x => x.EffectiveListenerCharacterId == speaker.Id);
            if (reverse is not null)
                throw new InvalidOperationException(reverse.Locked
                    ? "Quan hệ chiều ngược đang khóa; giữ nguyên và không ghi đè."
                    : "Quan hệ chiều ngược đã được người dùng chỉnh; không ghi đè tự động.");
            listener.Relationships.Add(new CharacterRelationship
            {
                TargetCharacterId = speaker.Id, SpeakerCharacterId = listener.Id, ListenerCharacterId = speaker.Id,
                Relationship = suggestion.Relationship, SelfReference = suggestion.ReverseSelfReference,
                AddresseeReference = suggestion.ReverseAddresseeReference, Enabled = true, Locked = false,
                AddressingNotes = "Đã xác nhận chiều ngược từ AI Book Intelligence: " + suggestion.Reason
            });
        }
        resources.Validate();
        resources = await _resources.SaveAsync(resources, cancellationToken).ConfigureAwait(false);
        suggestion.Status = IntelligenceReviewStatus.Accepted;
        AddApprovedAddressingPeriods(state, suggestion, knowledge, speaker.Id, listener.Id);
        CompleteCommit(state, suggestion.Id, resources.Revision);
        _ = new KinshipInferenceEngine().Recalculate(state);
        state = await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new(resources, state);
    }

    public async Task<IntelligenceCommitResult> AcceptDerivedRelationshipAsync(BookIntelligenceState state, Guid derivedId,
        CancellationToken cancellationToken = default)
    {
        DerivedRelationship derived = state.DerivedRelationships.Single(x => x.Id == derivedId);
        if (derived.ValidityStatus != InferenceValidityStatus.Valid || derived.Status != KnowledgeRelationshipStatus.DerivedAwaitingReview)
            throw new InvalidOperationException("Quan hệ suy luận đang mơ hồ, stale, xung đột hoặc đã được xử lý.");
        await EnsureEvidenceCurrentAsync(state, derived.SupportingEvidenceIds, cancellationToken).ConfigureAwait(false);
        derived.Status = KnowledgeRelationshipStatus.Accepted;
        KnowledgeRelationship[] inputs = state.RelationshipGraph.Where(x => derived.InputRelationIds.Contains(x.Id)).ToArray();
        state.RelationshipGraph.Add(new KnowledgeRelationship
        {
            SubjectCharacterId = derived.SubjectCharacterId, TargetCharacterId = derived.TargetCharacterId,
            RelationType = derived.OutputRelation, RelationQualifier = derived.Explanation,
            OriginalDescription = derived.Explanation, EvidenceIds = derived.SupportingEvidenceIds.ToList(),
            DiscoveryMethod = RelationshipDiscoveryMethod.CpuInference, Status = KnowledgeRelationshipStatus.Accepted,
            FirstObservedChapter = inputs.Select(x => x.FirstObservedChapter).Where(x => x is not null).DefaultIfEmpty().Max(),
            RevealedAtChapter = inputs.Select(x => x.RevealedAtChapter).Where(x => x is not null).DefaultIfEmpty().Max()
        });
        state = await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new(await _resources.LoadAsync(cancellationToken).ConfigureAwait(false), state);
    }

    public async Task<IntelligenceCommitResult> UndoCharacterMergeAsync(BookIntelligenceState state, Guid mergeId,
        long expectedResourceRevision, CancellationToken cancellationToken = default)
    {
        CandidateIdentity identity = state.CandidateIdentities.Single(x => x.MergeHistory.Any(m => m.Id == mergeId));
        IdentityMergeRecord merge = identity.MergeHistory.Single(x => x.Id == mergeId);
        if (merge.UndoneAt is not null) throw new InvalidOperationException("Lần gộp này đã được hoàn tác.");
        ProjectResources resources = await LoadExpectedAsync(expectedResourceRevision, cancellationToken).ConfigureAwait(false);
        if (resources.Revision != merge.ResourceRevisionAfter)
            throw new InvalidOperationException("Characters đã đổi sau lần gộp; mở hồ sơ để hoàn tác thủ công, tránh xóa nhầm chỉnh sửa mới.");
        CharacterProfile target = resources.Characters.Profiles.Single(x => x.Id == merge.TargetCharacterId);
        if (merge.AddedAliases.Any(a => !target.Aliases.Contains(a, StringComparer.Ordinal)))
            throw new InvalidOperationException("Bí danh đã đổi sau lần gộp; không thể tự hoàn tác an toàn.");
        target.Aliases.RemoveAll(merge.AddedAliases.Contains);
        resources = await _resources.SaveAsync(resources, cancellationToken).ConfigureAwait(false);
        merge.UndoneAt = DateTimeOffset.UtcNow;
        identity.Status = CandidateIdentityStatus.Candidate;
        IntelligenceCharacterSuggestion? suggestion = state.Characters.SingleOrDefault(x => x.CandidateIdentityId == identity.Id);
        if (suggestion is not null) suggestion.Status = IntelligenceReviewStatus.ReviewLater;
        state = await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new(resources, state);
    }

    public async Task<IntelligenceCommitResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        BookIntelligenceState state = await _state.LoadAsync(cancellationToken).ConfigureAwait(false);
        ProjectResources resources = await _resources.LoadAsync(cancellationToken).ConfigureAwait(false);
        bool changed = false;
        foreach (IntelligenceResourceCommit marker in state.PendingResourceCommits.ToArray())
        {
            if (!IsApplied(marker, state, resources)) continue;
            MarkSuggestionAccepted(state, marker.SuggestionId, marker.Kind);
            if (marker.Kind == ResourceCommitKind.Relationship)
            {
                IntelligenceRelationshipSuggestion suggestion = state.Relationships.Single(x => x.Id == marker.SuggestionId);
                CharacterProfile speaker = FindCharacter(resources, suggestion.SpeakerChineseName);
                CharacterProfile listener = FindCharacter(resources, suggestion.ListenerChineseName);
                KnowledgeRelationship? knowledge = suggestion.KnowledgeRelationshipId is Guid knowledgeId
                    ? state.RelationshipGraph.SingleOrDefault(x => x.Id == knowledgeId) : null;
                if (knowledge is not null)
                {
                    suggestion.ValidFromChapter ??= knowledge.ValidFromChapter;
                    suggestion.ValidToChapter ??= knowledge.ValidToChapter;
                    suggestion.RevealedAtChapter ??= knowledge.RevealedAtChapter;
                    knowledge.Status = KnowledgeRelationshipStatus.Confirmed;
                    knowledge.ValidFromChapter = suggestion.ValidFromChapter;
                    knowledge.ValidToChapter = suggestion.ValidToChapter;
                    knowledge.RevealedAtChapter = suggestion.RevealedAtChapter;
                }
                AddApprovedAddressingPeriods(state, suggestion, knowledge, speaker.Id, listener.Id);
            }
            state.PendingResourceCommits.Remove(marker); changed = true;
        }
        if (changed) state = await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new(resources, state);
    }

    public async Task<BookIntelligenceState> SetReviewStatusAsync(BookIntelligenceState state, Guid id,
        IntelligenceReviewStatus status, CancellationToken cancellationToken = default)
    {
        if (status is IntelligenceReviewStatus.Accepted or IntelligenceReviewStatus.Merged)
            throw new ArgumentException("Trạng thái lưu/gộp chỉ được đặt sau khi Project Resources commit thành công.");
        object? item = state.Characters.Cast<object>().Concat(state.Relationships).Concat(state.Glossary)
            .SingleOrDefault(x => x switch
            {
                IntelligenceCharacterSuggestion c => c.Id == id,
                IntelligenceRelationshipSuggestion r => r.Id == id,
                IntelligenceGlossarySuggestion g => g.Id == id,
                _ => false
            });
        if (item is null)
        {
            DerivedRelationship derived = state.DerivedRelationships.Single(x => x.Id == id);
            derived.Status = status switch
            {
                IntelligenceReviewStatus.Rejected => KnowledgeRelationshipStatus.Rejected,
                IntelligenceReviewStatus.ReviewLater => KnowledgeRelationshipStatus.DerivedAwaitingReview,
                _ => throw new ArgumentException("Trạng thái không hợp lệ cho quan hệ suy luận.")
            };
            return await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        switch (item)
        {
            case IntelligenceCharacterSuggestion c: c.Status = status; break;
            case IntelligenceRelationshipSuggestion r: r.Status = status; break;
            case IntelligenceGlossarySuggestion g: g.Status = status; break;
        }
        return await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProjectResources> LoadExpectedAsync(long expectedRevision, CancellationToken cancellationToken)
    {
        ProjectResources resources = await _resources.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (resources.Revision != expectedRevision)
            throw new InvalidOperationException("Characters/Glossary đã đổi sau khi mở hàng chờ. Hãy nạp lại đề xuất trước khi lưu.");
        return resources;
    }

    private async Task<BookIntelligenceState> PrepareCommitAsync(BookIntelligenceState state, ResourceCommitKind kind,
        Guid suggestionId, string fingerprint, long expectedRevision, CancellationToken cancellationToken)
    {
        state.PendingResourceCommits.RemoveAll(x => x.SuggestionId == suggestionId);
        state.PendingResourceCommits.Add(new IntelligenceResourceCommit
        {
            Kind = kind, State = ResourceCommitState.Prepared, SuggestionId = suggestionId,
            IntendedFingerprint = fingerprint, ExpectedResourceRevision = expectedRevision, CreatedAt = DateTimeOffset.UtcNow
        });
        return await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private static void CompleteCommit(BookIntelligenceState state, Guid suggestionId, long resourceRevision)
    {
        IntelligenceResourceCommit marker = state.PendingResourceCommits.Single(x => x.SuggestionId == suggestionId);
        marker.State = ResourceCommitState.ResourceCommitted; marker.CommittedResourceRevision = resourceRevision;
        state.PendingResourceCommits.Remove(marker);
    }

    private static bool IsApplied(IntelligenceResourceCommit marker, BookIntelligenceState state, ProjectResources resources)
    {
        string[] parts = marker.IntendedFingerprint.Split(':');
        return marker.Kind switch
        {
            ResourceCommitKind.Character when parts.Length >= 4 && Guid.TryParse(parts[1], out Guid id) =>
                resources.Characters.Profiles.Any(x => x.Id == id),
            ResourceCommitKind.CharacterMerge when parts.Length >= 3 && Guid.TryParse(parts[1], out Guid target) =>
                resources.Characters.Profiles.Any(x => x.Id == target && parts[2].Split('|', StringSplitOptions.RemoveEmptyEntries).All(a => x.Aliases.Contains(a))),
            ResourceCommitKind.Glossary when parts.Length >= 3 => resources.Terms.Any(x => x.Chinese == parts[1] && x.Vietnamese == parts[2]),
            ResourceCommitKind.Relationship when parts.Length >= 5 && Guid.TryParse(parts[1], out Guid speakerId) &&
                Guid.TryParse(parts[2], out Guid listenerId) => resources.Characters.Profiles.Any(p => p.Id == speakerId &&
                    p.Relationships.Any(r => r.Enabled && r.EffectiveListenerCharacterId == listenerId &&
                        r.SelfReference == parts[3] && r.AddresseeReference == parts[4])),
            _ => false
        };
    }

    private static void AddApprovedAddressingPeriods(BookIntelligenceState state,
        IntelligenceRelationshipSuggestion suggestion, KnowledgeRelationship? knowledge,
        Guid speakerId, Guid listenerId)
    {
        if (string.IsNullOrWhiteSpace(suggestion.SelfReference) || string.IsNullOrWhiteSpace(suggestion.AddresseeReference)) return;
        int? observed = FirstEvidenceChapter(state, suggestion.EvidenceIds);
        AddAddressingPeriod(state, speakerId, listenerId, suggestion.SelfReference, suggestion.AddresseeReference,
            suggestion.ValidFromChapter ?? knowledge?.ValidFromChapter ?? suggestion.RevealedAtChapter ?? knowledge?.RevealedAtChapter ?? observed,
            suggestion.ValidToChapter ?? knowledge?.ValidToChapter,
            suggestion.RevealedAtChapter ?? knowledge?.RevealedAtChapter ?? observed, suggestion.EvidenceIds);
        if (!string.IsNullOrWhiteSpace(suggestion.ReverseSelfReference) &&
            !string.IsNullOrWhiteSpace(suggestion.ReverseAddresseeReference))
            AddAddressingPeriod(state, listenerId, speakerId, suggestion.ReverseSelfReference,
                suggestion.ReverseAddresseeReference,
                suggestion.ValidFromChapter ?? knowledge?.ValidFromChapter ?? suggestion.RevealedAtChapter ?? knowledge?.RevealedAtChapter ?? observed,
                suggestion.ValidToChapter ?? knowledge?.ValidToChapter,
                suggestion.RevealedAtChapter ?? knowledge?.RevealedAtChapter ?? observed, suggestion.EvidenceIds);
    }

    private static void AddAddressingPeriod(BookIntelligenceState state, Guid speakerId, Guid listenerId,
        string selfReference, string addresseeReference, int? validFrom, int? validTo, int? revealedAt,
        IEnumerable<Guid> evidenceIds)
    {
        if (state.AddressingTimeline.Any(x => x.SpeakerCharacterId == speakerId && x.ListenerCharacterId == listenerId &&
            x.SelfReference == selfReference && x.AddresseeReference == addresseeReference &&
            x.ValidFromChapter == validFrom && x.ValidToChapter == validTo && x.RevealedAtChapter == revealedAt)) return;
        state.AddressingTimeline.Add(new AddressingRulePeriod
        {
            SpeakerCharacterId = speakerId, ListenerCharacterId = listenerId,
            SelfReference = selfReference, AddresseeReference = addresseeReference,
            ValidFromChapter = validFrom, ValidToChapter = validTo, RevealedAtChapter = revealedAt,
            Approved = true, Locked = false, EvidenceIds = evidenceIds.Distinct().ToList()
        });
    }

    private static int? FirstEvidenceChapter(BookIntelligenceState state, IEnumerable<Guid> evidenceIds)
    {
        HashSet<Guid> ids = evidenceIds.ToHashSet();
        return state.Chapters.Where(c => c.Evidence.Any(e => ids.Contains(e.Id)))
            .Select(c => (int?)c.ChapterNumber).OrderBy(x => x).FirstOrDefault();
    }

    private static void MarkSuggestionAccepted(BookIntelligenceState state, Guid id, ResourceCommitKind kind)
    {
        if (kind is ResourceCommitKind.Character or ResourceCommitKind.CharacterMerge)
            state.Characters.Single(x => x.Id == id).Status = kind == ResourceCommitKind.CharacterMerge ? IntelligenceReviewStatus.Merged : IntelligenceReviewStatus.Accepted;
        else if (kind == ResourceCommitKind.Glossary) state.Glossary.Single(x => x.Id == id).Status = IntelligenceReviewStatus.Accepted;
        else state.Relationships.Single(x => x.Id == id).Status = IntelligenceReviewStatus.Accepted;
    }
    private async Task EnsureEvidenceCurrentAsync(BookIntelligenceState state, IEnumerable<Guid> evidenceIds,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, CorpusEvidence> evidence = state.Evidence.ToDictionary(x => x.Id);
        CorpusEvidence[] selected = evidenceIds.Distinct().Select(id => evidence.TryGetValue(id, out CorpusEvidence? item)
            ? item : throw new InvalidDataException("Đề xuất tham chiếu chứng cứ không còn trong Knowledge Base.")).ToArray();
        if (selected.Length == 0 || selected.Any(x => !x.IsCurrent))
            throw new InvalidOperationException("Nguyên tác đã đổi sau khi phân tích. Hãy quét lại trước khi lưu đề xuất này.");
        var repository = new ProjectRepository();
        foreach (IGrouping<Guid, CorpusEvidence> chapterEvidence in selected.GroupBy(x => x.ChapterId))
        {
            ChapterDocument chapter = (await repository.LoadChapterAsync(session, chapterEvidence.Key, cancellationToken)
                .ConfigureAwait(false)).Chapter;
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chapter.OriginalText ?? "")));
            if (chapterEvidence.Any(x => !string.Equals(x.SourceSha256, hash, StringComparison.Ordinal)))
                throw new InvalidOperationException("Nguyên tác đã đổi sau khi phân tích. Hãy quét lại trước khi lưu đề xuất này.");
        }
    }
    private static CharacterProfile FindCharacter(ProjectResources resources, string chinese) =>
        resources.Characters.Profiles.SingleOrDefault(p => p.ChineseName == chinese || p.Aliases.Contains(chinese, StringComparer.Ordinal))
        ?? throw new InvalidOperationException($"Chưa có hồ sơ đã xác nhận cho {chinese}.");
    private static bool HasNameCollision(CharacterProfile existing, string chinese, IEnumerable<string> aliases) =>
        (!string.IsNullOrWhiteSpace(chinese) && (existing.ChineseName == chinese || existing.Aliases.Contains(chinese, StringComparer.Ordinal))) ||
        aliases.Any(alias => existing.ChineseName == alias || existing.Aliases.Contains(alias, StringComparer.Ordinal));
    private static void EnsureReviewable(IntelligenceReviewStatus status)
    {
        if (status is not (IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater))
            throw new InvalidOperationException("Đề xuất này đã được xử lý.");
    }
}
