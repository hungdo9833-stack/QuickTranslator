using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.Importing;

public sealed record BulkImportChapterDefaults(
    SegmenterOptions Segmentation,
    ModelOptions Model,
    PromptPreset Preset);

public sealed record BulkImportOptions(
    int MaxChaptersPerChunk = 50,
    long MaxUtf8BytesPerChunk = 16 * 1024 * 1024,
    bool ReplaceSoftDeletedHistory = false)
{
    public void Validate()
    {
        if (MaxChaptersPerChunk is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(MaxChaptersPerChunk), "Chunk phải có 1–1000 chương.");
        if (MaxUtf8BytesPerChunk is < 1024 or > 512L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxUtf8BytesPerChunk), "Dung lượng chunk phải từ 1 KiB đến 512 MiB.");
    }
}

public enum BulkImportChapterState
{
    NotStarted,
    Imported,
    AlreadyImported,
    RolledBack,
    Failed,
    NeedsRecovery
}

public sealed record BulkImportChapterOutcome(
    Guid PreviewItemId,
    Guid ChapterId,
    int Number,
    string Title,
    BulkImportChapterState State,
    string? Error);

public sealed record BulkImportProgress(
    int CompletedChunks,
    int TotalChunks,
    int ImportedChapters,
    int TotalChapters,
    string Message);

public sealed record BulkImportReport(
    Guid ImportId,
    string SourceArchivePath,
    string SourceRawSha256,
    int CompletedChunks,
    int TotalChunks,
    IReadOnlyList<BulkImportChapterOutcome> Chapters,
    string? Error,
    bool CanResume)
{
    public int ImportedCount => Chapters.Count(x => x.State == BulkImportChapterState.Imported);
    public int AlreadyImportedCount => Chapters.Count(x => x.State == BulkImportChapterState.AlreadyImported);
    public bool Completed => Error is null && Chapters.All(x => x.State is BulkImportChapterState.Imported or BulkImportChapterState.AlreadyImported);
    public string PlanFingerprint { get; init; } = "";
    public SourceCoverageSummary? Coverage { get; init; }
    public bool PostCommitVerified { get; init; }
    public bool VerifiedComplete => Completed && PostCommitVerified && Coverage?.IsVerifiedComplete == true;
    public bool PartialImport => Completed && PostCommitVerified && Coverage?.IsPartialImport == true;
}

public sealed class BulkImportValidationException(string message, IReadOnlyList<string> issues)
    : IOException(message)
{
    public IReadOnlyList<string> Issues { get; } = issues;
}

public sealed class SafeBulkImportService(ProjectRepository repository)
{
    public Action<string>? VerificationFaultPoint { get; set; }

    public async Task<BulkImportReport> ImportAsync(ProjectSession session, ImportPreviewPlan plan,
        BulkImportChapterDefaults defaults, BulkImportOptions? options = null,
        IProgress<BulkImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(defaults);
        options ??= new();
        options.Validate();
        defaults.Segmentation.Validate();
        defaults.Model.Validate();
        defaults.Preset.Validate();
        plan.ValidateExactCoverage();
        ValidateSourceSnapshot(plan.Source);
        if (!plan.CoverageAudit.CanImport)
            throw new BulkImportValidationException("Coverage Ledger còn vùng chưa giải quyết; chưa ghi Project.",
                plan.CoverageAudit.BlockingIssues);

        ImportPreviewItem[] selected = plan.ImportableChapters.ToArray();
        if (selected.Length == 0)
            throw new BulkImportValidationException("Chưa có chương hợp lệ được chọn để nhập.", ["Hãy include ít nhất một chương có số và nội dung."]);
        var previewErrors = plan.Issues.Where(x => x.Severity == DetectionIssueSeverity.Error).Select(x => x.Message).Distinct().ToArray();
        if (previewErrors.Length > 0)
            throw new BulkImportValidationException("Preview còn lỗi chặn import.", previewErrors);

        Guid importId = StableGuid($"qtaie-m2-import:{session.Manifest.ProjectId:D}:{plan.Fingerprint}");
        string archivePath = session.Resolve($"Backups/detected-source-{importId:N}.zip");
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync(cancellationToken).ConfigureAwait(false);
        resources.Validate();
        if (resources.ProjectId != session.Manifest.ProjectId)
            throw new InvalidDataException("Tài nguyên canonical không thuộc Project đang import.");

        var outcomes = selected.Select(item => new MutableOutcome(item)).ToArray();
        IReadOnlyList<string> collisions = await PreflightAsync(session, plan, outcomes, options, cancellationToken).ConfigureAwait(false);
        if (collisions.Count > 0)
            throw new BulkImportValidationException("Bulk import có collision; chưa ghi source rescue hoặc Project.", collisions);

        if (cancellationToken.IsCancellationRequested)
            return BuildReport(importId, archivePath, plan, 0, 0, outcomes,
                "Đã hủy trước khi ghi source rescue; Project chưa thay đổi.", true, false);

        try
        {
            await EnsureSourceArchiveAsync(session, plan, importId, archivePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            return BuildReport(importId, archivePath, plan, 0, 0, outcomes,
                "Đã hủy khi đang tạo source rescue; Project chưa thay đổi. " + ex.Message, true, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return BuildReport(importId, archivePath, plan, 0, 0, outcomes,
                "Không thể tạo/xác minh source rescue; Project chưa thay đổi. " + ex.Message, true, false);
        }
        MutableOutcome[] pending = outcomes.Where(x => x.State == BulkImportChapterState.NotStarted).ToArray();
        List<MutableOutcome[]> chunks = CreateChunks(plan, pending, options);
        int completedChunks = 0;

        for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            if (cancellationToken.IsCancellationRequested)
                return BuildReport(importId, archivePath, plan, completedChunks, chunks.Count, outcomes,
                    "Đã hủy giữa các chunk. Các chunk đã commit được giữ; phần còn lại có thể tiếp tục an toàn.", true, false);

            MutableOutcome[] chunk = chunks[chunkIndex];
            var documents = chunk.Select(x => (CreateChapter(session, plan, x.Item, defaults, resources), x.Item.Number!.Value)).ToArray();
            var published = new HashSet<Guid>();
            try
            {
                await repository.ImportDetectedChapterChunkAsync(session, documents, archivePath,
                    $"DetectedBulkImport:{importId:N}:{chunkIndex + 1}/{chunks.Count}", id => published.Add(id),
                    options.ReplaceSoftDeletedHistory, cancellationToken)
                    .ConfigureAwait(false);
                await VerifyPersistedItemsAsync(session.ManifestPath, plan, chunk.Select(x => x.Item), cancellationToken)
                    .ConfigureAwait(false);
                foreach (MutableOutcome item in chunk) item.State = BulkImportChapterState.Imported;
                completedChunks++;
                progress?.Report(new(completedChunks, chunks.Count,
                    outcomes.Count(x => x.State is BulkImportChapterState.Imported or BulkImportChapterState.AlreadyImported),
                    outcomes.Length, $"Đã commit chunk {completedChunks}/{chunks.Count}."));
            }
            catch (ProjectOperationInterruptedException)
            {
                throw; // Test seam models a real process loss: durable journal is handled after reopen.
            }
            catch (Exception ex)
            {
                bool rollbackComplete = ex.Data["RollbackComplete"] is true ||
                    ex is ProjectTransactionException transaction && transaction.RollbackComplete;
                BulkImportChapterState state = rollbackComplete ? BulkImportChapterState.RolledBack : BulkImportChapterState.NeedsRecovery;
                foreach (MutableOutcome item in chunk)
                {
                    item.State = state;
                    item.Error = ex.Message;
                }
                return BuildReport(importId, archivePath, plan, completedChunks, chunks.Count, outcomes,
                    rollbackComplete
                        ? "Chunk lỗi đã rollback toàn bộ; các chunk trước vẫn giữ nguyên và có thể tiếp tục. " + ex.Message
                        : "Chunk lỗi cần recovery trước khi ghi tiếp. " + ex.Message,
                    rollbackComplete, false);
            }
        }

        try
        {
            await VerifyPersistedItemsAsync(session.ManifestPath, plan, outcomes.Select(x => x.Item), cancellationToken)
                .ConfigureAwait(false);
            await VerifySourceArchiveAsync(archivePath, session.Manifest.ProjectId, plan, importId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {
            return BuildReport(importId, archivePath, plan, completedChunks, chunks.Count, outcomes,
                "Đã ghi dữ liệu nhưng kiểm toán sau commit thất bại; không đánh dấu hoàn tất. " + ex.Message, true, false);
        }
        return BuildReport(importId, archivePath, plan, completedChunks, chunks.Count, outcomes, null, true, true);
    }

    private async Task<IReadOnlyList<string>> PreflightAsync(ProjectSession session, ImportPreviewPlan plan,
        IReadOnlyList<MutableOutcome> outcomes, BulkImportOptions options, CancellationToken token)
    {
        var issues = new List<string>();
        Dictionary<Guid, ProjectChapterEntry> byId = session.Manifest.Chapters.ToDictionary(x => x.ChapterId);
        Dictionary<int, ProjectChapterEntry> activeByNumber = session.Manifest.Chapters.Where(x => x.DeletedAt is null)
            .ToDictionary(x => x.Number);
        foreach (IGrouping<int, MutableOutcome> duplicate in outcomes.GroupBy(x => x.Item.Number!.Value).Where(x => x.Count() > 1))
            issues.Add($"Project schema hiện yêu cầu số chapter active duy nhất: số {duplicate.Key} xuất hiện ở nhiều quyển. " +
                "Giữ nguyên số gốc trong preview và chưa ghi Project cho đến khi có migration metadata an toàn.");
        foreach (MutableOutcome outcome in outcomes)
        {
            token.ThrowIfCancellationRequested();
            ImportPreviewItem item = outcome.Item;
            int number = item.Number!.Value;
            string expectedText = plan.Slice(item.ContentSpan);
            string expectedTitle = Title(item);
            if (byId.TryGetValue(item.ChapterId, out ProjectChapterEntry? sameId))
            {
                if (sameId.DeletedAt is not null)
                {
                    if (!options.ReplaceSoftDeletedHistory)
                    {
                        issues.Add($"ChapterId {item.ChapterId:D} đã tồn tại trong History/xóa mềm; chỉ ghi đè khi người dùng bật tùy chọn xóa History cũ.");
                        continue;
                    }
                    try
                    {
                        ChapterDocument deleted = await ChapterFileStore.ReadVerifiedFileAsync(
                            session.Resolve(sameId.RelativePath), token).ConfigureAwait(false);
                        if (deleted.Id != sameId.ChapterId || deleted.ProjectId != session.Manifest.ProjectId)
                            throw new InvalidDataException("File History không thuộc ChapterId/Project cần thay thế.");
                        string backupPath = session.Resolve(sameId.RelativePath) + ".bak";
                        if (File.Exists(backupPath))
                        {
                            ChapterDocument backup = await ChapterFileStore.ReadVerifiedFileAsync(backupPath, token).ConfigureAwait(false);
                            if (backup.Id != sameId.ChapterId || backup.ProjectId != session.Manifest.ProjectId)
                                throw new InvalidDataException("Backup History không thuộc ChapterId/Project cần thay thế.");
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        issues.Add($"Không thể xác minh bản History của ChapterId {item.ChapterId:D}; chưa xóa hoặc ghi đè. {ex.Message}");
                        continue;
                    }
                }
                else
                {
                    ChapterDocument existing = (await repository.LoadChapterAsync(session, item.ChapterId, token).ConfigureAwait(false)).Chapter;
                    if (sameId.Number == number && existing.ChapterNumber == number && existing.ProjectId == session.Manifest.ProjectId &&
                        existing.Title == expectedTitle && existing.ChineseTitle == ChineseTitle(item) && existing.OriginalText == expectedText)
                    {
                        outcome.State = BulkImportChapterState.AlreadyImported;
                        continue;
                    }
                    issues.Add($"ChapterId {item.ChapterId:D} đã tồn tại nhưng nội dung/metadata khác preview; giữ nguyên chapter hiện có.");
                    continue;
                }
            }
            if (activeByNumber.TryGetValue(number, out ProjectChapterEntry? collision))
                issues.Add($"Số chương {number} đã thuộc chapter “{collision.VietnameseTitle}”; không ghi đè.");
            string path = session.ChapterStore.GetPath(item.ChapterId);
            if (File.Exists(path) || File.Exists(path + ".bak") || Directory.Exists(path) || Directory.Exists(path + ".bak"))
                issues.Add($"Đích {Path.GetFileName(path)} đã tồn tại ngoài manifest, có thể là orphan; cần đối chiếu recovery.");
        }
        return issues;
    }

    private async Task VerifyPersistedItemsAsync(string manifestPath, ImportPreviewPlan plan,
        IEnumerable<ImportPreviewItem> items, CancellationToken token)
    {
        VerificationFaultPoint?.Invoke("BeforePostCommitVerify");
        var verifier = new ProjectRepository();
        ProjectSession reopened = (await verifier.OpenAsync(manifestPath, token).ConfigureAwait(false)).Session;
        if (reopened.RecoveryReport.RequiresDecision)
            throw new InvalidDataException("Project còn transaction/orphan cần recovery sau commit.");
        foreach (ImportPreviewItem item in items)
        {
            token.ThrowIfCancellationRequested();
            VerificationFaultPoint?.Invoke($"VerifyChapter:{item.ChapterId:D}");
            ProjectChapterEntry manifest = reopened.Manifest.Chapters.SingleOrDefault(x =>
                x.ChapterId == item.ChapterId && x.DeletedAt is null)
                ?? throw new InvalidDataException($"Manifest thiếu ChapterId {item.ChapterId:D} sau commit.");
            ChapterDocument actual = (await verifier.LoadChapterAsync(reopened, item.ChapterId, token).ConfigureAwait(false)).Chapter;
            string expected = plan.Slice(item.ContentSpan);
            if (manifest.Number != item.Number || actual.ChapterNumber != item.Number ||
                actual.ProjectId != reopened.Manifest.ProjectId || actual.OriginalText != expected)
                throw new InvalidDataException($"Chapter {item.Number} không khớp plan sau khi đọc lại từ ổ đĩa.");
            string expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
            string actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actual.OriginalText)));
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 chapter {item.Number} không khớp plan.");
            string reconstructed = string.Concat(actual.Segments.OrderBy(x => x.Order).Select(x => x.SourceText));
            if (!string.Equals(reconstructed, actual.OriginalText, StringComparison.Ordinal))
                throw new InvalidDataException($"Segment.SourceText của chapter {item.Number} không tái tạo đúng OriginalText.");
        }
    }

    private static ChapterDocument CreateChapter(ProjectSession session, ImportPreviewPlan plan, ImportPreviewItem item,
        BulkImportChapterDefaults defaults, ProjectResources resources)
    {
        string text = plan.Slice(item.ContentSpan);
        var chapter = TranslationService.CreateChapter(Title(item), text, defaults.Segmentation, defaults.Model);
        chapter.Id = item.ChapterId;
        chapter.Segments = new TextSegmenter().Segment(text, chapter.Id, defaults.Segmentation).ToList();
        chapter.SelectedSegmentId = chapter.Segments[0].Id;
        chapter.SelectedPreset = defaults.Preset;
        chapter.ChineseTitle = ChineseTitle(item);
        chapter.ProjectId = session.Manifest.ProjectId;
        chapter.ChapterNumber = item.Number;
        resources.ApplyTo(chapter);
        DocumentJson.Validate(chapter);
        return chapter;
    }

    private static List<MutableOutcome[]> CreateChunks(ImportPreviewPlan plan, IReadOnlyList<MutableOutcome> pending,
        BulkImportOptions options)
    {
        var result = new List<MutableOutcome[]>();
        var current = new List<MutableOutcome>();
        long bytes = 0;
        foreach (MutableOutcome item in pending)
        {
            long itemBytes = Encoding.UTF8.GetByteCount(plan.Slice(item.Item.ContentSpan));
            if (current.Count > 0 && (current.Count >= options.MaxChaptersPerChunk || bytes + itemBytes > options.MaxUtf8BytesPerChunk))
            {
                result.Add(current.ToArray());
                current.Clear();
                bytes = 0;
            }
            current.Add(item);
            bytes = checked(bytes + itemBytes);
        }
        if (current.Count > 0) result.Add(current.ToArray());
        return result;
    }

    private static async Task EnsureSourceArchiveAsync(ProjectSession session, ImportPreviewPlan plan, Guid importId,
        string destination, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            await VerifySourceArchiveAsync(destination, session.Manifest.ProjectId, plan, importId, token).ConfigureAwait(false);
            return;
        }

        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    ZipArchiveEntry raw = archive.CreateEntry("source-original.bin", CompressionLevel.Optimal);
                    await using (Stream stream = raw.Open())
                        await stream.WriteAsync(plan.Source.OriginalBytes.ToArray(), token).ConfigureAwait(false);
                    ZipArchiveEntry metadata = archive.CreateEntry("import-plan.json", CompressionLevel.Optimal);
                    await using Stream metadataStream = metadata.Open();
                    await JsonSerializer.SerializeAsync(metadataStream, CreateArchiveManifest(session.Manifest.ProjectId, plan, importId),
                        DocumentJson.Options, token).ConfigureAwait(false);
                }
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            await VerifySourceArchiveAsync(temporary, session.Manifest.ProjectId, plan, importId, token).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Trace.TraceError($"Không dọn được source rescue tạm {temporary}: {ex.Message}"); }
            }
        }
    }

    private static async Task VerifySourceArchiveAsync(string path, Guid projectId, ImportPreviewPlan plan, Guid importId,
        CancellationToken token)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        ZipArchiveEntry raw = archive.GetEntry("source-original.bin")
            ?? throw new InvalidDataException("Source rescue thiếu raw source.");
        await using var rawStream = raw.Open();
        using var bytes = new MemoryStream();
        await rawStream.CopyToAsync(bytes, token).ConfigureAwait(false);
        if (!string.Equals(RecoveryFiles.Hash(bytes.ToArray()), plan.Source.RawSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source rescue không khớp raw hash của preview; không ghi Project.");
        ZipArchiveEntry metadata = archive.GetEntry("import-plan.json")
            ?? throw new InvalidDataException("Source rescue thiếu import plan.");
        await using Stream metadataStream = metadata.Open();
        ArchiveManifest saved = await JsonSerializer.DeserializeAsync<ArchiveManifest>(metadataStream, DocumentJson.Options, token)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Source rescue có metadata trống.");
        if (saved.SchemaVersion != 2 || saved.ProjectId != projectId || saved.ImportId != importId ||
            saved.RawSha256 != plan.Source.RawSha256 || saved.TextSha256 != plan.Source.TextSha256 ||
            saved.PlanFingerprint != plan.Fingerprint || saved.Items.Count != plan.Items.Length ||
            saved.CoverageFingerprint != CoverageFingerprint(plan.CoverageAudit.Entries) ||
            saved.Coverage.Count != plan.CoverageAudit.Entries.Length)
            throw new InvalidDataException("Source rescue không thuộc đúng Project/preview hiện tại.");
    }

    private static ArchiveManifest CreateArchiveManifest(Guid projectId, ImportPreviewPlan plan, Guid importId) => new()
    {
        ProjectId = projectId,
        ImportId = importId,
        SourceName = plan.Source.SourceName,
        EncodingName = plan.Source.EncodingName,
        RawSha256 = plan.Source.RawSha256,
        TextSha256 = plan.Source.TextSha256,
        PlanFingerprint = plan.Fingerprint,
        CoverageFingerprint = CoverageFingerprint(plan.CoverageAudit.Entries),
        Items = plan.Items.Select(x => new ArchiveItem
        {
            PreviewItemId = x.Id,
            ChapterId = x.ChapterId,
            Kind = x.Kind,
            StartUtf16 = x.Span.StartUtf16,
            LengthUtf16 = x.Span.LengthUtf16,
            ContentStartUtf16 = x.ContentSpan.StartUtf16,
            ContentLengthUtf16 = x.ContentSpan.LengthUtf16,
            Number = x.Number,
            Title = x.Title,
            VietnameseTitle = x.VietnameseTitle,
            VolumeTitle = x.VolumeTitle,
            VolumeIdentity = x.VolumeIdentity,
            Included = x.Included
        }).ToList(),
        Coverage = plan.CoverageAudit.Entries.Select(x => new ArchiveCoverageEntry
        {
            StartUtf16 = x.Span.StartUtf16,
            LengthUtf16 = x.Span.LengthUtf16,
            Sha256 = x.Sha256,
            Status = x.Status,
            PreviewItemId = x.PreviewItemId,
            TargetChapterId = x.TargetChapterId,
            Decision = x.Decision,
            Evidence = x.Evidence
        }).ToList()
    };

    private static void ValidateSourceSnapshot(RawSourceSnapshot source)
    {
        if (source.OriginalBytes.IsDefault || RecoveryFiles.Hash(source.OriginalBytes.ToArray()) != source.RawSha256)
            throw new InvalidDataException("Raw source đã đổi sau preview.");
        byte[] textBytes;
        try { textBytes = new UTF8Encoding(false, true).GetBytes(source.Text); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("Source chứa UTF-16 không hợp lệ.", ex); }
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(textBytes)), source.TextSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Text source đã đổi sau preview.");
    }

    private static BulkImportReport BuildReport(Guid importId, string archive, ImportPreviewPlan plan, int completedChunks,
        int totalChunks, IEnumerable<MutableOutcome> outcomes, string? error, bool canResume, bool postCommitVerified) =>
        new(importId, archive, plan.Source.RawSha256, completedChunks, totalChunks,
            outcomes.Select(x => new BulkImportChapterOutcome(x.Item.Id, x.Item.ChapterId, x.Item.Number!.Value,
                Title(x.Item), x.State, x.Error)).ToArray(), error, canResume)
        {
            PlanFingerprint = plan.Fingerprint,
            Coverage = plan.CoverageAudit.Summary,
            PostCommitVerified = postCommitVerified
        };

    private static string CoverageFingerprint(IEnumerable<SourceCoverageEntry> entries)
    {
        var text = new StringBuilder();
        foreach (SourceCoverageEntry entry in entries.OrderBy(x => x.Span.StartUtf16))
            text.Append(entry.Span.StartUtf16).Append(':').Append(entry.Span.LengthUtf16).Append('|')
                .Append(entry.Sha256).Append('|').Append(entry.Status).Append('|').Append(entry.TargetChapterId).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string Title(ImportPreviewItem item) => string.IsNullOrWhiteSpace(item.VietnameseTitle)
        ? item.Title.Trim() : item.VietnameseTitle.Trim();
    private static string ChineseTitle(ImportPreviewItem item) => item.Title.Trim();

    private static Guid StableGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private sealed class MutableOutcome(ImportPreviewItem item)
    {
        public ImportPreviewItem Item { get; } = item;
        public BulkImportChapterState State { get; set; } = BulkImportChapterState.NotStarted;
        public string? Error { get; set; }
    }

    private sealed class ArchiveManifest
    {
        public int SchemaVersion { get; set; } = 2;
        public Guid ProjectId { get; set; }
        public Guid ImportId { get; set; }
        public string SourceName { get; set; } = "";
        public string EncodingName { get; set; } = "";
        public string RawSha256 { get; set; } = "";
        public string TextSha256 { get; set; } = "";
        public string PlanFingerprint { get; set; } = "";
        public string CoverageFingerprint { get; set; } = "";
        public List<ArchiveItem> Items { get; set; } = [];
        public List<ArchiveCoverageEntry> Coverage { get; set; } = [];
    }

    private sealed class ArchiveItem
    {
        public Guid PreviewItemId { get; set; }
        public Guid ChapterId { get; set; }
        public PlannedRegionKind Kind { get; set; }
        public int StartUtf16 { get; set; }
        public int LengthUtf16 { get; set; }
        public int ContentStartUtf16 { get; set; }
        public int ContentLengthUtf16 { get; set; }
        public int? Number { get; set; }
        public string Title { get; set; } = "";
        public string? VietnameseTitle { get; set; }
        public string? VolumeTitle { get; set; }
        public string? VolumeIdentity { get; set; }
        public bool Included { get; set; }
    }

    private sealed class ArchiveCoverageEntry
    {
        public int StartUtf16 { get; set; }
        public int LengthUtf16 { get; set; }
        public string Sha256 { get; set; } = "";
        public SourceCoverageStatus Status { get; set; }
        public Guid? PreviewItemId { get; set; }
        public Guid? TargetChapterId { get; set; }
        public string Decision { get; set; } = "";
        public string Evidence { get; set; } = "";
    }
}
