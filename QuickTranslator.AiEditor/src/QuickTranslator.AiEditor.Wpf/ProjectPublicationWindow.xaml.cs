using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class ProjectPublicationWindow : Window
{
    private readonly ProjectSession _session;
    private readonly ProjectPublicationService _service;
    private readonly Func<Guid, Task>? _openChapter;
    private ProjectPublicationInspection? _inspection;
    private CancellationTokenSource? _operationCancellation;
    private bool _busy;

    internal string? LastExportPath { get; private set; }
    internal ProjectPublicationInspection? CurrentInspection => _inspection;

    internal ProjectPublicationWindow(ProjectSession session, ProjectPublicationService service,
        Func<Guid, Task>? openChapter = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _openChapter = openChapter;
        InitializeComponent();
        MainWindow.ClampWindowToWorkArea(this, 780, 690);
        ProjectTitleText.Text = session.Manifest.VietnameseTitle;
        UpdateFormatDescription();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_inspection is not null || _busy) return;
        await LoadInspectionAsync();
    }

    internal async Task LoadInspectionAsync()
    {
        ProjectPublicationRange? range;
        try { range = SelectedRange(); }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            _inspection = null;
            UpdateExportEnabled();
            ShowError(ex);
            return;
        }
        _inspection = null;
        SetBusy(true, "Đang kiểm tra các chương…");
        _operationCancellation = new();
        try
        {
            var progress = new Progress<ProjectPublicationProgress>(UpdateProgress);
            _inspection = await _service.InspectAsync(_session, progress, _operationCancellation.Token, range);
            ChapterCountText.Text = _inspection.TotalChapterCount.ToString("N0");
            InspectionText.Text = _inspection.RequiresConfirmation
                ? $"{_inspection.IncompleteChapterCount} chưa hoàn tất · {_inspection.InspectionChapterCount} cần kiểm tra"
                : "Sẵn sàng · mọi chương đều có bản dịch hợp lệ";
            InspectionText.Foreground = (System.Windows.Media.Brush)FindResource(
                _inspection.RequiresConfirmation ? "WarningBrush" : "SuccessBrush");
            AllowIncompleteInput.Visibility = _inspection.RequiresConfirmation ? Visibility.Visible : Visibility.Collapsed;
            IReadOnlyList<ProjectPublicationIssueChapter> issues = _inspection.IssueChapters ?? [];
            IssueChaptersList.ItemsSource = issues;
            IssueChaptersPanel.Visibility = issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ProgressText.Text = _inspection.TotalChapterCount == 0
                ? "Project chưa có chương đang hoạt động." : "Đã kiểm tra xong. Chọn định dạng và nơi lưu.";
        }
        catch (OperationCanceledException) { ProgressText.Text = "Đã hủy kiểm tra."; }
        catch (Exception ex) { ShowError(ex); }
        finally { _operationCancellation.Dispose(); _operationCancellation = null; SetBusy(false); }
    }

    private ProjectPublicationRange? SelectedRange()
    {
        static int? ReadNumber(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!int.TryParse(value.Trim(), out int number) || number < 1)
                throw new ArgumentException($"{label} phải là một số nguyên dương.");
            return number;
        }
        int? start = ReadNumber(StartChapterInput.Text, "Từ chương");
        int? end = ReadNumber(EndChapterInput.Text, "Đến chương");
        if (start is null && end is null) return null;
        var range = new ProjectPublicationRange(start, end);
        range.Validate();
        return range;
    }

    private void OnRangeChanged(object sender, TextChangedEventArgs e)
    {
        if (ExportButton is null || _busy) return;
        _inspection = null;
        ChapterCountText.Text = "—";
        InspectionText.Text = "Chưa kiểm tra phạm vi";
        IssueChaptersList.ItemsSource = null;
        IssueChaptersPanel.Visibility = Visibility.Collapsed;
        AllowIncompleteInput.IsChecked = false;
        AllowIncompleteInput.Visibility = Visibility.Collapsed;
        ProgressText.Text = "Phạm vi đã thay đổi. Bấm “Kiểm tra phạm vi” trước khi xuất.";
        UpdateExportEnabled();
    }

    private async void OnCheckRange(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await LoadInspectionAsync();
    }

    private async void OnIssueChapterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_busy || _openChapter is null || IssueChaptersList.SelectedItem is not ProjectPublicationIssueChapter issue)
            return;
        Guid chapterId = issue.ChapterId;
        Close();
        await _openChapter(chapterId);
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_inspection is null || _busy) return;
        PublicationFormat format = SelectedFormat();
        string extension = format == PublicationFormat.Pdf ? ".pdf" : ".txt";
        var dialog = new SaveFileDialog
        {
            Filter = format == PublicationFormat.Pdf ? "Tài liệu PDF (*.pdf)|*.pdf" : "Văn bản UTF-8 (*.txt)|*.txt",
            DefaultExt = extension,
            AddExtension = true,
            FileName = SanitizeFileName(_session.Manifest.VietnameseTitle) +
                (_inspection.Range is { } chosen && (chosen.StartChapterNumber.HasValue || chosen.EndChapterNumber.HasValue)
                    ? $"-chuong-{chosen.StartChapterNumber?.ToString() ?? "dau"}-{chosen.EndChapterNumber?.ToString() ?? "het"}" : "") +
                extension
        };
        if (dialog.ShowDialog(this) != true) return;

        var options = new ProjectPublicationOptions(
            IncludeProjectTitleInput.IsChecked == true,
            IncludeChapterTitlesInput.IsChecked == true,
            AllowIncomplete: AllowIncompleteInput.IsChecked == true);
        SetBusy(true, "Đang xuất bản…");
        _operationCancellation = new();
        try
        {
            var progress = new Progress<ProjectPublicationProgress>(UpdateProgress);
            ProjectPublicationReport report = format == PublicationFormat.Pdf
                ? await ProjectPdfExporter.ExportAsync(_service, _session, _inspection, dialog.FileName, options,
                    progress, _operationCancellation.Token)
                : await _service.ExportTextAsync(_session, _inspection, dialog.FileName, options, progress,
                    _operationCancellation.Token);
            LastExportPath = report.OutputPath;
            OutputPathText.Text = "Đã xuất " + report.ExportedChapterCount.ToString("N0") + " chương:\n" + report.OutputPath;
            ExportedActionsPanel.Visibility = Visibility.Visible;
            ProgressText.Text = report.IncompleteChapterCount > 0 || report.InspectionChapterCount > 0
                ? "Đã xuất bản nháp theo xác nhận của người dùng. Dữ liệu Project không bị thay đổi."
                : "Xuất bản hoàn tất. Dữ liệu Project không bị thay đổi.";
        }
        catch (OperationCanceledException) { ProgressText.Text = "Đã hủy. File đích cũ được giữ nguyên."; }
        catch (Exception ex) { ShowError(ex); }
        finally { _operationCancellation.Dispose(); _operationCancellation = null; SetBusy(false); }
    }

    private void OnOpenExportedFile(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LastExportPath) || !File.Exists(LastExportPath)) return;
        try { Process.Start(new ProcessStartInfo(LastExportPath) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OnOpenExportFolder(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LastExportPath)) return;
        string? dir = Path.GetDirectoryName(LastExportPath);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        FormatInput.IsEnabled = !busy;
        StartChapterInput.IsEnabled = !busy;
        EndChapterInput.IsEnabled = !busy;
        CheckRangeButton.IsEnabled = !busy;
        IncludeProjectTitleInput.IsEnabled = !busy;
        IncludeChapterTitlesInput.IsEnabled = !busy;
        AllowIncompleteInput.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
        CancelTaskButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (message is not null) ProgressText.Text = message;
        UpdateExportEnabled();
    }

    private void UpdateProgress(ProjectPublicationProgress progress)
    {
        ExportProgress.Maximum = Math.Max(1, progress.TotalChapterCount);
        ExportProgress.Value = progress.CompletedChapterCount;
        ProgressText.Text = $"{progress.CompletedChapterCount:N0}/{progress.TotalChapterCount:N0} · {progress.ChapterTitle}";
    }

    private void UpdateExportEnabled()
    {
        ExportButton.IsEnabled = !_busy && _inspection is { TotalChapterCount: > 0 } &&
            (!_inspection.RequiresConfirmation || AllowIncompleteInput.IsChecked == true);
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormatDescriptionText is not null) UpdateFormatDescription();
    }

    private void UpdateFormatDescription() => FormatDescriptionText.Text = SelectedFormat() == PublicationFormat.Pdf
        ? "PDF khổ A4, mỗi chương bắt đầu ở trang mới; nội dung tiếng Việt dùng cùng bộ ghép văn bản với Reader/TXT."
        : "TXT UTF-8 giữ nguyên nội dung tiếng Việt do Reader và trình xuất chương đang sử dụng.";

    private PublicationFormat SelectedFormat() =>
        string.Equals((FormatInput.SelectedItem as ComboBoxItem)?.Tag as string, "Pdf", StringComparison.Ordinal)
            ? PublicationFormat.Pdf : PublicationFormat.Txt;

    private void OnAllowIncompleteChanged(object sender, RoutedEventArgs e) => UpdateExportEnabled();
    private void OnCancelTask(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        _operationCancellation?.Cancel();
        ProgressText.Text = "Đang hủy tác vụ an toàn…";
    }

    private void ShowError(Exception ex)
    {
        ProgressText.Text = "Lỗi: " + ex.Message;
        ProgressText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        MessageBox.Show(this, ex.Message, "Xuất bản", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string SanitizeFileName(string value)
    {
        string safe = string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "QT-Studio-Export" : safe;
    }

    private enum PublicationFormat { Txt, Pdf }
}
