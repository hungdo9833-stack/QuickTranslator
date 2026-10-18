using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed class AdaptiveBookIntelligenceAnalyzer(IOllamaClient client)
{
    public const int MaximumEvidencePerRequest = 8;
    public const string PromptSchemaVersion = "abi-3-evidence-quality";
    internal static JsonElement ResponseSchema { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type":"object",
          "additionalProperties":false,
          "properties":{
            "characters":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
              "chinese_name":{"type":"string"},"vietnamese_name":{"type":"string"},
              "aliases":{"type":"array","items":{"type":"string"}},
              "gender":{"type":"string","enum":["unknown","male","female","other"]},
              "primary_pronoun":{"type":"string"},"secondary_pronoun":{"type":"string"},
              "confidence":{"type":"string","enum":["unknown","ambiguous","proposed"]},
              "evidence_ids":{"type":"array","items":{"type":"string"}},"reason":{"type":"string"}
            },"required":["chinese_name","vietnamese_name","aliases","gender","primary_pronoun","secondary_pronoun","confidence","evidence_ids","reason"]}},
            "relationships":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
              "speaker_chinese_name":{"type":"string"},"listener_chinese_name":{"type":"string"},
              "relationship":{"type":"string"},"self_reference":{"type":"string"},
              "addressee_reference":{"type":"string"},"reverse_self_reference":{"type":"string"},
              "reverse_addressee_reference":{"type":"string"},
              "confidence":{"type":"string","enum":["unknown","ambiguous","proposed"]},
              "evidence_ids":{"type":"array","items":{"type":"string"}},"reason":{"type":"string"}
            },"required":["speaker_chinese_name","listener_chinese_name","relationship","self_reference","addressee_reference","reverse_self_reference","reverse_addressee_reference","confidence","evidence_ids","reason"]}},
            "glossary":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
              "chinese":{"type":"string"},"vietnamese":{"type":"string"},
              "category":{"type":"string","enum":["place","organization","clan","cultivation","item","realm","title","term","addressing"]},
              "context_dependent":{"type":"boolean"},
              "confidence":{"type":"string","enum":["unknown","ambiguous","proposed"]},
              "evidence_ids":{"type":"array","items":{"type":"string"}},"reason":{"type":"string"}
            },"required":["chinese","vietnamese","category","context_dependent","confidence","evidence_ids","reason"]}}
          },
          "required":["characters","relationships","glossary"]
        }
        """);

    public async Task<BookIntelligenceState> AnalyzeAsync(ProjectSession session, BookIntelligenceState state,
        BookIntelligenceStore store, ProjectResources resources, ModelOptions options,
        IProgress<AnalyzerProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(resources);
        options.Validate();
        if (state.ProjectId != session.Manifest.ProjectId || resources.ProjectId != session.Manifest.ProjectId)
            throw new InvalidOperationException("Analyzer nhận dữ liệu của Project khác.");

        state.AnalyzerModel = options.Model;
        state.AnalyzerVersion = "2";
        int total = Eligible(state).Count();
        int processed = state.Evidence.Count(x => x.IsCurrent && (x.AiAnalyzed || x.AiBlocked));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CorpusEvidence[] pending = Eligible(state).Where(x => !x.AiAnalyzed && !x.AiBlocked)
                .OrderByDescending(x => x.Priority).ThenBy(x => ChapterNumber(state, x.ChapterId))
                .ThenBy(x => x.StartUtf16).ToArray();
            if (pending.Length == 0) break;
            var remaining = pending.ToList();
            var work = new List<BatchWork>();
            var scheduledKeys = new HashSet<string>(StringComparer.Ordinal);
            while (work.Count < state.AnalysisConcurrency && remaining.Count > 0)
            {
                CorpusEvidence[] batch = client is IAdaptiveSurveyBatchClient adaptive
                    ? TakeAdaptiveBatch(remaining.ToArray(), options, adaptive.TargetInputTokens)
                    : TakeBatch(remaining.ToArray(), options.ContextSize);
                var batchIds = batch.Select(x => x.Id).ToHashSet();
                remaining.RemoveAll(x => batchIds.Contains(x.Id));
                string relevantContext = RelevantContextFingerprint(batch, resources, state);
                string cacheModel = client is IAdaptiveSurveyBatchClient pool
                    ? pool.CacheSignature : options.Model;
                string key = AnalysisKey(batch, cacheModel, PromptSchemaVersion, relevantContext);
                if (state.CompletedAnalysisKeys.Contains(key))
                {
                    foreach (CorpusEvidence evidence in batch) evidence.AiAnalyzed = true;
                    state.Metrics.CacheHits++;
                    processed += batch.Length;
                    state.NextEvidenceIndex = processed;
                    state = await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new(processed, Math.Max(total, Eligible(state).Count()), state.PendingCount));
                    continue;
                }
                if (!scheduledKeys.Add(key)) continue;
                work.Add(new(batch, key, BuildPrompt(batch, resources, state)));
            }
            if (work.Count == 0) continue;

            // Workers only perform remote requests and parse into detached response objects.
            // The coordinator below is the sole writer of BookIntelligenceState and its store.
            BatchResult[] results = await Task.WhenAll(work.Select(item =>
                AnalyzeBatchAsync(item, options, cancellationToken))).ConfigureAwait(false);
            Exception? failure = null;
            foreach (BatchResult result in results)
            {
                state.Metrics.Requests += result.Requests;
                state.Metrics.Retries += result.Retries;
                if (result.Error is GeminiSurveyBlockedException blocked)
                {
                    // Respect explicit provider refusal: don't retry, route elsewhere,
                    // count as analyzed, or erase evidence. Persist and continue other batches.
                    foreach (CorpusEvidence item in result.Work.Evidence)
                    {
                        CorpusEvidence source = state.Evidence.Single(x => x.Id == item.Id);
                        source.AiBlocked = true;
                        source.AiBlockReason = blocked.ReasonCode;
                    }
                    state.Metrics.Errors++;
                    processed += result.Work.Evidence.Length;
                    state.NextEvidenceIndex = processed;
                    state = await store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                    progress?.Report(new(processed, Math.Max(total, Eligible(state).Count()), state.PendingCount));
                    continue;
                }
                if (result.Error is not null)
                {
                    state.Metrics.Errors++;
                    failure ??= result.Error;
                    continue;
                }

                CorpusEvidence[] batch = result.Work.Evidence.Select(item =>
                    state.Evidence.Single(current => current.Id == item.Id)).ToArray();
                OllamaCompletion completion = result.Completion!;
                AnalyzerResponse response = result.Response!;
                HashSet<Guid> referenced = response.Characters.SelectMany(x => x.EvidenceIds)
                    .Concat(response.Relationships.SelectMany(x => x.EvidenceIds))
                    .Concat(response.Glossary.SelectMany(x => x.EvidenceIds)).ToHashSet();
                ApplyValidatedResponse(state, response, batch, resources);
                bool auditDiscovery = batch.Any(x => x.Priority == EvidencePriority.Low && x.IsAuditSample && referenced.Contains(x.Id));
                foreach (CorpusEvidence evidence in batch)
                {
                    evidence.AiAnalyzed = true;
                    evidence.ImportantFinding = referenced.Contains(evidence.Id);
                }
                if (auditDiscovery)
                {
                    state.Metrics.AuditDiscoveries++;
                    ExpandAuditScope(state, batch.Where(x => x.Priority == EvidencePriority.Low && referenced.Contains(x.Id)));
                    total = Eligible(state).Count();
                }
                state.Metrics.InputTokens += completion.Metrics.InputTokens ?? 0;
                state.Metrics.OutputTokens += completion.Metrics.OutputTokens ?? 0;
                state.Metrics.AiDurationMilliseconds += (long)completion.Metrics.ClientElapsed.TotalMilliseconds;
                state.Metrics.EvidenceAnalyzed += batch.Length;
                state.CompletedAnalysisKeys.Add(result.Work.Key);
                state.LastError = "";
                processed += batch.Length;
                state.NextEvidenceIndex = processed;
                // Persist successful batches one at a time, even if a sibling failed.
                state = await store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                progress?.Report(new(processed, Math.Max(total, Eligible(state).Count()), state.PendingCount));
            }
            if (failure is not null)
            {
                state.LastError = failure.Message;
                state.NextEvidenceIndex = processed;
                _ = await store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        state.Metrics.EvidenceSkipped = state.Evidence.LongCount(x => x.IsCurrent && !x.AiAnalyzed);
        state.NextEvidenceIndex = processed; state.LastError = "";
        return await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BatchResult> AnalyzeBatchAsync(BatchWork work, ModelOptions options,
        CancellationToken cancellationToken)
    {
        int requests = 0, retries = 0;
        try
        {
            for (int parseAttempt = 0; ; parseAttempt++)
            {
                OllamaCompletion completion;
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        requests++;
                        var watch = Stopwatch.StartNew();
                        completion = client is IJsonSchemaOllamaClient structured
                            ? await structured.TranslateStructuredAsync(work.Prompt, options, ResponseSchema, cancellationToken).ConfigureAwait(false)
                            : await client.TranslateAsync(work.Prompt, options, cancellationToken).ConfigureAwait(false);
                        watch.Stop();
                        if (completion.Metrics.ClientElapsed == TimeSpan.Zero)
                            completion = completion with { Metrics = completion.Metrics with { ClientElapsed = watch.Elapsed } };
                        break;
                    }
                    catch (OllamaRequestException ex) when (attempt < 2 &&
                        ex.Kind is OllamaErrorKind.Connection or OllamaErrorKind.Timeout or OllamaErrorKind.Http or OllamaErrorKind.InvalidResponse)
                    {
                        retries++;
                        await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                    }
                }
                try { return new(work, completion, Parse(completion.Text), requests, retries, null); }
                catch (InvalidDataException) when (parseAttempt < 1) { retries++; }
            }
        }
        catch (Exception ex) { return new(work, null, null, requests, retries, ex); }
    }

    private sealed record BatchWork(CorpusEvidence[] Evidence, string Key, string Prompt);
    private sealed record BatchResult(BatchWork Work, OllamaCompletion? Completion, AnalyzerResponse? Response,
        int Requests, int Retries, Exception? Error);

    private static IEnumerable<CorpusEvidence> Eligible(BookIntelligenceState state) =>
        state.Evidence.Where(x => x.IsCurrent && (state.ScanMode == IntelligenceScanMode.FullSemantic ||
            x.Priority is EvidencePriority.High or EvidencePriority.Medium || x.IsAuditSample || x.AuditExpanded));

    private static CorpusEvidence[] TakeBatch(CorpusEvidence[] pending, int contextSize)
    {
        int characterBudget = Math.Max(1800, Math.Min(16000, contextSize * 2));
        var result = new List<CorpusEvidence>(); int used = 0;
        foreach (CorpusEvidence evidence in pending)
        {
            if (result.Count >= MaximumEvidencePerRequest || (result.Count > 0 && used + evidence.Excerpt.Length > characterBudget)) break;
            result.Add(evidence); used += evidence.Excerpt.Length;
        }
        return result.ToArray();
    }

    /// <summary>
    /// Gemini-only adaptive batching. Sensitive relationship/identity evidence receives
    /// smaller context windows; simple terms and low-priority passages can be packed
    /// more densely. Input budget leaves room for output and schema instructions.
    /// Ollama and older tests retain their original 8-evidence batching.
    /// </summary>
    internal static CorpusEvidence[] TakeAdaptiveBatch(
        CorpusEvidence[] pending, ModelOptions options, int targetInputTokens)
    {
        if (pending.Length == 0) return [];
        bool sensitive = pending[0].Kind == IntelligenceEvidenceKind.Relationship ||
            pending[0].SemanticGroup is "relationship" or "identity" or "dialogue";
        bool high = pending[0].Priority == EvidencePriority.High;
        int maximumItems = sensitive ? 12 : high ? 20 : pending[0].Priority == EvidencePriority.Medium ? 36 : 48;
        int budget = Math.Max(1_000, Math.Min(targetInputTokens,
            options.ContextSize - options.MaximumOutputTokens - 2_048));
        if (sensitive) budget = Math.Min(budget, 14_000);
        else if (high) budget = Math.Min(budget, 19_000);
        int tokens = 900; // instruction/schema overhead; conservative estimate
        var result = new List<CorpusEvidence>();
        foreach (CorpusEvidence item in pending)
        {
            int cost = PromptTokenBudget.Estimate(item.Excerpt) + 150;
            bool nextIsSensitive = item.Kind == IntelligenceEvidenceKind.Relationship ||
                item.SemanticGroup is "relationship" or "identity" or "dialogue";
            if (result.Count > 0 && (result.Count >= maximumItems || tokens + cost > budget ||
                (!sensitive && nextIsSensitive)))
                break;
            result.Add(item);
            tokens += cost;
            if (result.Count >= maximumItems) break;
        }
        // A single long excerpt still has to be sent and validated. The analyzer
        // may split it later if real API usage shows this estimate is too small.
        return result.ToArray();
    }

    private static void ExpandAuditScope(BookIntelligenceState state, IEnumerable<CorpusEvidence> discoveries)
    {
        foreach (CorpusEvidence hit in discoveries)
            foreach (CorpusEvidence similar in state.Evidence.Where(x => x.IsCurrent && !x.AiAnalyzed &&
                x.Priority == EvidencePriority.Low && x.SemanticGroup == hit.SemanticGroup && x.ChapterId == hit.ChapterId))
            {
                similar.AuditExpanded = true;
                similar.PriorityReason += "; expanded after deterministic audit discovered important data";
            }
    }

    internal static string BuildPrompt(IReadOnlyCollection<CorpusEvidence> evidence, ProjectResources resources,
        BookIntelligenceState? state = null)
    {
        string combined = string.Join('\n', evidence.Select(x => x.Excerpt));
        string knownCharacters = string.Join('\n', resources.Characters.Profiles.Where(p =>
                combined.Contains(p.ChineseName, StringComparison.Ordinal) || p.Aliases.Any(combined.Contains))
            .Select(p => $"- {p.ChineseName} = {p.VietnameseName}; aliases=[{string.Join(", ", p.Aliases)}]; gender={p.Gender}"));
        // Stage 1 / Character Intelligence: keep names from earlier AI batches in
        // context, but do NOT silently treat unapproved names as verified facts.
        string provisionalNames = state is null ? "" : string.Join('\n',
            state.CandidateIdentities.Where(c => c.Status != CandidateIdentityStatus.Rejected &&
                (combined.Contains(c.ChineseName, StringComparison.Ordinal) ||
                 c.Aliases.Any(a => !string.IsNullOrWhiteSpace(a) && combined.Contains(a, StringComparison.Ordinal))))
                .Take(80).Select(c =>
                    "- ỨNG VIÊN CHƯA DUYỆT: " + c.ChineseName +
                    "; aliases=[" + string.Join(", ", c.Aliases) + "]; không tự xác nhận danh tính"));
        string knownTerms = string.Join('\n', resources.Terms.Where(t => combined.Contains(t.Chinese, StringComparison.Ordinal))
            .Take(300).Select(t => $"- {t.Chinese} = {t.Vietnamese}"));
        string excerpts = string.Join("\n\n", evidence.Select(e =>
            $"EVIDENCE {e.Id:D} | chapter={e.ChapterId:D} | startUtf16={e.StartUtf16} | priority={e.Priority} | reason={e.PriorityReason}\n{e.Excerpt}"));
        return $$"""
            Bạn là bộ trích xuất dữ kiện cho tiểu thuyết Trung văn. Chỉ dùng chứng cứ được cung cấp; không suy đoán.
            Trả về DUY NHẤT một JSON object đúng schema. confidence chỉ nhận unknown, ambiguous, proposed.
            Mọi mục phải dẫn ít nhất một evidence_id. Tên/thuật ngữ Trung phải xuất hiện nguyên văn trong evidence được dẫn.
            Không biến danh xưng phụ thuộc ngữ cảnh thành glossary toàn cục. Không tự xác minh giới tính, danh tính hoặc xưng hô nếu chứng cứ chưa rõ.
            QUAN HỆ: chỉ đưa vào relationships những quan hệ tương đối bền vững có chứng cứ trực tiếp (cha/mẹ-con, anh chị em, vợ chồng, người yêu đã xác nhận, thầy trò, cấp trên-cấp dưới).
            Không đưa sự kiện/hành động hay cảm xúc thoáng qua như đưa tiền, nhờ vả, cãi nhau, gặp gỡ, romantic interest vào quan hệ ổn định.
            XƯNG HÔ CHO PHẦN MỀM DỊCH: self_reference, addressee_reference, reverse_self_reference, reverse_addressee_reference
            phải là CÁCH XƯNG HÔ TIẾNG VIỆT tự nhiên có chứng cứ ở ngữ cảnh nói chuyện, không chép nguyên các từ 老婆, 老公, 妈, 爸, 哥, 姐...
            Ví dụ: lời gọi 老婆 -> "vợ", 老公 -> "chồng" khi chứng cứ đúng người nói/người nghe; không tự tạo "anh/em" từ nhãn vợ chồng.
            Chỉ ghi xưng hô hai chiều khi chứng cứ xác nhận rõ. Nếu thiếu tuổi, thứ bậc hoặc chiều quan hệ, để trống đại từ chưa rõ; tuyệt đối không suy đoán.
            Schema {{PromptSchemaVersion}}:
            {"characters":[{"chinese_name":"","vietnamese_name":"","aliases":[],"gender":"unknown|male|female|other","primary_pronoun":"","secondary_pronoun":"","confidence":"unknown|ambiguous|proposed","evidence_ids":[],"reason":""}],"relationships":[{"speaker_chinese_name":"","listener_chinese_name":"","relationship":"","self_reference":"","addressee_reference":"","reverse_self_reference":"","reverse_addressee_reference":"","confidence":"unknown|ambiguous|proposed","evidence_ids":[],"reason":""}],"glossary":[{"chinese":"","vietnamese":"","category":"place|organization|clan|cultivation|item|realm|title|term|addressing","context_dependent":false,"confidence":"unknown|ambiguous|proposed","evidence_ids":[],"reason":""}]}

            NHÂN VẬT LIÊN QUAN ĐÃ CÓ:
            {{knownCharacters}}

            ỨNG VIÊN NHÂN VẬT TÍCH LŨY (CHƯA XÁC NHẬN):
            {{provisionalNames}}

            GLOSSARY LIÊN QUAN ĐÃ CÓ:
            {{knownTerms}}

            CHỨNG CỨ:
            {{excerpts}}
            """;
    }

    internal static AnalyzerResponse Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Model trả phản hồi khảo sát rỗng.");
        int start = text.IndexOf('{'), end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("Model không trả JSON khảo sát hợp lệ.");
        try
        {
            AnalyzerResponse response = JsonSerializer.Deserialize<AnalyzerResponse>(text[start..(end + 1)],
                new JsonSerializerOptions(DocumentJson.Options) { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("JSON khảo sát rỗng.");
            response.Characters ??= []; response.Relationships ??= []; response.Glossary ??= [];
            foreach (CharacterFinding item in response.Characters) { item.Aliases ??= []; item.EvidenceIds ??= []; }
            foreach (RelationshipFinding item in response.Relationships) item.EvidenceIds ??= [];
            foreach (GlossaryFinding item in response.Glossary) item.EvidenceIds ??= [];
            return response;
        }
        catch (JsonException ex) { throw new InvalidDataException("Model trả JSON khảo sát không đúng schema.", ex); }
    }

    internal static void ApplyValidatedResponse(BookIntelligenceState state, AnalyzerResponse response,
        IReadOnlyCollection<CorpusEvidence> batch, ProjectResources resources)
    {
        Dictionary<Guid, CorpusEvidence> allowed = batch.ToDictionary(x => x.Id);
        // Normalize older pending items as well as new AI responses.
        RelationshipLabelNormalizer.NormalizePending(state);
        foreach (CharacterFinding finding in response.Characters)
        {
            Guid[] ids = ValidateEvidenceIds(finding.EvidenceIds, allowed);
            string chinese = finding.ChineseName.Trim();
            if (ids.Length == 0 || chinese.Length == 0 || !Appears(chinese, ids, allowed)) continue;
            string[] aliases = finding.Aliases.Where(x => !string.IsNullOrWhiteSpace(x) && Appears(x.Trim(), ids, allowed))
                .Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            CandidateIdentity identity = EntityResolutionService.Resolve(state, resources, chinese,
                finding.VietnameseName.Trim(), aliases, ids);
            IntelligenceCharacterSuggestion? existing = state.Characters.FirstOrDefault(x =>
                x.CandidateIdentityId == identity.Id || x.ChineseName == chinese || x.Aliases.Contains(chinese, StringComparer.Ordinal));
            if (existing is not null)
            {
                bool newEvidence = ids.Any(id => !existing.EvidenceIds.Contains(id));
                foreach (Guid id in ids.Where(id => !existing.EvidenceIds.Contains(id))) existing.EvidenceIds.Add(id);
                foreach (string alias in aliases.Where(a => !existing.Aliases.Contains(a, StringComparer.Ordinal))) existing.Aliases.Add(alias);
                if (existing.Status == IntelligenceReviewStatus.Rejected && newEvidence)
                { existing.Status = IntelligenceReviewStatus.ReviewLater; existing.Reason += "\nCó bằng chứng mới sau lần từ chối trước."; }
                continue;
            }
            state.Characters.Add(new IntelligenceCharacterSuggestion
            {
                ChineseName = chinese, VietnameseName = finding.VietnameseName.Trim(), Aliases = aliases.ToList(),
                Gender = ParseGender(finding.Gender), PrimaryNarrativePronoun = finding.PrimaryPronoun.Trim(),
                SecondaryNarrativePronoun = finding.SecondaryPronoun.Trim(), Confidence = ParseConfidence(finding.Confidence),
                EvidenceIds = ids.ToList(), Reason = finding.Reason.Trim(),
                PossibleExistingCharacterId = identity.ProposedExistingCharacterId, CandidateIdentityId = identity.Id
            });
        }

        foreach (RelationshipFinding finding in response.Relationships)
        {
            Guid[] ids = ValidateEvidenceIds(finding.EvidenceIds, allowed);
            string speaker = finding.SpeakerChineseName.Trim(), listener = finding.ListenerChineseName.Trim();
            if (ids.Length == 0 || speaker.Length == 0 || listener.Length == 0 || speaker == listener ||
                !Appears(speaker, ids, allowed) || !Appears(listener, ids, allowed)) continue;

            RelationshipLabelNormalizer.Result normalized = RelationshipLabelNormalizer.Normalize(finding.Relationship);
            // Events and vague attraction are not relationships. The original evidence
            // is preserved in the corpus; it is not added to the relationship graph.
            if (normalized.Classification is RelationshipLabelNormalizer.Kind.Event or
                RelationshipLabelNormalizer.Kind.Uncertain) continue;

            string label = normalized.Display;
            bool stable = normalized.Classification == RelationshipLabelNormalizer.Kind.Stable;
            string original = finding.Relationship.Trim();
            string note = original == label ? "" : "Nhãn AI ban đầu: " + original;
            string reason = string.IsNullOrWhiteSpace(note) ? finding.Reason.Trim() :
                finding.Reason.Trim() + "\n" + note;
            if (!stable) reason += "\nNhãn chưa có trong danh mục chuẩn; cần xác minh trước khi sử dụng.";
            bool symmetric = stable &&
                !RelationshipIntelligenceQualityGate.RequiresDirection(normalized.RelationType);
            IntelligenceRelationshipSuggestion? existing = state.Relationships.FirstOrDefault(x =>
                (x.Status is IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater) &&
                RelationshipLabelNormalizer.Normalize(x.Relationship).Key == normalized.Key &&
                ((x.SpeakerChineseName == speaker && x.ListenerChineseName == listener) ||
                 (symmetric && x.SpeakerChineseName == listener && x.ListenerChineseName == speaker)));
            if (existing is not null)
            {
                foreach (Guid id in ids.Where(id => !existing.EvidenceIds.Contains(id))) existing.EvidenceIds.Add(id);
                // Only reversible relationship TYPES may share a case across both
                // named directions; addressing fields must be remapped by speaker.
                bool reversedNames = existing.SpeakerChineseName == listener &&
                                     existing.ListenerChineseName == speaker;
                string newSelf = RelationshipIntelligenceQualityGate.SafeAddressingDraft(
                    (reversedNames ? finding.ReverseSelfReference : finding.SelfReference).Trim());
                string newCall = RelationshipIntelligenceQualityGate.SafeAddressingDraft(
                    (reversedNames ? finding.ReverseAddresseeReference : finding.AddresseeReference).Trim());
                string newReverseSelf = RelationshipIntelligenceQualityGate.SafeAddressingDraft(
                    (reversedNames ? finding.SelfReference : finding.ReverseSelfReference).Trim());
                string newReverseCall = RelationshipIntelligenceQualityGate.SafeAddressingDraft(
                    (reversedNames ? finding.AddresseeReference : finding.ReverseAddresseeReference).Trim());
                if (existing.SelfReference != newSelf ||
                    existing.AddresseeReference != newCall ||
                    existing.ReverseSelfReference != newReverseSelf ||
                    existing.ReverseAddresseeReference != newReverseCall)
                {
                    string note = "Xưng hô AI ở đoạn khác không thống nhất (chưa duyệt); không tạo thêm hàng chờ hoặc ghi đè: " +
                        finding.SelfReference.Trim() + " / " + finding.AddresseeReference.Trim() + " / " +
                        finding.ReverseSelfReference.Trim() + " / " + finding.ReverseAddresseeReference.Trim();
                    if (!existing.Reason.Contains(note, StringComparison.Ordinal))
                        existing.Reason += "\n" + note;
                    existing.Status = IntelligenceReviewStatus.ReviewLater;
                    existing.Confidence = IntelligenceConfidence.Ambiguous;
                }
                continue;
            }
            var suggestion = new IntelligenceRelationshipSuggestion
            {
                SpeakerChineseName = speaker, ListenerChineseName = listener,
                Relationship = label, SelfReference = finding.SelfReference.Trim(),
                AddresseeReference = finding.AddresseeReference.Trim(),
                ReverseSelfReference = finding.ReverseSelfReference.Trim(),
                ReverseAddresseeReference = finding.ReverseAddresseeReference.Trim(),
                Confidence = ParseConfidence(finding.Confidence),
                Status = stable ? IntelligenceReviewStatus.Pending : IntelligenceReviewStatus.ReviewLater,
                EvidenceIds = ids.ToList(), Reason = reason
            };
            // Stage 2 validates evidence; stage 3 strips placeholders/Chinese labels.
            // Never create a graph fact before the user confirms its semantics/direction.
            RelationshipIntelligenceQualityGate.ProtectUnverifiedNewSuggestion(suggestion, allowed);
            state.Relationships.Add(suggestion);
        }

        foreach (GlossaryFinding finding in response.Glossary)
        {
            Guid[] ids = ValidateEvidenceIds(finding.EvidenceIds, allowed);
            string chinese = finding.Chinese.Trim();
            if (ids.Length == 0 || chinese.Length == 0 || !Appears(chinese, ids, allowed) ||
                resources.Characters.Profiles.Any(p => p.ChineseName == chinese || p.Aliases.Contains(chinese, StringComparer.Ordinal))) continue;
            IntelligenceGlossarySuggestion? existing = state.Glossary.FirstOrDefault(x => x.Chinese == chinese);
            if (existing is not null)
            {
                bool newEvidence = ids.Any(id => !existing.EvidenceIds.Contains(id));
                foreach (Guid id in ids.Where(id => !existing.EvidenceIds.Contains(id))) existing.EvidenceIds.Add(id);
                if (existing.Status == IntelligenceReviewStatus.Rejected && newEvidence)
                { existing.Status = IntelligenceReviewStatus.ReviewLater; existing.Reason += "\nCó bằng chứng mới sau lần từ chối trước."; }
                continue;
            }
            if (resources.Terms.Any(x => x.Chinese == chinese)) continue;
            (GlossaryKnowledgeCategory category, GlossaryKnowledgeScope scope) = ClassifyGlossary(finding);
            state.Glossary.Add(new IntelligenceGlossarySuggestion
            {
                Chinese = chinese, Vietnamese = finding.Vietnamese.Trim(), Category = finding.Category.Trim(),
                ContextDependent = finding.ContextDependent, Confidence = ParseConfidence(finding.Confidence),
                EvidenceIds = ids.ToList(), Reason = finding.Reason.Trim(), KnowledgeCategory = category, Scope = scope
            });
        }
        state.Validate();
    }

    internal static string RelevantContextFingerprint(IReadOnlyCollection<CorpusEvidence> evidence, ProjectResources resources,
        BookIntelligenceState? state = null)
    {
        string source = string.Join('\n', evidence.Select(x => x.Excerpt));
        IEnumerable<string> characters = resources.Characters.Profiles.Where(p => source.Contains(p.ChineseName, StringComparison.Ordinal) ||
                p.Aliases.Any(source.Contains))
            .Select(p => $"C:{p.Id:D}:{p.ChineseName}:{p.VietnameseName}:{p.Gender}:{string.Join(',', p.Aliases.Order())}");
        IEnumerable<string> terms = resources.Terms.Where(t => source.Contains(t.Chinese, StringComparison.Ordinal))
            .Select(t => $"T:{t.Chinese}:{t.Vietnamese}");
        IEnumerable<string> candidates = state?.CandidateIdentities.Where(c =>
                c.Status != CandidateIdentityStatus.Rejected &&
                (source.Contains(c.ChineseName, StringComparison.Ordinal) ||
                 c.Aliases.Any(a => !string.IsNullOrWhiteSpace(a) && source.Contains(a, StringComparison.Ordinal))))
            .Select(c => "U:" + c.ChineseName + ":" + string.Join(',', c.Aliases.Order())) ?? [];
        string value = string.Join('|', characters.Concat(terms).Concat(candidates).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    internal static string AnalysisKey(IEnumerable<CorpusEvidence> evidence, string model, string version, string relevantContext)
    {
        string value = model + "|" + version + "|context:" + relevantContext + "|" +
            string.Join('|', evidence.Select(x => $"{x.SourceSha256}:{x.StartUtf16}:{x.LengthUtf16}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static (GlossaryKnowledgeCategory, GlossaryKnowledgeScope) ClassifyGlossary(GlossaryFinding finding)
    {
        string c = finding.Category.Trim().ToLowerInvariant();
        GlossaryKnowledgeCategory category = c switch
        {
            "place" => GlossaryKnowledgeCategory.Place, "organization" => GlossaryKnowledgeCategory.Organization,
            "clan" => GlossaryKnowledgeCategory.Clan, "cultivation" => GlossaryKnowledgeCategory.CultivationMethod,
            "item" => GlossaryKnowledgeCategory.Item, "realm" => GlossaryKnowledgeCategory.Realm,
            "title" => GlossaryKnowledgeCategory.Title, _ => GlossaryKnowledgeCategory.StoryTerm
        };
        GlossaryKnowledgeScope scope = c == "addressing" ? GlossaryKnowledgeScope.AddressingKinship :
            finding.ContextDependent || c == "title" ? GlossaryKnowledgeScope.ContextDependent : GlossaryKnowledgeScope.Global;
        return (category, scope);
    }

    private static int ChapterNumber(BookIntelligenceState state, Guid chapterId) => state.Chapters.Single(c => c.ChapterId == chapterId).ChapterNumber;
    private static Guid[] ValidateEvidenceIds(List<Guid> ids, IReadOnlyDictionary<Guid, CorpusEvidence> allowed) => ids.Where(allowed.ContainsKey).Distinct().ToArray();
    private static bool Appears(string value, IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, CorpusEvidence> evidence) => ids.Any(id => evidence[id].Excerpt.Contains(value, StringComparison.Ordinal));
    private static CharacterGender ParseGender(string value) => value.Trim().ToLowerInvariant() switch { "male" => CharacterGender.Male, "female" => CharacterGender.Female, "other" => CharacterGender.Other, _ => CharacterGender.Unknown };
    private static IntelligenceConfidence ParseConfidence(string value) => value.Trim().ToLowerInvariant() switch { "proposed" => IntelligenceConfidence.Proposed, "ambiguous" => IntelligenceConfidence.Ambiguous, _ => IntelligenceConfidence.Unknown };
}

public sealed class AnalyzerResponse
{
    public List<CharacterFinding> Characters { get; set; } = [];
    public List<RelationshipFinding> Relationships { get; set; } = [];
    public List<GlossaryFinding> Glossary { get; set; } = [];
}
public sealed class CharacterFinding
{
    [JsonPropertyName("chinese_name")] public string ChineseName { get; set; } = "";
    [JsonPropertyName("vietnamese_name")] public string VietnameseName { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public string Gender { get; set; } = "unknown";
    [JsonPropertyName("primary_pronoun")] public string PrimaryPronoun { get; set; } = "";
    [JsonPropertyName("secondary_pronoun")] public string SecondaryPronoun { get; set; } = "";
    public string Confidence { get; set; } = "unknown";
    [JsonPropertyName("evidence_ids")] public List<Guid> EvidenceIds { get; set; } = [];
    public string Reason { get; set; } = "";
}
public sealed class RelationshipFinding
{
    [JsonPropertyName("speaker_chinese_name")] public string SpeakerChineseName { get; set; } = "";
    [JsonPropertyName("listener_chinese_name")] public string ListenerChineseName { get; set; } = "";
    public string Relationship { get; set; } = "";
    [JsonPropertyName("self_reference")] public string SelfReference { get; set; } = "";
    [JsonPropertyName("addressee_reference")] public string AddresseeReference { get; set; } = "";
    [JsonPropertyName("reverse_self_reference")] public string ReverseSelfReference { get; set; } = "";
    [JsonPropertyName("reverse_addressee_reference")] public string ReverseAddresseeReference { get; set; } = "";
    public string Confidence { get; set; } = "unknown";
    [JsonPropertyName("evidence_ids")] public List<Guid> EvidenceIds { get; set; } = [];
    public string Reason { get; set; } = "";
}
public sealed class GlossaryFinding
{
    public string Chinese { get; set; } = "";
    public string Vietnamese { get; set; } = "";
    public string Category { get; set; } = "";
    [JsonPropertyName("context_dependent")] public bool ContextDependent { get; set; }
    public string Confidence { get; set; } = "unknown";
    [JsonPropertyName("evidence_ids")] public List<Guid> EvidenceIds { get; set; } = [];
    public string Reason { get; set; } = "";
}
