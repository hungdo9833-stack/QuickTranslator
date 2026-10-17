using System.IO;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ImportPreviewUiTests
{
    [Fact]
    public Task Reimport_after_soft_delete_requires_explicit_history_replacement() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        const string source = "第1章 第一章\n原文一。\n第2章 第二章\n原文二。\n";
        ImportPreviewPlan originalPlan = ImportPreviewEditor.Create(
            new ChapterDetector().Detect(source, "same-source.txt")).Current;
        foreach (ImportPreviewItem item in originalPlan.ImportableChapters)
        {
            var chapter = TranslationService.CreateChapter(item.Title, originalPlan.Slice(item.ContentSpan));
            chapter.Id = item.ChapterId;
            chapter.Segments = new TextSegmenter().Segment(chapter.OriginalText, chapter.Id, new()).ToList();
            chapter.SelectedSegmentId = chapter.Segments[0].Id;
            await fixture.Repository.AddChapterAsync(fixture.Project, chapter, item.Number!.Value);
            await fixture.Repository.SoftDeleteChapterAsync(fixture.Project, chapter.Id);
        }
        Guid[] deletedIds = fixture.Project.Manifest.Chapters.Select(item => item.ChapterId).ToArray();
        string path = Path.Combine(fixture.Root, "same-source.txt");
        await File.WriteAllTextAsync(path, source);

        var window = new ImportPreviewWindow(fixture.Project);
        try
        {
            await window.LoadSourceAsync(path, SourceTextEncoding.Auto);

            var replace = Assert.IsType<CheckBox>(window.FindName("ReplaceHistoryCheck"));
            Assert.True(replace.IsEnabled);
            Assert.False(((Button)window.FindName("ConfirmButton")).IsEnabled);

            replace.IsChecked = true;

            Assert.True(((Button)window.FindName("ConfirmButton")).IsEnabled);
            Assert.Equal(deletedIds.Order(), window.CurrentPlan!.ImportableChapters
                .Select(item => item.ChapterId).Order());
            Assert.Equal(2, fixture.Project.Manifest.DeletedChapterCount);
            Assert.Contains("xóa vĩnh viễn", ((TextBlock)window.FindName("FooterHintText")).Text,
                StringComparison.CurrentCultureIgnoreCase);
            Assert.Equal(source, window.CurrentPlan.ReconstructSource());
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task History_replacement_does_not_unlock_an_active_number_collision() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        const string source = "第1章 第一章\n原文一。\n";
        ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(source, "same-source.txt")).Current;
        ImportPreviewItem item = Assert.Single(plan.ImportableChapters);
        var old = TranslationService.CreateChapter(item.Title, plan.Slice(item.ContentSpan));
        old.Id = item.ChapterId;
        old.Segments = new TextSegmenter().Segment(old.OriginalText, old.Id, new()).ToList();
        old.SelectedSegmentId = old.Segments[0].Id;
        await fixture.Repository.AddChapterAsync(fixture.Project, old, 1);
        await fixture.Repository.SoftDeleteChapterAsync(fixture.Project, old.Id);
        await fixture.AddChapterAsync("Chương active khác", 1);
        string path = Path.Combine(fixture.Root, "same-source.txt");
        await File.WriteAllTextAsync(path, source);

        var window = new ImportPreviewWindow(fixture.Project);
        try
        {
            await window.LoadSourceAsync(path, SourceTextEncoding.Auto);
            ((CheckBox)window.FindName("ReplaceHistoryCheck")).IsChecked = true;

            Assert.False(((Button)window.FindName("ConfirmButton")).IsEnabled);
            Assert.Contains("Số 1", ((TextBlock)window.FindName("FooterHintText")).Text +
                string.Join(" ", ((ListBox)window.FindName("IssuesList")).Items.Cast<object>()),
                StringComparison.OrdinalIgnoreCase);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Dark_preview_loads_detector_output_and_blocks_project_number_collisions() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        await fixture.AddChapterAsync("Đã có", 1);
        string path = Path.Combine(fixture.Root, "multi.txt");
        await File.WriteAllTextAsync(path, "版权信息\r\n第1章 冲突\r\n甲。\r\n第2章 新章\r\n乙。");
        var window = new ImportPreviewWindow(fixture.Project);
        try
        {
            await window.LoadSourceAsync(path, SourceTextEncoding.Auto);
            Assert.Equal(2, window.CurrentPlan!.Chapters.Count());
            Assert.Equal("版权信息\r\n第1章 冲突\r\n甲。\r\n第2章 新章\r\n乙。", window.CurrentPlan.ReconstructSource());
            Assert.False(((Button)window.FindName("ConfirmButton")).IsEnabled);
            Assert.NotNull(window.FindName("TranslateTitlesCheck"));
            Assert.NotNull(window.FindName("TitleMethodInput"));
            Assert.NotNull(window.FindName("TranslateAllTitlesButton"));
            Assert.NotNull(window.FindName("RetryFailedTitlesButton"));
            Assert.NotNull(window.FindName("RetrySelectedTitlesButton"));
            Assert.NotNull(window.FindName("ConfirmBoundaryButton"));
            Assert.NotNull(window.FindName("RejectBoundaryButton"));
            Assert.Contains("Ledger:", ((TextBlock)window.FindName("CoverageSummaryText")).Text,
                StringComparison.Ordinal);
            Assert.Contains("chưa giải quyết", ((TextBlock)window.FindName("CoverageSummaryText")).Text,
                StringComparison.CurrentCultureIgnoreCase);
            Assert.Contains("Số 1", ((TextBlock)window.FindName("FooterHintText")).Text +
                string.Join(" ", ((ListBox)window.FindName("IssuesList")).Items.Cast<object>()), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(System.Windows.Media.Color.FromRgb(7, 17, 31),
                ((System.Windows.Media.SolidColorBrush)window.Background).Color);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task MainWindow_production_import_path_persists_fifty_chapters_and_reopens_project() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        string source = string.Concat(Enumerable.Range(1, 50).Select(i => $"第{i}章 标题{i}\n原文{i}。\n"));
        ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(source, "fifty.txt")).Current;

        var task = (Task<BulkImportReport>)fixture.Call("ImportDetectedPlanAsync", plan, null, CancellationToken.None)!;
        BulkImportReport report = await task;

        Assert.True(report.Completed);
        Assert.True(report.PostCommitVerified);
        Assert.True(report.VerifiedComplete);
        Assert.Equal(50, report.ImportedCount);
        ProjectSession reopened = (await new ProjectRepository().OpenAsync(fixture.Project.ManifestPath)).Session;
        Assert.Equal(50, reopened.Manifest.ChapterCount);
        Assert.Empty(reopened.RecoveryReport.Orphans);
        Assert.False(reopened.RecoveryReport.RequiresDecision);
        Assert.Equal(source, plan.ReconstructSource());
    });

    [Fact]
    public Task Existing_one_to_one_hundred_can_be_skipped_before_importing_one_hundred_and_one_to_three_hundred() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        for (int number = 1; number <= 100; number++)
            await fixture.AddChapterAsync($"Chương cũ {number}", number);

        string source = string.Concat(Enumerable.Range(1, 300)
            .Select(number => $"第{number}章 标题{number}\n新原文{number}。\n"));
        string path = Path.Combine(fixture.Root, "one-to-three-hundred.txt");
        await File.WriteAllTextAsync(path, source);
        var window = new ImportPreviewWindow(fixture.Project);
        try
        {
            await window.LoadSourceAsync(path, SourceTextEncoding.Auto);
            Assert.False(((Button)window.FindName("ConfirmButton")).IsEnabled);
            Assert.Contains("100", ((TextBlock)window.FindName("ExistingCountText")).Text);

            ((Button)window.FindName("SkipExistingButton")).RaiseEvent(
                new System.Windows.RoutedEventArgs(Button.ClickEvent));

            Assert.True(((Button)window.FindName("ConfirmButton")).IsEnabled);
            Assert.Equal(Enumerable.Range(101, 200).Select(number => (int?)number),
                window.CurrentPlan!.ImportableChapters.Select(chapter => chapter.Number));

            BulkImportReport report = await (Task<BulkImportReport>)fixture.Call(
                "ImportDetectedPlanAsync", window.CurrentPlan, null, CancellationToken.None)!;
            Assert.True(report.Completed);
            Assert.True(report.PostCommitVerified);
            Assert.True(report.PartialImport);
            Assert.False(report.VerifiedComplete);
            Assert.Equal(200, report.ImportedCount);

            ProjectSession reopened = (await new ProjectRepository().OpenAsync(fixture.Project.ManifestPath)).Session;
            Assert.Equal(300, reopened.Manifest.ChapterCount);
            Assert.Equal("Chương cũ 1", (await fixture.Repository.LoadChapterAsync(reopened,
                reopened.Manifest.Chapters.Single(chapter => chapter.Number == 1 && chapter.DeletedAt is null).ChapterId)).Chapter.Title);
            Assert.Empty(reopened.RecoveryReport.Orphans);
            Assert.False(reopened.RecoveryReport.RequiresDecision);
            Assert.Equal(source, window.CurrentPlan.ReconstructSource());
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Preview_auto_detects_legacy_encoding_without_replacement_characters() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        string path = Path.Combine(fixture.Root, "gbk.txt");
        await File.WriteAllBytesAsync(path, [0xb5, 0xda, 0x31, 0xd5, 0xc2, 0x0a, 0xd6, 0xd0, 0xce, 0xc4, 0xa1, 0xa3]);
        var window = new ImportPreviewWindow(fixture.Project);
        try
        {
            await window.LoadSourceAsync(path, SourceTextEncoding.Auto);
            Assert.DoesNotContain('\uFFFD', window.CurrentPlan!.Source.Text);
            Assert.True(window.CurrentPlan.Source.EncodingWasAutoDetected);
            Assert.Contains("GB", window.CurrentPlan.Source.EncodingName, StringComparison.OrdinalIgnoreCase);
            await window.LoadSourceAsync(path, SourceTextEncoding.Gbk);
            Assert.Equal("GBK (CP936)", window.CurrentPlan!.Source.EncodingName);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Preview_blocks_unreviewed_same_number_chapters() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        string path = Path.Combine(fixture.Root, "unordered.txt");
        await File.WriteAllTextAsync(path,
            "第3章 三\n正文三。\n第1章 一（前）\n正文一前。\n第2章 二\n正文二。\n第1章 一（后）\n正文一后。\n");
        var window = new ImportPreviewWindow(fixture.Project);
        try
        {
            await window.LoadSourceAsync(path, SourceTextEncoding.Auto);

            Assert.Equal([1, 1, 2, 3], window.CurrentPlan!.ImportableChapters.Select(chapter => chapter.Number));
            Assert.Equal(0, window.CurrentPlan.AutomaticallySkippedDuplicateCount);
            Assert.False(((Button)window.FindName("ConfirmButton")).IsEnabled);
            Assert.Contains("tự sắp xếp", ((TextBlock)window.FindName("PreviewStatusText")).Text,
                StringComparison.CurrentCultureIgnoreCase);

            var grid = (DataGrid)window.FindName("PreviewGrid");
            int?[] displayedNumbers = grid.Items.Cast<object>()
                .Select(row => row.GetType().GetProperty("NumberLabel")?.GetValue(row)?.ToString())
                .Where(value => int.TryParse(value, out _))
                .Select(value => (int?)int.Parse(value!)).ToArray();
            Assert.Equal([1, 1, 2, 3], displayedNumbers);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task BL18_Title_translation_fallback_keeps_confirm_enabled_and_drag_drop_loads_standalone_txt() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        string txtPath = Path.Combine(fixture.Root, "drop-chapter.txt");
        await File.WriteAllTextAsync(txtPath, "第1章 标题一\n正文一。\n第2章 标题二\n正文二。\n");

        // Standalone drag-and-drop loads .txt into ChapterInput
        bool handled = await fixture.Window.HandleDroppedFilesAsync([txtPath]);
        Assert.True(handled);
        Assert.Equal("drop-chapter", ((TextBox)fixture.Window.FindName("TitleInput")).Text);
        Assert.Contains("第1章", ((TextBox)fixture.Window.FindName("ChapterInput")).Text);

        await fixture.CreateProjectAsync();
        var window = new ImportPreviewWindow(fixture.Project);
        try
        {
            await window.LoadSourceAsync(txtPath, SourceTextEncoding.Auto);
            ((CheckBox)window.FindName("TranslateTitlesCheck")).IsChecked = true;
            Assert.True(((Button)window.FindName("ConfirmButton")).IsEnabled);
            window.EnsureFallbackVietnameseTitles();
            Assert.All(window.CurrentPlan!.ImportableChapters, c => Assert.False(string.IsNullOrWhiteSpace(c.VietnameseTitle)));
        }
        finally { window.Close(); }
    });
}
