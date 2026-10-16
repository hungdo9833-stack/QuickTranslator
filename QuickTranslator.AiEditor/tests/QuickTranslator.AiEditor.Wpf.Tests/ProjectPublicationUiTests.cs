using System.IO;
using System.Windows;
using System.Windows.Controls;
using PdfSharp.Pdf.IO;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ProjectPublicationUiTests
{
    [Fact]
    public Task Workspace_sidebar_exposes_the_publication_command() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();

        var button = Assert.IsType<Button>(fixture.Window.FindName("PublishNavButton"));

        Assert.Equal("Publish", button.Tag);
        Assert.Contains("Xuất bản", button.Content?.ToString());
        return Task.CompletedTask;
    });

    [Fact]
    public Task Publication_window_inspects_active_chapters_and_requires_confirmation_for_drafts() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        ChapterDocument translated = await fixture.AddChapterAsync("Đã dịch", 1);
        translated.Segments[0].UserDraft = "Bản dịch.";
        translated.Segments[0].Status = SegmentStatus.AwaitingReview;
        await fixture.Repository.UpdateChapterAsync(fixture.Project, translated);
        await fixture.AddChapterAsync("Chưa dịch", 2);
        var service = new ProjectPublicationService(fixture.Repository,
            fixture.Get<ProjectCatalog>("_projectCatalog"));
        var window = new ProjectPublicationWindow(fixture.Project, service);
        try
        {
            await window.LoadInspectionAsync();

            Assert.Equal(2, window.CurrentInspection?.TotalChapterCount);
            Assert.Equal(1, window.CurrentInspection?.IncompleteChapterCount);
            Assert.Equal(Visibility.Visible, ((CheckBox)window.FindName("AllowIncompleteInput")).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("IssueChaptersPanel")).Visibility);
            Assert.Single(((ListBox)window.FindName("IssueChaptersList")).Items);
            Assert.NotNull(window.FindName("OpenExportedFileButton"));
            Assert.NotNull(window.FindName("OpenExportFolderButton"));
            Assert.False(((Button)window.FindName("ExportButton")).IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Publication_window_checks_selected_range_and_disables_export_when_range_changes() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        ChapterDocument old = await fixture.AddChapterAsync("Chương 299", 299);
        ChapterDocument selected = await fixture.AddChapterAsync("Chương 300", 300);
        selected.Segments[0].UserDraft = "Bản dịch 300.";
        selected.Segments[0].Status = SegmentStatus.AwaitingReview;
        await fixture.Repository.UpdateChapterAsync(fixture.Project, selected);
        var service = new ProjectPublicationService(fixture.Repository, fixture.Get<ProjectCatalog>("_projectCatalog"));
        var window = new ProjectPublicationWindow(fixture.Project, service);
        try
        {
            await window.LoadInspectionAsync();
            Assert.Equal(2, window.CurrentInspection?.TotalChapterCount);
            var from = Assert.IsType<TextBox>(window.FindName("StartChapterInput"));
            var to = Assert.IsType<TextBox>(window.FindName("EndChapterInput"));
            from.Text = "300";
            Assert.False(((Button)window.FindName("ExportButton")).IsEnabled);
            await window.LoadInspectionAsync();
            Assert.Equal(1, window.CurrentInspection?.TotalChapterCount);
            Assert.False(window.CurrentInspection?.RequiresConfirmation);
            Assert.Equal(300, window.CurrentInspection?.Range?.StartChapterNumber);
            to.Text = "299";
            Assert.False(((Button)window.FindName("ExportButton")).IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Pdf_export_creates_a_readable_unicode_book_without_changing_project_data() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        ChapterDocument first = await fixture.AddChapterAsync("Chương 1: Khởi đầu", 1);
        first.Segments[0].UserDraft = "Vương Tú Lan bước vào. Tiếng Việt có dấu.";
        first.Segments[0].Status = SegmentStatus.Approved;
        await fixture.Repository.UpdateChapterAsync(fixture.Project, first);
        ChapterDocument second = await fixture.AddChapterAsync("Chương 2: Gặp lại", 2);
        second.Segments[0].UserDraft = "Nàng nói: “Chúng ta đi thôi.”";
        second.Segments[0].Status = SegmentStatus.AwaitingReview;
        await fixture.Repository.UpdateChapterAsync(fixture.Project, second);
        var service = new ProjectPublicationService(fixture.Repository,
            fixture.Get<ProjectCatalog>("_projectCatalog"));
        ProjectPublicationInspection inspection = await service.InspectAsync(fixture.Project);
        string path = Path.Combine(fixture.Root, "book.pdf");

        ProjectPublicationReport report = await ProjectPdfExporter.ExportAsync(service, fixture.Project,
            inspection, path, new ProjectPublicationOptions());

        Assert.Equal(2, report.ExportedChapterCount);
        Assert.True(new FileInfo(path).Length > 1_000);
        byte[] header = new byte[5];
        await using (FileStream stream = File.OpenRead(path))
            Assert.Equal(5, await stream.ReadAsync(header));
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(header));
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount >= 3);
        Assert.Equal("Truyện thử tổng hợp", pdf.Info.Title);
    });
}
