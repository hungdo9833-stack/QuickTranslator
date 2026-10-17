using System.Text;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class SafeBulkImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-m2-bulk-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(500)]
    [InlineData(5000)]
    public async Task Imports_1_50_500_or_5000_chapters_and_reopens_without_orphans(int count)
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "p-" + count), "Synthetic " + count);
        ImportPreviewPlan plan = Plan(count);
        var service = new SafeBulkImportService(repository);

        BulkImportReport report = await service.ImportAsync(session, plan, Defaults(),
            new BulkImportOptions(MaxChaptersPerChunk: 500, MaxUtf8BytesPerChunk: 4 * 1024 * 1024));

        Assert.True(report.Completed);
        Assert.True(report.PostCommitVerified);
        Assert.True(report.VerifiedComplete);
        Assert.False(report.PartialImport);
        Assert.Equal(plan.Fingerprint, report.PlanFingerprint);
        Assert.Equal(count, report.ImportedCount);
        Assert.True(File.Exists(report.SourceArchivePath));
        Assert.Equal(plan.Source.RawSha256, report.SourceRawSha256);
        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.Equal(count, reopened.Manifest.ChapterCount);
        Assert.False(reopened.RecoveryReport.RequiresDecision);
        Assert.Empty(reopened.RecoveryReport.Orphans);
        Assert.Equal(count, Directory.GetFiles(reopened.Resolve("Chapters"), "*.qtaie.json").Length);
    }

    [Fact]
    public async Task Preflight_collision_rejects_everything_before_source_rescue_or_chapter_write()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "collision"), "Synthetic collision");
        ChapterDocument existing = TranslationService.CreateChapter("Có sẵn", "原文。");
        await repository.AddChapterAsync(session, existing, 1);
        byte[] existingBytes = File.ReadAllBytes(session.ChapterStore.GetPath(existing.Id));

        BulkImportValidationException error = await Assert.ThrowsAsync<BulkImportValidationException>(() =>
            new SafeBulkImportService(repository).ImportAsync(session, Plan(2), Defaults()));

        Assert.Contains(error.Issues, x => x.Contains("số chương 1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(existingBytes, File.ReadAllBytes(session.ChapterStore.GetPath(existing.Id)));
        Assert.Single(session.Manifest.Chapters);
        Assert.Empty(Directory.GetFiles(session.Resolve("Backups"), "detected-source-*.zip"));
    }

    [Fact]
    public async Task Existing_approved_draft_history_and_project_glossary_are_byte_identical_after_import()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "protected"), "Synthetic protected");
        ChapterDocument protectedChapter = TranslationService.CreateChapter("Đã biên tập", "原文。第二句。");
        await repository.AddChapterAsync(session, protectedChapter, 1);
        protectedChapter = (await repository.LoadChapterAsync(session, protectedChapter.Id)).Chapter;
        TranslationSegment segment = protectedChapter.Segments[0];
        segment.UserDraft = "Bản nháp người dùng"; segment.DraftRevision = 3;
        segment.DraftHistory.Add(new() { RecordedAt = DateTimeOffset.UtcNow, Revision = 2, Text = "Lịch sử", Reason = "test" });
        segment.Approvals.Add(new() { ApprovedAt = DateTimeOffset.UtcNow, DraftRevision = 3, Text = "Bản đã duyệt" });
        segment.Status = SegmentStatus.Approved;
        await session.ChapterStore.SaveAsync(protectedChapter);
        var resources = await new ProjectResourceStore(session).LoadAsync();
        resources.Terms.Add(new("王", "Vương"));
        await new ProjectResourceStore(session).SaveAsync(resources);
        string chapterPath = session.ChapterStore.GetPath(protectedChapter.Id);
        string resourcesPath = session.Resolve("Settings/resources.json");
        byte[] chapterBefore = File.ReadAllBytes(chapterPath), resourcesBefore = File.ReadAllBytes(resourcesPath);

        ImportPreviewPlan plan = Plan(3, firstNumber: 10);
        BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(session, plan, Defaults());

        Assert.True(report.Completed);
        Assert.Equal(chapterBefore, File.ReadAllBytes(chapterPath));
        Assert.Equal(resourcesBefore, File.ReadAllBytes(resourcesPath));
        ChapterDocument reloaded = (await repository.LoadChapterAsync(session, protectedChapter.Id)).Chapter;
        Assert.Equal("Bản nháp người dùng", reloaded.Segments[0].UserDraft);
        Assert.Single(reloaded.Segments[0].Approvals);
        Assert.Single(reloaded.Segments[0].DraftHistory);
    }

    [Theory]
    [InlineData("AfterJournal")]
    [InlineData("AfterFile:1")]
    [InlineData("BeforeCommit")]
    public async Task Fault_at_start_middle_or_end_rolls_back_the_entire_chunk(string point)
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "fault-" + point.Replace(':', '-')), "Synthetic fault");
        repository.FaultPoint = value => { if (value == point) throw new IOException("There is not enough space on the disk. Injected."); };

        BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(session, Plan(5), Defaults(),
            new BulkImportOptions(5, 1024 * 1024));

        Assert.False(report.Completed);
        Assert.Equal(0, report.ImportedCount);
        Assert.All(report.Chapters, x => Assert.Contains(x.State, new[] { BulkImportChapterState.RolledBack, BulkImportChapterState.NotStarted }));
        Assert.Empty(session.Manifest.Chapters);
        Assert.Empty(Directory.GetFiles(session.Resolve("Chapters"), "*.qtaie.json"));
        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.False(reopened.RecoveryReport.RequiresDecision);
        Assert.Empty(reopened.RecoveryReport.Orphans);
    }

    [Fact]
    public async Task Cancel_between_chunks_reports_partial_success_and_retry_is_idempotent()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "cancel"), "Synthetic cancel");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value => { if (value.CompletedChunks == 1) cancellation.Cancel(); });
        var service = new SafeBulkImportService(repository);
        ImportPreviewPlan plan = Plan(7);

        BulkImportReport first = await service.ImportAsync(session, plan, Defaults(), new BulkImportOptions(3, 1024 * 1024), progress, cancellation.Token);
        Assert.False(first.Completed);
        Assert.Equal(3, first.ImportedCount);
        Assert.Equal(4, first.Chapters.Count(x => x.State == BulkImportChapterState.NotStarted));

        BulkImportReport retry = await service.ImportAsync(session, plan, Defaults(), new BulkImportOptions(3, 1024 * 1024));
        Assert.True(retry.Completed);
        Assert.Equal(3, retry.AlreadyImportedCount);
        Assert.Equal(4, retry.ImportedCount);
        Assert.Equal(7, session.Manifest.ChapterCount);
        Assert.Equal(7, session.Manifest.Chapters.Select(x => x.ChapterId).Distinct().Count());
    }

    [Fact]
    public async Task Cancel_inside_a_chunk_rolls_back_every_chapter_in_that_chunk()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "cancel-inside"), "Synthetic cancel inside chunk");
        repository.FaultPoint = point =>
        {
            if (point == "AfterFile:1") throw new OperationCanceledException("Injected cancellation while publishing a chunk.");
        };

        BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(
            session, Plan(5), Defaults(), new BulkImportOptions(5, 1024 * 1024));

        Assert.False(report.Completed);
        Assert.Equal(0, report.ImportedCount);
        Assert.All(report.Chapters, chapter => Assert.Equal(BulkImportChapterState.RolledBack, chapter.State));
        Assert.Empty(session.Manifest.Chapters);
        Assert.Empty(Directory.GetFiles(session.Resolve("Chapters"), "*.qtaie.json"));
        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.False(reopened.RecoveryReport.RequiresDecision);
        Assert.Empty(reopened.RecoveryReport.Orphans);
    }

    [Fact]
    public async Task Corrupt_existing_source_rescue_blocks_resume_before_any_target_write()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "corrupt-rescue"), "Synthetic corrupt rescue");
        ImportPreviewPlan plan = Plan(3);
        var service = new SafeBulkImportService(repository);
        BulkImportReport first = await service.ImportAsync(session, plan, Defaults());
        Assert.True(first.Completed);

        byte[] manifestBefore = File.ReadAllBytes(session.ManifestPath);
        Dictionary<string, byte[]> chaptersBefore = Directory.GetFiles(session.Resolve("Chapters"), "*.qtaie.json")
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(first.SourceArchivePath, "corrupt rescue archive");

        BulkImportReport retry = await service.ImportAsync(session, plan, Defaults());

        Assert.False(retry.Completed);
        Assert.Contains("source rescue", retry.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, retry.AlreadyImportedCount);
        Assert.Equal(manifestBefore, File.ReadAllBytes(session.ManifestPath));
        Assert.Equal(chaptersBefore.Keys.Order(), Directory.GetFiles(session.Resolve("Chapters"), "*.qtaie.json")
            .Select(path => Path.GetFileName(path)!).Order());
        foreach ((string fileName, byte[] before) in chaptersBefore)
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(session.Resolve("Chapters"), fileName)));
    }

    [Fact]
    public async Task Simulated_crash_reopens_with_recovery_then_same_plan_resumes_without_duplicates()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "crash"), "Synthetic crash");
        repository.FaultPoint = point => { if (point == "BeforeCommit") throw new ProjectOperationInterruptedException(point); };
        var service = new SafeBulkImportService(repository);
        ImportPreviewPlan plan = Plan(4);

        await Assert.ThrowsAsync<ProjectOperationInterruptedException>(() => service.ImportAsync(session, plan, Defaults(), new BulkImportOptions(4, 1024 * 1024)));
        var afterCrashRepository = new ProjectRepository();
        ProjectSession reopened = (await afterCrashRepository.OpenAsync(session.ManifestPath)).Session;
        ProjectTransactionPreview pending = Assert.Single(reopened.RecoveryReport.Transactions,
            x => x.State == ProjectTransactionState.Pending);
        await afterCrashRepository.RecoverTransactionAsync(reopened, pending.Id, ProjectRecoveryDecision.RollBack);

        BulkImportReport retry = await new SafeBulkImportService(afterCrashRepository).ImportAsync(reopened, plan, Defaults(), new BulkImportOptions(4, 1024 * 1024));
        Assert.True(retry.Completed);
        Assert.Equal(4, reopened.Manifest.ChapterCount);
        Assert.Equal(4, reopened.Manifest.Chapters.Select(x => x.ChapterId).Distinct().Count());
    }

    [Fact]
    public async Task Unordered_source_blocks_different_duplicate_until_explicit_partial_import_decision()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "unordered"), "Synthetic unordered");
        const string source = "第4章 四\n正文四。\n第2章 二（前）\n正文二前。\n第3章 三\n正文三。\n第2章 二（后）\n正文二后。\n第1章 一\n正文一。";
        var editor = ImportPreviewEditor.Create(new ChapterDetector().Detect(source, "unordered.txt"));
        ImportPreviewPlan unresolved = editor.Current;

        await Assert.ThrowsAsync<BulkImportValidationException>(() =>
            new SafeBulkImportService(repository).ImportAsync(session, unresolved, Defaults()));
        Assert.Empty(session.Manifest.Chapters);
        editor.SetIncluded(editor.Current.Chapters.Last(x => x.Number == 2).Id, false);
        ImportPreviewPlan plan = editor.Current;
        BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(session, plan, Defaults());

        Assert.True(report.Completed);
        Assert.True(report.PostCommitVerified);
        Assert.True(report.PartialImport);
        Assert.False(report.VerifiedComplete);
        Assert.Equal([1, 2, 3, 4], report.Chapters.Select(chapter => chapter.Number));
        Assert.Equal([1, 2, 3, 4], session.Manifest.Chapters.Select(chapter => chapter.Number));
        ProjectChapterEntry chapterTwo = session.Manifest.Chapters.Single(chapter => chapter.Number == 2);
        ChapterDocument loaded = (await repository.LoadChapterAsync(session, chapterTwo.ChapterId)).Chapter;
        Assert.Equal("二（前）", loaded.Title);
        Assert.Contains("正文二前。", loaded.OriginalText, StringComparison.Ordinal);
        Assert.DoesNotContain("正文二后。", loaded.OriginalText, StringComparison.Ordinal);
        Assert.Equal(source, plan.ReconstructSource());
    }

    [Fact]
    public async Task Postcommit_verification_failure_never_reports_complete_and_retry_reverifies_idempotently()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "postverify"), "Synthetic postverify");
        ImportPreviewPlan plan = Plan(3);
        var service = new SafeBulkImportService(repository)
        {
            VerificationFaultPoint = point =>
            {
                if (point == "BeforePostCommitVerify") throw new IOException("Injected verification read failure.");
            }
        };

        BulkImportReport first = await service.ImportAsync(session, plan, Defaults());

        Assert.False(first.Completed);
        Assert.False(first.PostCommitVerified);
        Assert.Contains("verification", first.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session.Manifest.ChapterCount);

        var retryRepository = new ProjectRepository();
        ProjectSession reopened = (await retryRepository.OpenAsync(session.ManifestPath)).Session;
        BulkImportReport retry = await new SafeBulkImportService(retryRepository)
            .ImportAsync(reopened, plan, Defaults());
        Assert.True(retry.Completed);
        Assert.True(retry.PostCommitVerified);
        Assert.True(retry.VerifiedComplete);
        Assert.Equal(3, retry.AlreadyImportedCount);
    }

    [Fact]
    public async Task Explicit_history_replacement_deletes_soft_deleted_copy_and_imports_same_id()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "replace-history"), "Synthetic replace history");
        ImportPreviewPlan plan = Plan(2);
        var oldPaths = new List<string>();
        foreach (ImportPreviewItem item in plan.ImportableChapters)
        {
            ChapterDocument old = ChapterWithId(item.ChapterId, "Bản cũ", "旧内容。", session.Manifest.ProjectId);
            await repository.AddChapterAsync(session, old, item.Number!.Value);
            await repository.SoftDeleteChapterAsync(session, old.Id);
            oldPaths.Add(session.Manifest.Chapters.Single(x => x.ChapterId == old.Id).RelativePath);
        }

        await Assert.ThrowsAsync<BulkImportValidationException>(() =>
            new SafeBulkImportService(repository).ImportAsync(session, plan, Defaults()));
        Assert.Equal(2, session.Manifest.DeletedChapterCount);
        Assert.All(oldPaths, relative => Assert.True(File.Exists(session.Resolve(relative))));

        BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(session, plan, Defaults(),
            new BulkImportOptions(ReplaceSoftDeletedHistory: true));

        Assert.True(report.Completed);
        Assert.Equal(2, report.ImportedCount);
        Assert.Equal(2, session.Manifest.ChapterCount);
        Assert.Equal(0, session.Manifest.DeletedChapterCount);
        Assert.Equal(plan.ImportableChapters.Select(x => x.ChapterId).Order(),
            session.Manifest.Chapters.Select(x => x.ChapterId).Order());
        Assert.All(oldPaths, relative => Assert.False(File.Exists(session.Resolve(relative))));
        foreach (ImportPreviewItem item in plan.ImportableChapters)
        {
            ChapterDocument imported = (await repository.LoadChapterAsync(session, item.ChapterId)).Chapter;
            Assert.Equal(plan.Slice(item.ContentSpan), imported.OriginalText);
        }
        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.False(reopened.RecoveryReport.RequiresDecision);
        Assert.Empty(reopened.RecoveryReport.Orphans);
    }

    [Fact]
    public async Task History_replacement_failure_rolls_back_deleted_copy_and_new_chapter()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "replace-history-rollback"), "Synthetic rollback");
        ImportPreviewPlan plan = Plan(1);
        ImportPreviewItem item = Assert.Single(plan.ImportableChapters);
        ChapterDocument old = ChapterWithId(item.ChapterId, "Bản cũ", "旧内容。", session.Manifest.ProjectId);
        await repository.AddChapterAsync(session, old, item.Number!.Value);
        await repository.SoftDeleteChapterAsync(session, old.Id);
        ProjectChapterEntry deleted = Assert.Single(session.Manifest.Chapters);
        string historyPath = session.Resolve(deleted.RelativePath);
        byte[] historyBefore = await File.ReadAllBytesAsync(historyPath);
        repository.FaultPoint = point => { if (point == "AfterFile:1") throw new IOException("Injected disk failure."); };

        BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(session, plan, Defaults(),
            new BulkImportOptions(ReplaceSoftDeletedHistory: true));

        Assert.False(report.Completed);
        Assert.Equal(0, report.ImportedCount);
        ProjectChapterEntry restored = Assert.Single(session.Manifest.Chapters);
        Assert.NotNull(restored.DeletedAt);
        Assert.Equal(deleted.RelativePath, restored.RelativePath);
        Assert.Equal(historyBefore, await File.ReadAllBytesAsync(historyPath));
        Assert.False(File.Exists(session.ChapterStore.GetPath(item.ChapterId)));
        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.False(reopened.RecoveryReport.RequiresDecision);
        Assert.Empty(reopened.RecoveryReport.Orphans);
    }

    private static ChapterDocument ChapterWithId(Guid id, string title, string source, Guid projectId)
    {
        ChapterDocument chapter = TranslationService.CreateChapter(title, source);
        chapter.Id = id;
        chapter.ProjectId = projectId;
        chapter.Segments = new TextSegmenter().Segment(chapter.OriginalText, chapter.Id, new()).ToList();
        chapter.SelectedSegmentId = chapter.Segments[0].Id;
        return chapter;
    }

    private static ImportPreviewPlan Plan(int count, int firstNumber = 1)
    {
        string source = string.Concat(Enumerable.Range(firstNumber, count).Select(number => $"第{number}章 标题{number}\n原文{number}。\n"));
        return ImportPreviewEditor.Create(new ChapterDetector().Detect(source, $"synthetic-{count}.txt")).Current;
    }

    private static BulkImportChapterDefaults Defaults() => new(new(), new(), PromptPreset.Default);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class InlineProgress(Action<BulkImportProgress> action) : IProgress<BulkImportProgress>
    {
        public void Report(BulkImportProgress value) => action(value);
    }
}
