using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class ImportPreviewWindow : Window
{
    private readonly ProjectSession _project;
    private readonly ChapterTitleRuntimeFactory? _titleRuntimeFactory;
    private readonly ObservableCollection<PreviewRow> _rows = [];
    private ImportPreviewEditor? _editor;
    private ChapterTitleTranslationDraft? _titleDraft;
    private ChapterTitleTranslationDraftStore? _titleDraftStore;
    private CancellationTokenSource? _titleTranslationCancellation;
    private bool _loading;
    private bool _translatingTitles;

    public ImportPreviewWindow(ProjectSession project) : this(project, null, null) { }

    internal ImportPreviewWindow(ProjectSession project, ChapterTitleRuntimeFactory? titleRuntimeFactory, string? initialPath = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _titleRuntimeFactory = titleRuntimeFactory;
        InitializeComponent();
        MainWindow.ClampWindowToWorkArea(this, 1380, 860);
        PreviewGrid.ItemsSource = _rows;
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            Loaded += async (_, _) => await AnalyzePathAsync(initialPath);
        }
    }

    public ImportPreviewPlan? ConfirmedPlan { get; private set; }
    public bool ReplaceSoftDeletedHistory { get; private set; }
    public bool ImportAndTranslate { get; private set; }
    public string? SourcePath { get; private set; }
    internal ImportPreviewPlan? CurrentPlan => _editor?.Current;

    private void OnWindowPreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Any(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        string? txt = files.FirstOrDefault(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        if (txt is null) return;
        e.Handled = true;
        await AnalyzePathAsync(txt);
    }

    internal async Task LoadSourceAsync(string path, SourceTextEncoding encoding, CancellationToken token = default)
    {
        if (_loading) return;
        _loading = true;
        PreviewStatusText.Text = "Đang đọc và nhận diện…";
        ConfirmButton.IsEnabled = false;
        ImportAndTranslateButton.IsEnabled = false;
        ConfirmedPlan = null;
        ImportAndTranslate = false;
        ReplaceSoftDeletedHistory = false;
        ReplaceHistoryCheck.IsChecked = false;
        ReplaceHistoryCheck.Visibility = Visibility.Collapsed;
        _editor = null;
        _rows.Clear();
        try
        {
            RawSourceSnapshot source = await RawSourceReader.ReadFileAsync(path, encoding, token);
            ImmutableImportPlan detected = await Task.Run(() => new ChapterDetector().Detect(source), token);
            _editor = ImportPreviewEditor.Create(detected);
            SourcePath = Path.GetFullPath(path);
            SourcePathInput.Text = SourcePath;
            await RestoreTitleDraftAsync(token);
            RefreshRows();
            if (source.EncodingDetectionUncertain)
                PreviewStatusText.Text = $"Đã tự nhận diện {source.EncodingName}, nhưng nội dung có dấu hiệu bất thường. Hãy xem trước hoặc chọn bảng mã khác trong mục Bảng mã.";
            PreviewStatusText.Text = PreviewStatus(_editor.Current);
        }
        finally { _loading = false; }
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Văn bản tiểu thuyết (*.txt)|*.txt|Tất cả tệp|*.*", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        SourcePathInput.Text = dialog.FileName;
        await AnalyzePathAsync(dialog.FileName);
    }

    private async void OnAnalyze(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SourcePathInput.Text)) { ShowError("Hãy chọn một tệp TXT."); return; }
        await AnalyzePathAsync(SourcePathInput.Text);
    }

    private async Task AnalyzePathAsync(string path)
    {
        try { await LoadSourceAsync(path, SelectedEncoding()); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { ShowError(ex.Message); }
    }

    private SourceTextEncoding SelectedEncoding() => Enum.Parse<SourceTextEncoding>(
        ((EncodingInput.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Auto"), ignoreCase: true);

    private void RefreshRows(Guid? select = null)
    {
        if (_editor is null) return;
        ImportPreviewPlan plan = _editor.Current;
        int historyCollisions = plan.ImportableChapters.Count(IsSoftDeletedCollision);
        bool replaceHistory = historyCollisions > 0 && ReplaceHistoryCheck.IsChecked == true;
        ReplaceHistoryCheck.Visibility = historyCollisions > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReplaceHistoryCheck.IsEnabled = !_translatingTitles && historyCollisions > 0;
        Guid? selectedId = select ?? (PreviewGrid.SelectedItem as PreviewRow)?.Id;
        string query = SearchInput.Text.Trim();
        string filter = (FilterInput.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        _rows.Clear();
        var projectIssues = new List<string>();
        IEnumerable<ImportPreviewItem> displayItems = plan.ChaptersInImportOrder
            .Concat(plan.Items.Where(item => !item.IsChapter).OrderBy(item => item.Span.StartUtf16));
        HashSet<Guid> duplicateIds = plan.DuplicateEvidence.SelectMany(x => new[] { x.FirstItemId, x.SecondItemId }).ToHashSet();
        foreach (ImportPreviewItem item in displayItems)
        {
            string[] problems = plan.Issues.Where(x => x.ItemIds.Contains(item.Id)).Select(x => x.Message).ToArray();
            ChapterTitleTranslationItem? translated = _titleDraft?.Items.SingleOrDefault(x => x.Id == item.Id);
            if (translated?.Status == ChapterTitleTranslationStatus.Failed && translated.Error is not null)
                problems = [.. problems, "Dịch tiêu đề (đã dùng tên dự phòng): " + translated.Error];
            string? collision = ProjectCollision(item);
            if (collision is not null) { problems = [.. problems, collision]; projectIssues.Add(collision); }
            else if (replaceHistory && IsSoftDeletedCollision(item))
                problems = [.. problems, "Sẽ xóa vĩnh viễn bản History cũ và nhập lại ChapterId này."];
            var row = new PreviewRow(item, problems, translated?.Status);
            bool matchesQuery = query.Length == 0 || row.NumberLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || row.VolumeTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            bool matchesFilter = filter switch
            {
                "Problems" => problems.Length > 0,
                "Duplicates" => duplicateIds.Contains(item.Id),
                "Uncertain" => item.Decision == CandidateDecision.NeedsReview,
                "MissingNumber" => item.IsChapter && item.Number is not > 0,
                "Included" => item.Included,
                "Unclassified" => item.Kind == PlannedRegionKind.Unclassified,
                _ => true
            };
            if (matchesQuery && matchesFilter) _rows.Add(row);
        }
        PreviewGrid.SelectedItem = _rows.FirstOrDefault(x => x.Id == selectedId) ?? _rows.FirstOrDefault();
        IssuesList.ItemsSource = plan.Issues.Select(x => SeverityLabel(x.Severity) + " " + x.Message)
            .Concat(projectIssues.Distinct().Select(x => "● " + x)).ToArray();
        int collisions = plan.ImportableChapters.Count(x => ProjectCollision(x) is not null);
        int exactExisting = plan.Chapters.Count(IsActiveExistingChapter);
        int numberConflicts = plan.Chapters.Count(IsActiveNumberConflict);
        int newChapters = plan.Chapters.Count(item => item.Included && !IsActiveExistingChapter(item) &&
            !IsActiveNumberConflict(item));
        int problemsCount = plan.Issues.Count(x => x.Severity != DetectionIssueSeverity.Information) + collisions +
            (plan.Source.EncodingDetectionUncertain ? 1 : 0);
        NewCountText.Text = $"{newChapters:N0} chương";
        ExistingCountText.Text = $"{exactExisting + numberConflicts:N0} chương";
        ImportCountText.Text = $"{newChapters:N0} chương";
        ProblemCountText.Text = $"{problemsCount:N0} mục";
        SourceSummaryText.Text = $"{plan.Source.EncodingName} · {plan.Source.Text.Length:N0} ký tự";
        SourceCoverageSummary coverage = plan.CoverageAudit.Summary;
        int trusted = plan.Chapters.Count(x => x.Decision == CandidateDecision.Accepted);
        int review = plan.Chapters.Count(x => x.Decision == CandidateDecision.NeedsReview);
        int duplicates = duplicateIds.Count;
        int missing = plan.Chapters.Count(x => x.Number is not > 0);
        int unknown = plan.Items.Count(x => x.Kind == PlannedRegionKind.Unclassified);
        CoverageSummaryText.Text = $"Tin cậy {trusted:N0} · cần xem {review:N0} · trùng {duplicates:N0} · thiếu số {missing:N0} · vùng chưa phân loại {unknown:N0}\n" +
            $"Ledger: nguồn {coverage.TotalUtf16:N0} · nội dung nhập {coverage.ImportedContentUtf16:N0} · heading {coverage.ImportedHeadingUtf16:N0} · metadata {coverage.MetadataUtf16:N0} · trùng xác minh {coverage.VerifiedDuplicateUtf16:N0} · loại rõ ràng {coverage.ExplicitlyExcludedUtf16:N0} · chưa giải quyết {coverage.UnresolvedUtf16:N0}.";
        UndoButton.IsEnabled = _editor.CanUndo;
        int failedTitles = _titleDraft?.Items.Count(item => item.Status == ChapterTitleTranslationStatus.Failed) ?? 0;
        int untranslatedCount = plan.ImportableChapters.Count(item => string.IsNullOrWhiteSpace(item.VietnameseTitle));
        // BL-18: Do not lock out ConfirmButton when a few titles fail or remain untranslated; automatically fallback to safe titles with warning.
        bool canImport = !_translatingTitles && !plan.HasBlockingIssues && collisions == 0 &&
            plan.ImportableChapters.Any();
        ConfirmButton.IsEnabled = canImport;
        ImportAndTranslateButton.IsEnabled = canImport && newChapters > 0;
        SkipExistingButton.IsEnabled = !_translatingTitles && exactExisting + numberConflicts > 0;
        FooterHintText.Text = ConfirmButton.IsEnabled
            ? (replaceHistory
                ? $"Sẵn sàng nhập {newChapters:N0} chương mới; sẽ xóa vĩnh viễn {historyCollisions:N0} bản trong History sau khi bạn xác nhận."
                : TranslateTitlesCheck.IsChecked == true && (failedTitles > 0 || untranslatedCount > 0)
                ? $"Sẵn sàng nhập {newChapters:N0} chương mới. {failedTitles:N0} tiêu đề lỗi và {untranslatedCount:N0} tiêu đề chưa dịch sẽ dùng tên dự phòng."
                : $"Sẵn sàng nhập {newChapters:N0} chương mới theo thứ tự đọc.")
            : "Cần xử lý các mục cảnh báo hoặc bỏ qua chương đã có trước khi nhập. File TXT không bị thay đổi.";
        int translatedCount = plan.ImportableChapters.Count(item => !string.IsNullOrWhiteSpace(item.VietnameseTitle));
        TitleTranslationProgressText.Text = $"{translatedCount:N0}/{plan.ImportableChapters.Count():N0} tiêu đề · {failedTitles:N0} lỗi";
        TranslateAllTitlesButton.IsEnabled = !_translatingTitles && plan.ImportableChapters.Any();
        RetryFailedTitlesButton.IsEnabled = !_translatingTitles && failedTitles > 0;
        RetrySelectedTitlesButton.IsEnabled = !_translatingTitles && PreviewGrid.SelectedItems.Count > 0;
        CancelTitleTranslationButton.IsEnabled = _translatingTitles;
        UpdateSelection();
    }

    private void OnFilterChanged(object sender, EventArgs e)
    {
        if (IsLoaded && _editor is not null) RefreshRows();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (_editor is null || PreviewGrid.SelectedItem is not PreviewRow row) return;
        ImportPreviewItem item = _editor.Current.Items.Single(x => x.Id == row.Id);
        NumberInput.Text = item.Number?.ToString() ?? "";
        TitleInput.Text = item.Title;
        NumberInput.IsEnabled = TitleInput.IsEnabled = IncludeButton.IsEnabled = item.IsChapter;
        ConfirmBoundaryButton.IsEnabled = item.IsChapter && item.Decision != CandidateDecision.Accepted;
        RejectBoundaryButton.IsEnabled = item.IsChapter && _editor.Current.Items.IndexOf(item) > 0;
        IncludeButton.Content = item.Included ? "Loại khỏi import" : "Đưa vào import";
        StartContextText.Text = Context(item.Span.StartUtf16, "⟦ BẮT ĐẦU ⟧");
        EndContextText.Text = Context(item.Span.EndUtf16, "⟦ KẾT THÚC ⟧");
        string[] duplicateDetails = _editor.Current.DuplicateEvidence.Where(x => x.FirstItemId == item.Id || x.SecondItemId == item.Id)
            .Select(x => $"{x.Kind}: {x.Reason}").ToArray();
        SelectedDetailText.Text = item.Kind == PlannedRegionKind.Unclassified
            ? "Vùng này được giữ trong bản nguồn nhưng không nhập thành chương."
            : string.Join("\n", row.Problems.Concat(duplicateDetails).DefaultIfEmpty("Ranh giới chương đã sẵn sàng."));
    }

    private string Context(int boundary, string marker)
    {
        string source = _editor!.Current.Source.Text;
        int before = Math.Max(0, boundary - 260), after = Math.Min(source.Length, boundary + 260);
        return source[before..boundary] + marker + source[boundary..after];
    }

    private void OnApplyEdit(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedChapter(out PreviewRow? row)) return;
        try
        {
            int? number = string.IsNullOrWhiteSpace(NumberInput.Text) ? null : int.Parse(NumberInput.Text);
            _editor!.UpdateChapter(row.Id, number, TitleInput.Text);
            InvalidateTitleDraft();
            RefreshRows(row.Id);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException) { ShowError(ex.Message); }
    }

    private void OnToggleInclude(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedChapter(out PreviewRow? row)) return;
        ImportPreviewItem item = _editor!.Current.Items.Single(x => x.Id == row.Id);
        try { _editor.SetIncluded(row.Id, !item.Included); RefreshRows(row.Id); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OnConfirmBoundary(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedChapter(out PreviewRow? row)) return;
        try { _editor!.ConfirmBoundary(row.Id); RefreshRows(row.Id); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OnRejectFalseBoundary(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedChapter(out PreviewRow? row)) return;
        try { _editor!.RejectFalseBoundary(row.Id); InvalidateTitleDraft(); RefreshRows(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OnSplit(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedChapter(out PreviewRow? row)) return;
        ImportPreviewItem item = _editor!.Current.Items.Single(x => x.Id == row.Id);
        LineChoice[] choices = _editor.Current.Lines.Where(x => x.FullSpan.StartUtf16 > item.ContentSpan.StartUtf16 && x.FullSpan.StartUtf16 < item.Span.EndUtf16)
            .Select(x => new LineChoice(x.FullSpan.StartUtf16, $"Dòng {x.Number}: {Short(x.Content)}")).ToArray();
        if (!TryChooseBoundary("Tách chương", choices, (item.Number ?? 0) + 1, "Chương tách mới", out int offset, out int number, out string title)) return;
        try { _editor.Split(row.Id, offset, number, title); InvalidateTitleDraft(); RefreshRows(row.Id); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OnMerge(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedChapter(out PreviewRow? row)) return;
        try { _editor!.MergeWithNext(row.Id); InvalidateTitleDraft(); RefreshRows(row.Id); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OnMoveBoundary(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedChapter(out PreviewRow? row)) return;
        ImportPreviewItem item = _editor!.Current.Items.Single(x => x.Id == row.Id);
        int index = _editor.Current.Items.IndexOf(item);
        if (index + 1 >= _editor.Current.Items.Length || !_editor.Current.Items[index + 1].IsChapter)
        { ShowError("Không có chương liền kề phía sau."); return; }
        ImportPreviewItem next = _editor.Current.Items[index + 1];
        LineChoice[] choices = _editor.Current.Lines.Where(x => x.FullSpan.StartUtf16 > item.ContentSpan.StartUtf16 && x.FullSpan.StartUtf16 < next.Span.EndUtf16)
            .Select(x => new LineChoice(x.FullSpan.StartUtf16, $"Dòng {x.Number}: {Short(x.Content)}")).ToArray();
        if (!TryChooseBoundary("Dời ranh giới", choices, next.Number ?? 1, next.Title, out int offset, out _, out _)) return;
        try { _editor.MoveBoundary(row.Id, offset); InvalidateTitleDraft(); RefreshRows(row.Id); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OnUndo(object sender, RoutedEventArgs e) { _editor?.Undo(); InvalidateTitleDraft(); RefreshRows(); }

    private void OnTitleTranslationOptionChanged(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && _editor is not null) RefreshRows();
    }

    private async void OnTranslateAllTitles(object sender, RoutedEventArgs e) =>
        await TranslateTitlesAsync(retryFailedOnly: false, selectedIds: null);

    private async void OnRetryFailedTitles(object sender, RoutedEventArgs e) =>
        await TranslateTitlesAsync(retryFailedOnly: true, selectedIds: null);

    private async void OnRetrySelectedTitles(object sender, RoutedEventArgs e)
    {
        Guid[] selected = PreviewGrid.SelectedItems.Cast<PreviewRow>().Select(row => row.Id).ToArray();
        if (_titleDraft is not null)
            foreach (ChapterTitleTranslationItem item in _titleDraft.Items.Where(item => selected.Contains(item.Id) &&
                         !item.ExistingTitleProtected))
                item.Status = ChapterTitleTranslationStatus.Pending;
        await TranslateTitlesAsync(retryFailedOnly: false, selectedIds: selected);
    }

    private void OnCancelTitleTranslation(object sender, RoutedEventArgs e) => _titleTranslationCancellation?.Cancel();

    private async Task TranslateTitlesAsync(bool retryFailedOnly, IReadOnlyCollection<Guid>? selectedIds)
    {
        if (_editor is null || _translatingTitles) return;
        TranslateTitlesCheck.IsChecked = true;
        ChapterTitleTranslationMethod method = SelectedTitleMethod();
        try
        {
            ChapterTitleRuntime runtime = await CreateTitleRuntimeAsync(method, CancellationToken.None);
            EnsureTitleDraft(method, runtime);
            _titleTranslationCancellation = new();
            _translatingTitles = true;
            RefreshRows();
            if (_titleDraftStore is not null) await _titleDraftStore.SaveAsync(_titleDraft!);
            var service = new ChapterTitleTranslationService();
            var progress = new Progress<ChapterTitleTranslationProgress>(value =>
                TitleTranslationProgressText.Text = $"{value.Completed:N0}/{value.Total:N0} tiêu đề · {value.Failed:N0} lỗi");
            await service.TranslateAsync(_titleDraft!, runtime.Backend, runtime.ModelOptions, retryFailedOnly,
                selectedIds, overwriteProtected: false, progress,
                _titleDraftStore is null ? null : _titleDraftStore.SaveAsync,
                _titleTranslationCancellation.Token);
            ApplyCompletedTitles();
        }
        catch (OperationCanceledException)
        {
            PreviewStatusText.Text = "Đã hủy dịch tiêu đề; các batch hoàn tất vẫn nằm trong draft.";
            ApplyCompletedTitles();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            _titleTranslationCancellation?.Dispose(); _titleTranslationCancellation = null;
            _translatingTitles = false; RefreshRows();
        }
    }

    private void EnsureTitleDraft(ChapterTitleTranslationMethod method, ChapterTitleRuntime runtime)
    {
        ImportPreviewItem[] chapters = _editor!.Current.ImportableChapters.ToArray();
        Guid[] ids = chapters.Select(item => item.Id).ToArray();
        bool compatible = _titleDraft is not null && _titleDraft.Method == method &&
            _titleDraft.Items.Select(item => item.Id).SequenceEqual(ids);
        if (compatible) return;
        _titleDraft = ChapterTitleTranslationDraft.Create(_project.Manifest.ProjectId,
            "import:" + _editor.Current.Source.RawSha256, method,
            chapters.Select(item => new ChapterTitleTranslationSource(item.Id, item.Number, item.Title,
                item.VietnameseTitle ?? "")), runtime.BackendProfileId, runtime.Model);
    }

    private async Task<ChapterTitleRuntime> CreateTitleRuntimeAsync(ChapterTitleTranslationMethod method,
        CancellationToken cancellationToken)
    {
        if (_titleRuntimeFactory is not null) return await _titleRuntimeFactory(method, cancellationToken);
        if (method == ChapterTitleTranslationMethod.KeepChinese)
            return new(new KeepChineseChapterTitleTranslationBackend(), new ModelOptions(), null, "none");
        throw new InvalidOperationException("Phiên thử UI chưa có backend dịch tiêu đề. Hãy mở từ cửa sổ chính của QT Studio.");
    }

    private ChapterTitleTranslationMethod SelectedTitleMethod() => Enum.Parse<ChapterTitleTranslationMethod>(
        (TitleMethodInput.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Ollama", true);

    private async Task RestoreTitleDraftAsync(CancellationToken cancellationToken)
    {
        _titleDraft = null;
        string sourceKey = "import:" + _editor!.Current.Source.RawSha256;
        _titleDraftStore = new ChapterTitleTranslationDraftStore(_project.Resolve(
            $"Cache/title-translation-import-{_editor.Current.Source.RawSha256[..24]}.json"));
        try
        {
            ChapterTitleTranslationDraft? loaded = await _titleDraftStore.LoadAsync(cancellationToken);
            if (loaded is null || loaded.ProjectId != _project.Manifest.ProjectId || loaded.SourceKey != sourceKey) return;
            HashSet<Guid> current = _editor.Current.Chapters.Select(item => item.Id).ToHashSet();
            if (loaded.Items.Any(item => !current.Contains(item.Id))) return;
            _titleDraft = loaded;
            TitleMethodInput.SelectedIndex = (int)loaded.Method;
            TranslateTitlesCheck.IsChecked = true;
            ApplyCompletedTitles();
        }
        catch (InvalidDataException ex) { PreviewStatusText.Text = "Draft tiêu đề cần kiểm tra: " + ex.Message; }
    }

    private void ApplyCompletedTitles()
    {
        if (_editor is null || _titleDraft is null) return;
        var titles = new Dictionary<Guid, string>();
        foreach (ChapterTitleTranslationItem item in _titleDraft.Items)
        {
            if (item.Status is ChapterTitleTranslationStatus.Completed or ChapterTitleTranslationStatus.Protected &&
                !string.IsNullOrWhiteSpace(item.VietnameseTitle))
            {
                titles[item.Id] = item.VietnameseTitle;
            }
            else if (item.Status == ChapterTitleTranslationStatus.Failed)
            {
                titles[item.Id] = !string.IsNullOrWhiteSpace(item.VietnameseTitle)
                    ? item.VietnameseTitle
                    : FallbackVietnameseTitle(item.ChapterNumber, item.ChineseTitle);
            }
        }
        if (titles.Count > 0) _editor.ApplyVietnameseTitles(titles);
    }

    internal void EnsureFallbackVietnameseTitles()
    {
        if (_editor is null) return;
        var fallback = new Dictionary<Guid, string>();
        foreach (ImportPreviewItem item in _editor.Current.ImportableChapters)
        {
            if (string.IsNullOrWhiteSpace(item.VietnameseTitle))
                fallback[item.Id] = FallbackVietnameseTitle(item.Number, item.Title);
        }
        if (fallback.Count > 0) _editor.ApplyVietnameseTitles(fallback);
    }

    private static string FallbackVietnameseTitle(int? number, string originalTitle)
    {
        string trimmed = originalTitle.Trim();
        if (number is int n && !trimmed.StartsWith("Chương ", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(trimmed) ? $"Chương {n}" : $"Chương {n}: {trimmed}";
        return string.IsNullOrWhiteSpace(trimmed) ? "Chương mới" : trimmed;
    }

    private void InvalidateTitleDraft()
    {
        _titleTranslationCancellation?.Cancel();
        _titleDraft = null;
    }

    private void OnPreviewCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (_editor is null || e.EditAction != DataGridEditAction.Commit ||
            e.Row.Item is not PreviewRow row || e.EditingElement is not TextBox input ||
            e.Column.Header?.ToString() != "Tiêu đề Việt") return;
        try
        {
            _editor.UpdateVietnameseTitle(row.Id, input.Text);
            if (_titleDraft?.Items.SingleOrDefault(item => item.Id == row.Id) is ChapterTitleTranslationItem item)
            {
                item.VietnameseTitle = input.Text.Trim();
                item.TranslatedName = input.Text.Trim();
                item.Status = string.IsNullOrWhiteSpace(input.Text)
                    ? ChapterTitleTranslationStatus.Pending : ChapterTitleTranslationStatus.Completed;
                item.Error = null;
            }
            Dispatcher.BeginInvoke(async () => await SaveManualTitleEditAsync(row.Id));
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task SaveManualTitleEditAsync(Guid selectedId)
    {
        try
        {
            if (_titleDraftStore is not null && _titleDraft is not null)
                await _titleDraftStore.SaveAsync(_titleDraft);
            RefreshRows(selectedId);
        }
        catch (Exception ex) { ShowError("Không lưu được draft tiêu đề: " + ex.Message); }
    }

    private bool TryChooseBoundary(string title, LineChoice[] choices, int suggestedNumber, string suggestedTitle,
        out int offset, out int number, out string chapterTitle)
    {
        offset = 0; number = suggestedNumber; chapterTitle = suggestedTitle;
        if (choices.Length == 0) { ShowError("Không có đầu dòng hợp lệ trong vùng đã chọn."); return false; }
        var lines = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(LineChoice.Label), SelectedIndex = 0, Margin = new Thickness(8) };
        var numberInput = new TextBox { Text = suggestedNumber.ToString(), Margin = new Thickness(8) };
        var titleInput = new TextBox { Text = suggestedTitle, Margin = new Thickness(8) };
        var ok = new Button { Content = "Áp dụng", IsDefault = true }; var cancel = new Button { Content = "Hủy", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(12) };
        var l1 = new TextBlock { Text = "Chọn dòng bắt đầu" }; l1.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var l2 = new TextBlock { Text = "Số chương" }; l2.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var l3 = new TextBlock { Text = "Tên chương" }; l3.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        panel.Children.Add(l1); panel.Children.Add(lines);
        panel.Children.Add(l2); panel.Children.Add(numberInput);
        panel.Children.Add(l3); panel.Children.Add(titleInput); panel.Children.Add(buttons);
        var dialog = new Window { Owner = this, Title = title, Width = 620, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        dialog.SetResourceReference(BackgroundProperty, "PanelBrush");
        dialog.SetResourceReference(ForegroundProperty, "TextBrush");
        MainWindow.ClampWindowToWorkArea(dialog, 620, 460);
        ok.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true || lines.SelectedItem is not LineChoice selected || !int.TryParse(numberInput.Text, out number) || number <= 0) return false;
        offset = selected.Offset; chapterTitle = titleInput.Text.Trim(); return true;
    }

    private bool TrySelectedChapter([NotNullWhen(true)] out PreviewRow? row)
    {
        row = PreviewGrid.SelectedItem as PreviewRow;
        if (row is null || _editor is null)
        { ShowError("Hãy chọn một chương."); return false; }
        Guid selectedId = row.Id;
        if (!_editor.Current.Items.Single(x => x.Id == selectedId).IsChapter)
        { ShowError("Hãy chọn một chương."); return false; }
        return true;
    }

    private string? ProjectCollision(ImportPreviewItem item)
    {
        if (!item.Included || item.Number is not int number) return null;
        ProjectChapterEntry? id = _project.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == item.ChapterId);
        if (id is not null)
        {
            if (id.DeletedAt is null) return null;
            if (ReplaceHistoryCheck.IsChecked != true)
                return "ChapterId nằm trong History/xóa mềm. Bật “Ghi đè History” để thay thế bản cũ.";
        }
        ProjectChapterEntry? used = _project.Manifest.Chapters.SingleOrDefault(x => x.DeletedAt is null && x.Number == number);
        return used is null ? null : $"Số {number} đã thuộc “{used.VietnameseTitle}”.";
    }

    private bool IsSoftDeletedCollision(ImportPreviewItem item) => item.Included &&
        _project.Manifest.Chapters.Any(x => x.ChapterId == item.ChapterId && x.DeletedAt is not null);

    private bool IsActiveExistingChapter(ImportPreviewItem item) => item.IsChapter &&
        _project.Manifest.Chapters.Any(entry => entry.DeletedAt is null && entry.ChapterId == item.ChapterId);

    private bool IsActiveNumberConflict(ImportPreviewItem item) => item.IsChapter && item.Number is int number &&
        _project.Manifest.Chapters.Any(entry => entry.DeletedAt is null && entry.Number == number &&
            entry.ChapterId != item.ChapterId);

    private void OnSkipExisting(object sender, RoutedEventArgs e)
    {
        if (_editor is null) return;
        Guid[] existing = _editor.Current.Chapters
            .Where(item => item.Included && (IsActiveExistingChapter(item) || IsActiveNumberConflict(item)))
            .Select(item => item.Id).ToArray();
        foreach (Guid id in existing) _editor.SetIncluded(id, false);
        RefreshRows();
        PreviewStatusText.Text = existing.Length == 0
            ? "Không có chương đã tồn tại cần bỏ qua."
            : $"Đã bỏ qua {existing.Length:N0} chương đã có; chỉ các chương mới sẽ được nhập.";
    }

    private void OnReplaceHistoryChanged(object sender, RoutedEventArgs e)
    {
        if (_editor is not null) RefreshRows();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => Confirm(importAndTranslate: false);
    private void OnConfirmAndTranslate(object sender, RoutedEventArgs e) => Confirm(importAndTranslate: true);

    private void Confirm(bool importAndTranslate)
    {
        if (_editor is null || !ConfirmButton.IsEnabled) return;
        int historyCollisions = _editor.Current.ImportableChapters.Count(IsSoftDeletedCollision);
        bool replaceHistory = historyCollisions > 0 && ReplaceHistoryCheck.IsChecked == true;
        if (replaceHistory && MessageBox.Show(this,
                $"Xóa vĩnh viễn {historyCollisions:N0} chương đang nằm trong History và nhập lại từ TXT?\n\n" +
                "Bản dịch, bản nháp, duyệt và lịch sử của các bản cũ sẽ bị xóa khi transaction commit. " +
                "Nếu import thất bại, transaction sẽ phục hồi chúng.",
                "Xác nhận ghi đè History", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        if (TranslateTitlesCheck.IsChecked == true)
            EnsureFallbackVietnameseTitles();
        ReplaceSoftDeletedHistory = replaceHistory;
        ImportAndTranslate = importAndTranslate;
        ConfirmedPlan = _editor.Current;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void ShowError(string message) { PreviewStatusText.Text = "Lỗi: " + message; MessageBox.Show(this, message, "Import Preview", MessageBoxButton.OK, MessageBoxImage.Error); }
    private static string SeverityLabel(DetectionIssueSeverity severity) => severity switch { DetectionIssueSeverity.Error => "●", DetectionIssueSeverity.Warning => "▲", _ => "ℹ" };
    private static string PreviewStatus(ImportPreviewPlan plan)
    {
        var details = new List<string>();
        if (plan.WasAutomaticallyReordered) details.Add("đã tự sắp xếp theo số chương");
        if (plan.AutomaticallySkippedDuplicateCount > 0)
            details.Add($"đã bỏ qua {plan.AutomaticallySkippedDuplicateCount:N0} bản trùng phía sau");
        return details.Count == 0
            ? "Đã nhận diện. Kiểm tra các cảnh báo trước khi nhập."
            : "Đã nhận diện và " + string.Join("; ", details) + ". Nguyên văn nguồn được giữ nguyên.";
    }
    private static string Short(string value) => value.Length <= 70 ? value : value[..67] + "…";

    private sealed record LineChoice(int Offset, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record PreviewRow(Guid Id, string IncludedLabel, string NumberLabel, string Title,
        string VietnameseTitle, string VolumeTitle, string Status, string ProblemLabel, string[] Problems)
    {
        public PreviewRow(ImportPreviewItem item, string[] problems, ChapterTitleTranslationStatus? titleStatus) : this(
            item.Id, item.Included ? "✓" : "—",
            item.Number?.ToString() ?? "—", item.Kind == PlannedRegionKind.Unclassified ? "Vùng chưa phân loại" : item.Title,
            item.VietnameseTitle ?? "", item.VolumeTitle ?? "", item.Kind switch
            {
                PlannedRegionKind.Volume => "Quyển", PlannedRegionKind.Unclassified => "Chưa phân loại",
                _ when item.AutomaticallyExcludedDuplicate => "Bỏ qua bản trùng",
                _ when titleStatus == ChapterTitleTranslationStatus.Failed => "Lỗi dịch tên",
                _ when titleStatus == ChapterTitleTranslationStatus.Completed => "Đã dịch tên",
                _ when item.Decision == CandidateDecision.NeedsReview => "Cần xem", _ => "Tin cậy"
            }, problems.Length == 0 ? "" : string.Join(" · ", problems), problems) { }
    }
}
