using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public enum IntelligenceEvidenceKind { Character, Relationship, Term, Mixed }
public enum IntelligenceReviewStatus { Pending, ReviewLater, Accepted, Merged, Rejected }
public enum IntelligenceConfidence { Unknown, Ambiguous, Proposed }

public sealed class CorpusEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChapterId { get; set; }
    public int StartUtf16 { get; set; }
    public int LengthUtf16 { get; set; }
    public string SourceSha256 { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public IntelligenceEvidenceKind Kind { get; set; }
    public bool IsCurrent { get; set; } = true;
    public List<string> Signals { get; set; } = [];
    public EvidencePriority Priority { get; set; }
    public string PriorityReason { get; set; } = "";
    public string SemanticGroup { get; set; } = "general";
    public bool IsAuditSample { get; set; }
    public bool AuditExpanded { get; set; }
    public bool AiAnalyzed { get; set; }
    // Explicit Google provider block: this excerpt was NOT analyzed or approved.
    // Keep it distinct from AiAnalyzed; Resume skips it and a deliberate resurvey
    // resets the flag. No sensitive response content is persisted.
    public bool AiBlocked { get; set; }
    public string AiBlockReason { get; set; } = "";
    public bool ImportantFinding { get; set; }
    public List<string> CandidateNames { get; set; } = [];
}

public sealed class CorpusChapterIndex
{
    public Guid ChapterId { get; set; }
    public int ChapterNumber { get; set; }
    public string SourceSha256 { get; set; } = "";
    public int SourceLengthUtf16 { get; set; }
    public DateTimeOffset ScannedAt { get; set; }
    public List<CorpusEvidence> Evidence { get; set; } = [];
}

public sealed class IntelligenceCharacterSuggestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ChineseName { get; set; } = "";
    public string VietnameseName { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public CharacterGender Gender { get; set; }
    public string PrimaryNarrativePronoun { get; set; } = "";
    public string SecondaryNarrativePronoun { get; set; } = "";
    public IntelligenceConfidence Confidence { get; set; }
    public IntelligenceReviewStatus Status { get; set; }
    public List<Guid> EvidenceIds { get; set; } = [];
    public string Reason { get; set; } = "";
    public Guid? PossibleExistingCharacterId { get; set; }
    public Guid CandidateIdentityId { get; set; }
}

public sealed class IntelligenceRelationshipSuggestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SpeakerChineseName { get; set; } = "";
    public string ListenerChineseName { get; set; } = "";
    public string Relationship { get; set; } = "";
    // Explicit human verification of the direction of this relationship.
    // Default is Unverified for ALL previously saved projects.
    public RelationshipIntelligenceQualityGate.Direction ConfirmedDirection { get; set; }
    public string SelfReference { get; set; } = "";
    public string AddresseeReference { get; set; } = "";
    public string ReverseSelfReference { get; set; } = "";
    public string ReverseAddresseeReference { get; set; } = "";
    public int? ValidFromChapter { get; set; }
    public int? ValidToChapter { get; set; }
    public int? RevealedAtChapter { get; set; }
    public IntelligenceConfidence Confidence { get; set; }
    public IntelligenceReviewStatus Status { get; set; }
    public List<Guid> EvidenceIds { get; set; } = [];
    public string Reason { get; set; } = "";
    public Guid? KnowledgeRelationshipId { get; set; }
}

public sealed class IntelligenceGlossarySuggestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Chinese { get; set; } = "";
    public string Vietnamese { get; set; } = "";
    public string Category { get; set; } = "";
    public bool ContextDependent { get; set; }
    public IntelligenceConfidence Confidence { get; set; }
    public IntelligenceReviewStatus Status { get; set; }
    public List<Guid> EvidenceIds { get; set; } = [];
    public string Reason { get; set; } = "";
    public GlossaryKnowledgeCategory KnowledgeCategory { get; set; }
    public GlossaryKnowledgeScope Scope { get; set; }
}

public sealed class BookIntelligenceState
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid ProjectId { get; set; }
    public long Revision { get; set; }
    public bool AutoApplyVerified { get; set; }
    public string AnalyzerModel { get; set; } = "translategemma:12b";
    public Guid? AnalyzerProfileId { get; set; }
    public string AnalyzerVersion { get; set; } = "1";
    public IntelligenceScanMode ScanMode { get; set; } = IntelligenceScanMode.FastAdaptive;
    public double LowPriorityAuditRate { get; set; } = 0.15;
    public int AnalysisConcurrency { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
    public int NextEvidenceIndex { get; set; }
    public string LastError { get; set; } = "";
    public List<CorpusChapterIndex> Chapters { get; set; } = [];
    public HashSet<string> CompletedAnalysisKeys { get; set; } = [];
    public List<IntelligenceCharacterSuggestion> Characters { get; set; } = [];
    public List<IntelligenceRelationshipSuggestion> Relationships { get; set; } = [];
    public List<IntelligenceGlossarySuggestion> Glossary { get; set; } = [];
    public List<CandidateIdentity> CandidateIdentities { get; set; } = [];
    public List<KnowledgeRelationship> RelationshipGraph { get; set; } = [];
    public List<DerivedRelationship> DerivedRelationships { get; set; } = [];
    public List<AddressingRulePeriod> AddressingTimeline { get; set; } = [];
    public List<SceneContextSnapshot> SceneContexts { get; set; } = [];
    public List<IntelligenceResourceCommit> PendingResourceCommits { get; set; } = [];
    public IntelligenceAnalysisMetrics Metrics { get; set; } = new();

    public IEnumerable<CorpusEvidence> Evidence => Chapters.OrderBy(c => c.ChapterNumber).SelectMany(c => c.Evidence.OrderBy(e => e.StartUtf16));
    public int PendingCount => Characters.Count(x => x.Status is IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater) +
        Relationships.Count(x => x.Status is IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater) +
        Glossary.Count(x => x.Status is IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater);

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || ProjectId == Guid.Empty || Revision < 0 ||
            string.IsNullOrWhiteSpace(AnalyzerModel) || AnalyzerModel.Length > 200 || Chapters is null ||
            CompletedAnalysisKeys is null || Characters is null || Relationships is null || Glossary is null ||
            CandidateIdentities is null || RelationshipGraph is null || DerivedRelationships is null ||
            AddressingTimeline is null || SceneContexts is null || PendingResourceCommits is null || Metrics is null ||
            LowPriorityAuditRate is < 0 or > 1 || AnalysisConcurrency is < 1 or > 8 || !Enum.IsDefined(ScanMode))
            throw new InvalidDataException("Dữ liệu AI Book Intelligence không hợp lệ.");
        if (AutoApplyVerified) throw new InvalidDataException("Phiên 1 không cho phép tự áp dụng đề xuất AI.");
        if (Chapters.GroupBy(x => x.ChapterId).Any(x => x.Count() > 1) || Chapters.Any(c => c.ChapterId == Guid.Empty ||
            string.IsNullOrWhiteSpace(c.SourceSha256) || c.SourceLengthUtf16 < 0 || c.Evidence is null ||
            c.Evidence.Any(e => e.Id == Guid.Empty || e.ChapterId != c.ChapterId || e.StartUtf16 < 0 || e.LengthUtf16 < 0 ||
                (e.IsCurrent && e.StartUtf16 + e.LengthUtf16 > c.SourceLengthUtf16) || string.IsNullOrWhiteSpace(e.SourceSha256) || e.Excerpt is null)))
            throw new InvalidDataException("Chỉ mục corpus không hợp lệ.");
        Guid[] evidenceIds = Evidence.Select(x => x.Id).ToArray();
        if (evidenceIds.Distinct().Count() != evidenceIds.Length) throw new InvalidDataException("EvidenceId bị trùng.");
        var evidenceSet = evidenceIds.ToHashSet();
        if (Characters.Concat<object>(Relationships).Concat(Glossary).Any(x => x is null))
            throw new InvalidDataException("Hàng chờ duyệt chứa mục rỗng.");
        if (Characters.Any(x => x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.ChineseName) ||
            x.EvidenceIds.Count == 0 || x.EvidenceIds.Any(id => !evidenceSet.Contains(id))) ||
            Relationships.Any(x => x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.SpeakerChineseName) ||
                string.IsNullOrWhiteSpace(x.ListenerChineseName) || x.EvidenceIds.Count == 0 || x.EvidenceIds.Any(id => !evidenceSet.Contains(id))) ||
            Glossary.Any(x => x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Chinese) ||
                x.EvidenceIds.Count == 0 || x.EvidenceIds.Any(id => !evidenceSet.Contains(id))))
            throw new InvalidDataException("Đề xuất AI thiếu định danh hoặc chứng cứ hợp lệ.");
        if (CandidateIdentities.Any(x => x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.ChineseName) ||
                x.EvidenceHistory.Any(id => !evidenceSet.Contains(id))) ||
            RelationshipGraph.Any(x => x.Id == Guid.Empty || x.SubjectCharacterId == Guid.Empty ||
                x.TargetCharacterId == Guid.Empty || x.SubjectCharacterId == x.TargetCharacterId ||
                x.EvidenceIds.Any(id => !evidenceSet.Contains(id))) ||
            DerivedRelationships.Any(x => x.Id == Guid.Empty || x.SubjectCharacterId == Guid.Empty ||
                x.TargetCharacterId == Guid.Empty || x.InferenceDepth is < 1 or > 2 ||
                x.InputRelationIds.Count == 0 || x.SupportingEvidenceIds.Any(id => !evidenceSet.Contains(id))))
            throw new InvalidDataException("Knowledge Base AI Book Intelligence không hợp lệ.");
    }
}

public sealed record CorpusIndexProgress(int ProcessedChapters, int TotalChapters, int RescannedChapters, int EvidenceCount);
public sealed record AnalyzerProgress(int ProcessedEvidence, int TotalEvidence, int PendingSuggestions);
