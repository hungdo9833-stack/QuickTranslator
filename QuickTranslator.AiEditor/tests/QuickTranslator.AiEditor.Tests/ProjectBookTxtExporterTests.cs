using System.Text;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ProjectBookTxtExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "qt-book-export-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExportsAllChaptersInNumberOrder_AndDoesNotModifyProject()
    {
        var repository = new ProjectRepository();
        var session = await repository.CreateAsync(Path.Combine(_root, "novel"), "Truyện mẫu");
        await AddChapterAsync(repository, session, 3, "Ba", "Nội dung ba");
        await AddChapterAsync(repository, session, 1, "Một", "Nội dung một");
        await AddChapterAsync(repository, session, 2, "Hai", "Nội dung hai");
        var originals = session.Manifest.Chapters.ToDictionary(x => x.ChapterId,
            x => File.ReadAllBytes(session.Resolve(x.RelativePath)));
        string output = Path.Combine(_root, "all.txt");

        var result = await ProjectBookTxtExporter.ExportAsync(repository, session, output);
        string text = await File.ReadAllTextAsync(output, Encoding.UTF8);

        Assert.Equal(3, result.ExportedChapters);
        Assert.Equal(0, result.IncompleteChapters);
        Assert.Equal("Truyện mẫu\n\nChương 1: Một\n\nNội dung một\n\n" +
                     "Chương 2: Hai\n\nNội dung hai\n\n" +
                     "Chương 3: Ba\n\nNội dung ba\n", text.Replace("\r\n", "\n"));
        Assert.All(originals, entry => Assert.Equal(entry.Value,
            File.ReadAllBytes(session.Resolve(session.Manifest.Chapters.Single(x => x.ChapterId == entry.Key).RelativePath))));
        Assert.Equal(3, session.Manifest.ChapterCount);
    }

    [Fact]
    public async Task IncompleteChapter_DoesNotReplaceExistingExport()
    {
        var repository = new ProjectRepository();
        var session = await repository.CreateAsync(Path.Combine(_root, "novel"), "Truyện mẫu");
        await AddChapterAsync(repository, session, 1, "Một", "Dịch đầy đủ");
        var incomplete = TranslationService.CreateChapter("Hai", "原文。");
        await repository.AddChapterAsync(session, incomplete, 2);
        string output = Path.Combine(_root, "all.txt");
        await File.WriteAllTextAsync(output, "EXISTING");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectBookTxtExporter.ExportAsync(repository, session, output));
        Assert.Equal("EXISTING", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));

        var result = await ProjectBookTxtExporter.ExportAsync(repository, session, output, allowIncomplete: true);
        Assert.Equal(1, result.IncompleteChapters);
        Assert.Contains("Chương 2", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task RejectsOutputInsideProjectFolder()
    {
        var repository = new ProjectRepository();
        var session = await repository.CreateAsync(Path.Combine(_root, "novel"), "Truyện mẫu");
        await AddChapterAsync(repository, session, 1, "Một", "Có dịch");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectBookTxtExporter.ExportAsync(repository, session,
                Path.Combine(session.RootDirectory, "Backups", "book.txt")));
    }

    private static async Task AddChapterAsync(ProjectRepository repository, ProjectSession session,
        int number, string title, string translation)
    {
        var chapter = TranslationService.CreateChapter(title, "原文。");
        chapter.Segments[0].UserDraft = translation;
        chapter.Segments[0].DraftRevision = 1;
        await repository.AddChapterAsync(session, chapter, number);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
