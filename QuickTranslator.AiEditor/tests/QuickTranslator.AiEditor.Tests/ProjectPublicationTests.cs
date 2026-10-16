using System.Security.Cryptography;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ProjectPublicationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-publication-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectRepository _repository = new();

    [Fact]
    public async Task Txt_export_uses_catalog_reading_order_and_canonical_chapter_composer()
    {
        ProjectSession session = await _repository.CreateAsync(Path.Combine(_root, "Novel"), "Truyện thử");
        ChapterDocument second = await AddChapterAsync(session, 2, "Chương hai", "Nội dung hai.");
        ChapterDocument first = await AddChapterAsync(session, 1, "Chương một", "Dòng một.\nDòng hai.");
        ChapterDocument deleted = await AddChapterAsync(session, 3, "Chương đã xóa", "Không được xuất.");
        await _repository.SoftDeleteChapterAsync(session, deleted.Id);
        string path = Path.Combine(_root, "book.txt");
        var service = new ProjectPublicationService(_repository, new ProjectCatalog(_repository));

        ProjectPublicationReport report = await service.ExportTextAsync(session, path,
            new ProjectPublicationOptions(IncludeProjectTitle: true, IncludeChapterTitles: true));

        Assert.Equal(2, report.ExportedChapterCount);
        Assert.Equal(0, report.IncompleteChapterCount);
        Assert.Equal("Truyện thử\n\n\nChương một\n\nDòng một.\nDòng hai.\n\n\nChương hai\n\nNội dung hai.",
            await File.ReadAllTextAsync(path));
        Assert.DoesNotContain("Không được xuất", await File.ReadAllTextAsync(path));
        Assert.Equal("Dòng một.\nDòng hai.", ChapterTranslationComposer.Compose(first).Text);
        Assert.Equal("Nội dung hai.", ChapterTranslationComposer.Compose(second).Text);
    }

    [Fact]
    public async Task Inspection_reports_incomplete_and_review_chapters_without_writing_project()
    {
        ProjectSession session = await _repository.CreateAsync(Path.Combine(_root, "Inspect"), "Truyện kiểm tra");
        ChapterDocument complete = await AddChapterAsync(session, 1, "Hoàn tất", "Đã dịch.");
        ChapterDocument incomplete = TranslationService.CreateChapter("Còn thiếu", "第二章 原文。");
        incomplete.Segments[0].UserDraft = "";
        await _repository.AddChapterAsync(session, incomplete, 2);
        ChapterDocument review = await AddChapterAsync(session, 3, "Cần xem", "Bản nháp.");
        review.Segments[0].Status = SegmentStatus.InspectionRequired;
        await _repository.UpdateChapterAsync(session, review);
        Dictionary<string, string> before = HashProjectFiles(session.RootDirectory);
        var service = new ProjectPublicationService(_repository, new ProjectCatalog(_repository));

        ProjectPublicationInspection inspection = await service.InspectAsync(session);

        Assert.Equal(3, inspection.TotalChapterCount);
        Assert.Equal(1, inspection.IncompleteChapterCount);
        Assert.Equal(1, inspection.InspectionChapterCount);
        Assert.Equal(before, HashProjectFiles(session.RootDirectory));
        Assert.NotNull(complete);
    }

    [Fact]
    public async Task Incomplete_export_requires_explicit_permission_and_preserves_existing_target()
    {
        ProjectSession session = await _repository.CreateAsync(Path.Combine(_root, "Incomplete"), "Truyện dở");
        ChapterDocument chapter = TranslationService.CreateChapter("Chương dở", "原文。");
        await _repository.AddChapterAsync(session, chapter, 1);
        string path = Path.Combine(_root, "existing.txt");
        await File.WriteAllTextAsync(path, "Bản xuất cũ");
        var service = new ProjectPublicationService(_repository, new ProjectCatalog(_repository));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportTextAsync(session, path,
            new ProjectPublicationOptions(AllowIncomplete: false)));

        Assert.Equal("Bản xuất cũ", await File.ReadAllTextAsync(path));
        ProjectPublicationReport report = await service.ExportTextAsync(session, path,
            new ProjectPublicationOptions(AllowIncomplete: true));
        Assert.Equal(1, report.IncompleteChapterCount);
    }

    [Fact]
    public async Task Read_failure_mid_export_never_replaces_existing_file()
    {
        ProjectSession session = await _repository.CreateAsync(Path.Combine(_root, "Fault"), "Truyện lỗi");
        await AddChapterAsync(session, 1, "Một", "Một.");
        await AddChapterAsync(session, 2, "Hai", "Hai.");
        string path = Path.Combine(_root, "existing.txt");
        await File.WriteAllTextAsync(path, "Giữ nguyên");
        int reads = 0;
        _repository.FaultPoint = point =>
        {
            if (point == "BeforeChapterRead" && Interlocked.Increment(ref reads) == 2)
                throw new IOException("Synthetic read failure");
        };
        var service = new ProjectPublicationService(_repository, new ProjectCatalog(_repository));

        try
        {
            await Assert.ThrowsAsync<IOException>(() => service.ExportTextAsync(session, path,
                new ProjectPublicationOptions()));
            Assert.Equal("Giữ nguyên", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(_root, "*.publication-*.tmp", SearchOption.AllDirectories));
        }
        finally { _repository.FaultPoint = null; }
    }

    [Fact]
    public async Task Project_change_after_inspection_refuses_stale_export_and_preserves_target()
    {
        ProjectSession session = await _repository.CreateAsync(Path.Combine(_root, "Revision"), "Truyện đổi");
        ChapterDocument chapter = await AddChapterAsync(session, 1, "Một", "Bản một.");
        var service = new ProjectPublicationService(_repository, new ProjectCatalog(_repository));
        ProjectPublicationInspection inspection = await service.InspectAsync(session);
        string path = Path.Combine(_root, "revision.txt");
        await File.WriteAllTextAsync(path, "Bản xuất trước");
        chapter.Segments[0].UserDraft = "Bản hai.";
        await _repository.UpdateChapterAsync(session, chapter);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportTextAsync(session, inspection, path,
            new ProjectPublicationOptions()));

        Assert.Equal("Bản xuất trước", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Export_refuses_every_destination_inside_project_data_directory()
    {
        ProjectSession session = await _repository.CreateAsync(Path.Combine(_root, "Managed"), "Truyện an toàn");
        await AddChapterAsync(session, 1, "Một", "Bản dịch.");
        var service = new ProjectPublicationService(_repository, new ProjectCatalog(_repository));
        string managedPath = session.Resolve(session.Manifest.Files.ContextNotes);
        Directory.CreateDirectory(Path.GetDirectoryName(managedPath)!);
        await File.WriteAllTextAsync(managedPath, "Ngữ cảnh cần bảo vệ.");
        string original = await File.ReadAllTextAsync(managedPath);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportTextAsync(session, managedPath, new ProjectPublicationOptions()));

        Assert.Contains("bên ngoài", error.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(managedPath));
    }

    [Fact]
    public async Task Chapter_range_300_to_end_exports_only_selected_chapters_in_catalog_order()
    {
        ProjectSession session = await _repository.CreateAsync(Path.Combine(_root, "Range"), "Truyện 500 chương");
        await AddChapterAsync(session, 299, "Chương 299", "Không xuất.");
        await AddChapterAsync(session, 500, "Chương 500", "Cuối truyện.");
        await AddChapterAsync(session, 301, "Chương 301", "Tiếp theo.");
        await AddChapterAsync(session, 300, "Chương 300", "Bắt đầu.");
        var service = new ProjectPublicationService(_repository, new ProjectCatalog(_repository));
        var range = new ProjectPublicationRange(StartChapterNumber: 300);
        Dictionary<string, string> original = HashProjectFiles(session.RootDirectory);

        ProjectPublicationInspection inspection = await service.InspectAsync(session, range: range);
        Assert.Equal(3, inspection.TotalChapterCount);
        Assert.Equal(range, inspection.Range);

        string path = Path.Combine(_root, "range-300.txt");
        ProjectPublicationReport report = await service.ExportTextAsync(session, inspection, path,
            new ProjectPublicationOptions(IncludeProjectTitle: false));

        Assert.Equal(3, report.ExportedChapterCount);
        string output = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("Chương 299", output);
        Assert.DoesNotContain("Không xuất.", output);
        Assert.True(output.IndexOf("Chương 300", StringComparison.Ordinal) <
            output.IndexOf("Chương 301", StringComparison.Ordinal));
        Assert.True(output.IndexOf("Chương 301", StringComparison.Ordinal) <
            output.IndexOf("Chương 500", StringComparison.Ordinal));
        Assert.Equal(original, HashProjectFiles(session.RootDirectory));
    }

    [Fact]
    public async Task Range_checks_only_selected_chapters_and_supports_explicit_end()
    {
        ProjectSession session = await _repository.CreateAsync(Path.Combine(_root, "RangeValidation"), "Truyện thử");
        ChapterDocument untranslated = TranslationService.CreateChapter("Chương 299", "原文未翻译。");
        await _repository.AddChapterAsync(session, untranslated, 299);
        await AddChapterAsync(session, 300, "Chương 300", "Đã dịch 300.");
        await AddChapterAsync(session, 301, "Chương 301", "Đã dịch 301.");
        await AddChapterAsync(session, 500, "Chương 500", "Đã dịch 500.");
        var service = new ProjectPublicationService(_repository, new ProjectCatalog(_repository));
        var range = new ProjectPublicationRange(300, 301);

        ProjectPublicationInspection inspection = await service.InspectAsync(session, range: range);
        Assert.Equal(2, inspection.TotalChapterCount);
        Assert.False(inspection.RequiresConfirmation);
        string path = Path.Combine(_root, "range-300-301.txt");
        await service.ExportTextAsync(session, inspection, path, new ProjectPublicationOptions());
        string exported = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("Chương 299", exported);
        Assert.DoesNotContain("Chương 500", exported);
        Assert.Contains("Chương 300", exported);
        Assert.Contains("Chương 301", exported);
        Assert.Equal(4, (await service.InspectAsync(session)).TotalChapterCount);
    }

    [Fact]
    public async Task Invalid_or_missing_range_boundaries_are_rejected_not_silently_shifted()
    {
        ProjectSession session = await _repository.CreateAsync(Path.Combine(_root, "InvalidRange"), "Truyện thử");
        await AddChapterAsync(session, 300, "Chương 300", "Đã dịch.");
        await AddChapterAsync(session, 500, "Chương 500", "Đã dịch.");
        var service = new ProjectPublicationService(_repository, new ProjectCatalog(_repository));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.InspectAsync(session, range: new ProjectPublicationRange(500, 300)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InspectAsync(session, range: new ProjectPublicationRange(299)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InspectAsync(session, range: new ProjectPublicationRange(300, 501)));
    }

    private async Task<ChapterDocument> AddChapterAsync(ProjectSession session, int number, string title, string text)
    {
        ChapterDocument chapter = TranslationService.CreateChapter(title, $"第{number}章 原文。");
        chapter.Segments[0].UserDraft = text;
        chapter.Segments[0].Status = SegmentStatus.AwaitingReview;
        await _repository.AddChapterAsync(session, chapter, number);
        return chapter;
    }

    private static Dictionary<string, string> HashProjectFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    public void Dispose()
    {
        _repository.FaultPoint = null;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
