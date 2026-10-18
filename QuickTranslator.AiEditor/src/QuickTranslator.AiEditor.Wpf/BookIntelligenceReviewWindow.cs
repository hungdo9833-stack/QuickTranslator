using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public sealed class BookIntelligenceReviewWindow : Window
{
    internal static Guid GeminiFlashPoolProfileId { get; } = Guid.Parse("c528d3a8-147a-4b7d-8471-e4cbe39d6f38");
    private readonly ProjectSession _session;
    private readonly ProjectRepository _repository;
    private ProjectResources _resources;
    private readonly IReadOnlyList<OllamaBackendProfile> _profiles;
    private readonly Func<IntelligenceCharacterSuggestion, CharacterProfile?> _editCharacter;
    private readonly Action<ProjectResources> _resourcesChanged;
    private readonly Func<CorpusEvidence, Task> _openEvidence;
    private readonly Action<BookIntelligenceState, bool> _stateChanged;
    private BookIntelligenceState? _state;
    private CancellationTokenSource? _cancellation;

    private readonly ComboBox _profile = new() { Width = 210, DisplayMemberPath = nameof(OllamaBackendProfile.Name) };
    private readonly ComboBox _model = new() { Width = 240, IsEditable = false };
    private readonly Button _run = new() { Content = "Bắt đầu / Tiếp tục" };
    private readonly Button _rerun = new() { Content = "Khảo sát lại", ToolTip = "Lưu bản sao kết quả cũ và khảo sát lại từ đầu, giữ dữ liệu đã xác nhận." };
    private readonly Button _pairs = new() { Content = "Hồ sơ theo cặp", IsEnabled = false,
        ToolTip = "Xem chung tất cả bằng chứng và giả thuyết của mỗi cặp nhân vật; không tự duyệt." };
    private readonly Button _blocked = new() { Content = "Đoạn bị chặn", IsEnabled = false,
        ToolTip = "Xem các đoạn Google đã từ chối; không tự gửi lại hoặc duyệt nội dung." };
    private readonly Button _cancel = new() { Content = "Tạm dừng", IsEnabled = false };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 5, Minimum = 0, Maximum = 1 };
    private readonly DataGrid _queue = new() { AutoGenerateColumns = false, IsReadOnly = true,
        CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Extended };
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 150 };
    private readonly ListBox _evidence = new() { DisplayMemberPath = nameof(EvidenceRow.Label), MinHeight = 100 };
    private readonly ComboBox _mergeTarget = new() { Width = 220, DisplayMemberPath = nameof(CharacterProfile.VietnameseName),
        SelectedValuePath = nameof(CharacterProfile.Id) };
    private readonly Button _save = new() { Content = "Chỉnh sửa rồi lưu" };
    private readonly Button _merge = new() { Content = "Gộp vào nhân vật đã có" };
    private readonly Button _later = new() { Content = "Xem xét sau" };
    private readonly Button _reject = new() { Content = "Không phải / Bỏ đề xuất" };
    private readonly Button _open = new() { Content = "Mở đoạn nguyên tác" };
    private readonly Button _viewToggle = new()
    { Content = "Xem đề xuất thô", ToolTip = "Chuyển qua lại giữa hồ sơ nhân vật và dữ liệu kiểm toán chi tiết." };
    private bool _dossierMode = true;
    private readonly ComboBox _scanMode = new() { Width = 170, ItemsSource = Enum.GetValues<IntelligenceScanMode>() };
    private readonly CheckBox _translationContext = new() { Content = "Dùng Knowledge Context khi dịch (thử nghiệm)", Margin = new Thickness(10, 4, 0, 4) };
    private readonly TextBox _search = new() { Width = 190, ToolTip = "Tìm tên Trung, Việt hoặc bí danh" };
    private readonly ComboBox _kindFilter = new() { Width = 145, ItemsSource = new[] { "Tất cả loại", "Nhân vật", "Quan hệ", "Suy luận", "Glossary" }, SelectedIndex = 0 };
    private readonly ComboBox _statusFilter = new() { Width = 145, ItemsSource = new[] { "Tất cả trạng thái", "Chờ duyệt", "Đã xử lý", "Xung đột/Stale" }, SelectedIndex = 0 };
    private bool _loading;

    public BookIntelligenceReviewWindow(ProjectSession session, ProjectRepository repository,
        ProjectResources resources, IReadOnlyList<OllamaBackendProfile> profiles,
        Func<IntelligenceCharacterSuggestion, CharacterProfile?> editCharacter,
        Action<ProjectResources> resourcesChanged, Func<CorpusEvidence, Task> openEvidence,
        Action<BookIntelligenceState, bool> stateChanged)
    {
        _session = session;
        _repository = repository;
        _resources = resources.Clone();
        _profiles =
        [
            CreateGeminiFlashPoolProfile(),
            .. profiles.Where(profile => profile.Id != GeminiFlashPoolProfileId)
        ];
        _editCharacter = editCharacter;
        _resourcesChanged = resourcesChanged;
        _openEvidence = openEvidence;
        _stateChanged = stateChanged;
        Title = "QT Studio · Khảo sát nhân vật & Glossary bằng AI";
        Width = 1220; Height = 780; MinWidth = 900; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildContent();
        Loaded += OnLoaded;
    }

    private UIElement BuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(14) };
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = "Khảo sát nguyên tác bằng AI", FontSize = 25, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "CPU đọc toàn bộ OriginalText; AI chỉ nhận các đoạn có tín hiệu. Mọi đề xuất phải được bạn xác nhận trước khi lưu.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8)
        });
        var controls = new WrapPanel();
        controls.Children.Add(new TextBlock { Text = "Backend khảo sát", VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_profile);
        controls.Children.Add(new TextBlock { Text = "Model độc lập", VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_model); controls.Children.Add(new TextBlock { Text = "Chế độ", VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_scanMode); controls.Children.Add(_run); controls.Children.Add(_rerun); controls.Children.Add(_cancel); controls.Children.Add(_blocked); controls.Children.Add(_pairs);
        controls.Children.Add(_translationContext);
        controls.Children.Add(_viewToggle);
        var filters = new WrapPanel { Margin = new Thickness(0, 5, 0, 3) };
        filters.Children.Add(new TextBlock { Text = "Lọc", VerticalAlignment = VerticalAlignment.Center });
        filters.Children.Add(_kindFilter); filters.Children.Add(_statusFilter); filters.Children.Add(_search);
        header.Children.Add(filters);
        header.Children.Add(controls); header.Children.Add(_progress); header.Children.Add(_summary);
        root.Children.Add(header);

        var footer = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(_save); footer.Children.Add(_mergeTarget); footer.Children.Add(_merge);
        footer.Children.Add(_later); footer.Children.Add(_reject); footer.Children.Add(_open);
        root.Children.Add(footer);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        _queue.Columns.Add(new DataGridTextColumn { Header = "Loại", Binding = new Binding(nameof(ReviewRow.Kind)), Width = 95 });
        _queue.Columns.Add(new DataGridTextColumn { Header = "Đề xuất", Binding = new Binding(nameof(ReviewRow.Title)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _queue.Columns.Add(new DataGridTextColumn { Header = "Mức tin cậy", Binding = new Binding(nameof(ReviewRow.ConfidenceDisplay)), Width = 105 });
        _queue.Columns.Add(new DataGridTextColumn { Header = "Trạng thái", Binding = new Binding(nameof(ReviewRow.StatusDisplay)), Width = 110 });
        _queue.Columns.Add(new DataGridTextColumn { Header = "Nguồn", Binding = new Binding(nameof(ReviewRow.Origin)), Width = 85 });
        _queue.Columns.Add(new DataGridTextColumn { Header = "Chứng cứ", Binding = new Binding(nameof(ReviewRow.EvidenceCount)), Width = 75 });
        _queue.SelectionChanged += (_, _) => ShowSelection();
        grid.Children.Add(_queue);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.SlateGray };
        Grid.SetColumn(splitter, 1); grid.Children.Add(splitter);
        var right = new Grid(); right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.Children.Add(_details);
        var evidenceTitle = new TextBlock { Text = "Chứng cứ (ChapterId · UTF-16)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 3) };
        Grid.SetRow(evidenceTitle, 1); right.Children.Add(evidenceTitle); Grid.SetRow(_evidence, 2); right.Children.Add(_evidence);
        Grid.SetColumn(right, 2); grid.Children.Add(right); root.Children.Add(grid);

        _run.Click += async (_, _) => await RunAsync();
        _rerun.Click += async (_, _) => await RunAsync(forceResurvey: true);
        _blocked.Click += async (_, _) => await ShowBlockedEvidenceAsync();
        _pairs.Click += (_, _) => ShowRelationshipPairCases();
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        _save.Click += async (_, _) => await SaveSelectedAsync();
        _merge.Click += async (_, _) => await MergeSelectedAsync();
        _later.Click += async (_, _) => await SetStatusAsync(IntelligenceReviewStatus.ReviewLater);
        _reject.Click += async (_, _) => await SetStatusAsync(IntelligenceReviewStatus.Rejected);
        _open.Click += async (_, _) => await OpenSelectedEvidenceAsync();
        _viewToggle.Click += (_, _) =>
        {
            _dossierMode = !_dossierMode;
            _viewToggle.Content = _dossierMode ? "Xem đề xuất thô" : "Xem hồ sơ nhân vật";
            RefreshQueue();
        };
        _evidence.MouseDoubleClick += async (_, _) => await OpenSelectedEvidenceAsync();
        _translationContext.Click += async (_, _) => await SaveContextFeatureFlagAsync();
        _profile.SelectionChanged += (_, _) => SelectSurveyBackend();
        _model.SelectionChanged += (_, _) => { if (!_loading && _profile.SelectedItem is OllamaBackendProfile p && p.Id == GeminiFlashPoolProfileId) ShowGeminiConfiguration(); };
        _search.TextChanged += (_, _) => RefreshQueue();
        _kindFilter.SelectionChanged += (_, _) => RefreshQueue();
        _statusFilter.SelectionChanged += (_, _) => RefreshQueue();
        return root;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _loading = true;
            IntelligenceCommitResult reconciled = await new BookIntelligenceReviewService(_session).ReconcileAsync();
            _state = reconciled.State; _resources = reconciled.Resources;
            // Idempotent, safe normalization of pending results from earlier survey runs.
            // Never modify accepted or rejected user decisions.
            if (RelationshipLabelNormalizer.NormalizePending(_state))
                _state = await new BookIntelligenceStore(_session).SaveAsync(_state);
            ProjectTranslationSettings translation = await new ProjectSettingsStore(_session).LoadTranslationAsync();
            _translationContext.IsChecked = translation.BookKnowledgeContextEnabled;
            _scanMode.SelectedItem = _state.ScanMode;
            bool stateUsesGemini = _state.AnalyzerModel?.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) == true;
            bool hasGeminiKey = !string.IsNullOrWhiteSpace(GeminiApiKeyVault.ResolveForSurvey());
            OllamaBackendProfile selected = (stateUsesGemini || hasGeminiKey
                    ? _profiles.FirstOrDefault(p => p.Id == GeminiFlashPoolProfileId)
                    : _profiles.FirstOrDefault(p => p.Id == _state.AnalyzerProfileId))
                ?? _profiles.FirstOrDefault(p => p.Id == OllamaBackendProfile.DefaultLocalId)
                ?? _profiles.First();
            _profile.ItemsSource = _profiles; _profile.SelectedItem = selected;
            PopulateModelDropdown(selected, stateUsesGemini ? _state.AnalyzerModel! : selected.Model);
            RefreshQueue();
            if (selected.Id == GeminiFlashPoolProfileId) ShowGeminiConfiguration();
        }
        catch (Exception ex) { ShowFailure(ex); }
        finally { _loading = false; }
    }

    private async Task RunAsync(bool forceResurvey = false)
    {
        if (_state is null || _profile.SelectedItem is not OllamaBackendProfile profile || string.IsNullOrWhiteSpace(_model.Text)) return;
        SetBusy(true); _cancellation = new CancellationTokenSource();
        try
        {
            var store = new BookIntelligenceStore(_session);
            _summary.Text = "Đang quét 100% nguyên tác bằng CPU…";
            _state.AnalyzerProfileId = profile.Id; _state.AnalyzerModel = _model.Text.Trim();
            _state.ScanMode = _scanMode.SelectedItem is IntelligenceScanMode mode ? mode : IntelligenceScanMode.FastAdaptive;
            var indexProgress = new Progress<CorpusIndexProgress>(p =>
            {
                _progress.Maximum = Math.Max(1, p.TotalChapters); _progress.Value = p.ProcessedChapters;
                _summary.Text = $"CPU: {p.ProcessedChapters}/{p.TotalChapters} chương · quét lại {p.RescannedChapters} · {p.EvidenceCount} đoạn chứng cứ.";
            });
            _state = await new ProjectCorpusIndexer(_repository).IndexAsync(_session, _state, store,
                _resources.Characters, indexProgress, _cancellation.Token);
            _summary.Text = "Đã lập chỉ mục. Đang phân tích các đoạn có tín hiệu bằng model khảo sát…";
            if (!forceResurvey && !BookIntelligenceResurveyService.HasUnanalyzedEvidence(_state, _state.ScanMode))
            {
                _summary.Text = "Không có đoạn mới chưa khảo sát. Dữ liệu cũ được giữ để tiếp tục; " +
                    "nhấn 'Khảo sát lại' nếu muốn chạy AI lại từ đầu (có bản sao lưu).";
                return;
            }
            if (forceResurvey && MessageBox.Show(this,
                "Khảo sát lại toàn bộ các đoạn đủ điều kiện?\n" +
                "QT Studio sẽ sao lưu kết quả cũ, làm mới đề xuất chưa xác nhận và gọi lại AI (có sử dụng quota). " +
                "Tài nguyên đã duyệt không bị xóa.",
                "Xác nhận khảo sát lại", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            bool isGemini = profile.Id == GeminiFlashPoolProfileId ||
                _model.Text.Trim().StartsWith("gemini-", StringComparison.OrdinalIgnoreCase);
            if (!isGemini && profile.Kind == OllamaBackendKind.Remote && !profile.RemoteDisclosureAccepted)
                throw new InvalidOperationException("Profile Remote chưa xác nhận quyền gửi các đoạn nguyên tác để khảo sát.");
            if (isGemini && MessageBox.Show(this,
                "Khảo sát bằng Gemini sẽ gửi các đoạn nguyên tác và thông tin nhân vật liên quan đến Google API. Tiếp tục?",
                "Xác nhận gửi dữ liệu khảo sát lên cloud", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            ModelOptions baseOptions = (profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model)).ModelOptions;
            ModelOptions options = baseOptions with
            {
                Model = _model.Text.Trim(),
                Temperature = Math.Min(0.2, baseOptions.Temperature),
                ContextSize = isGemini ? Math.Max(65_536, baseOptions.ContextSize) : baseOptions.ContextSize,
                MaximumOutputTokens = isGemini ? Math.Max(8192, baseOptions.MaximumOutputTokens) : baseOptions.MaximumOutputTokens
            };
            var aiProgress = new Progress<AnalyzerProgress>(p =>
            {
                _progress.Maximum = Math.Max(1, p.TotalEvidence); _progress.Value = p.ProcessedEvidence;
                _summary.Text = $"AI: {p.ProcessedEvidence}/{p.TotalEvidence} đoạn · {p.PendingSuggestions} đề xuất chờ duyệt.";
            });

            using var httpHandler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(httpHandler) { Timeout = Timeout.InfiniteTimeSpan };
            IOllamaBackendClient? ollama = null;
            GeminiSurveyPoolClient? gemini = null;
            try
            {
                IOllamaClient backend;
                if (isGemini)
                {
                    string apiKey = GeminiApiKeyVault.ResolveForSurvey();
                    if (string.IsNullOrWhiteSpace(apiKey))
                        throw new InvalidOperationException(
                            "Chưa có Gemini API key. Vào Cài đặt → AI & hiệu năng → Gemini API để nhập và lưu key.");
                    var models = GeminiSurveyPoolClient.ModelsFromSettings(
                        Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_SURVEY_MODELS"));
                    // A deliberate dropdown choice sends requests ONLY to that model.
                    // In particular, Lite quota is never used without explicit selection.
                    models = GeminiSurveyPoolClient.ModelsFromSettings(options.Model);
                    if (!models.Any(model => string.Equals(model.Model, options.Model,
                            StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException(
                            "Model đã chọn không nằm trong Gemini Survey Pool. Chọn một model trong danh sách.");
                    _summary.Text = "Flash Survey Pool: " + string.Join(", ", models.Select(model => model.Model));
                    // Google project scope, not QT Studio book ProjectId.
                    Guid quotaScope = GeminiSurveyProjectScope.Resolve(apiKey,
                        Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_PROJECT_ID"));
                    int? maximumRequests = GeminiSurveyPoolClient.MaximumRequestsFromSettings(
                        Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_SURVEY_MAX_REQUESTS"));
                    gemini = new GeminiSurveyPoolClient(http, apiKey, quotaScope, models, maximumRequests);
                    backend = gemini;
                    _state.AnalyzerProfileId = null;
                    _state.AnalysisConcurrency = gemini.RecommendedConcurrency;
                }
                else
                {
                    ollama = OllamaClient.Create(profile);
                    backend = ollama;
                }
                if (forceResurvey)
                {
                    (BookIntelligenceState restarted, string backup) =
                        await BookIntelligenceResurveyService.BeginFreshAsync(
                            _state, store, _cancellation.Token);
                    _state = restarted;
                    RefreshQueue();
                    _summary.Text = "Đã sao lưu khảo sát cũ tại: " + backup +
                        ". Bắt đầu khảo sát mới bằng " + options.Model + "…";
                }
                _state = await new AdaptiveBookIntelligenceAnalyzer(backend).AnalyzeAsync(_session, _state, store,
                    _resources, options, aiProgress, _cancellation.Token);
            }
            finally { ollama?.Dispose(); gemini?.Dispose(); }
            var scenes = new SceneContextService();
            foreach (ProjectChapterEntry entry in _session.Manifest.Chapters.Where(x => x.DeletedAt is null).OrderBy(x => x.Number))
            {
                ChapterDocument chapter = (await _repository.LoadChapterAsync(_session, entry.ChapterId, _cancellation.Token)).Chapter;
                scenes.RebuildChapter(_state, chapter, _resources);
            }
            _state = await store.SaveAsync(_state, _cancellation.Token);
            _stateChanged(_state, _translationContext.IsChecked == true);
            RefreshQueue();
            int blockedCount = _state.Evidence.Count(x => x.IsCurrent && x.AiBlocked);
            ReviewRow? newest = Rows().FirstOrDefault(x => x.Status == IntelligenceReviewStatus.Pending.ToString());
            _summary.Text = $"{(blockedCount > 0 ? "Khảo sát kết thúc (chưa đầy đủ)" : "Khảo sát hoàn tất")} · " +
                $"{_state.Metrics.Requests} request · " +
                $"{_state.PendingCount} đề xuất chờ duyệt · {blockedCount} đoạn thuộc batch bị Google từ chối. " +
                (blockedCount > 0
                    ? "Nhấn 'Đoạn bị chặn' để kiểm tra; những đoạn này chưa được AI phân tích."
                    : newest is null ? "Không có đề xuất mới." : $"Ví dụ: {newest.Title}.");
        }
        catch (OperationCanceledException)
        {
            await ReloadLatestSurveyCheckpointAsync();
            _summary.Text = "Đã tạm dừng. Những batch hoàn thành đã được lưu; bấm Bắt đầu / Tiếp tục để chạy tiếp.";
        }
        catch (Exception ex)
        {
            await ReloadLatestSurveyCheckpointAsync();
            ShowFailure(ex);
        }
        finally { SetBusy(false); _cancellation?.Dispose(); _cancellation = null; }
    }

    private async Task ReloadLatestSurveyCheckpointAsync()
    {
        // The analyzer saves after each completed batch. If a later batch fails,
        // the window's in-memory revision may be stale. Reload before Resume.
        try
        {
            _state = await new BookIntelligenceStore(_session).LoadAsync();
            RefreshQueue();
            _stateChanged(_state, _translationContext.IsChecked == true);
        }
        catch (Exception)
        {
            // Preserve the original model/network error; users can reopen the dialog
            // to recover a checkpoint if a concurrent file operation blocked reload.
        }
    }

    private async Task SaveSelectedAsync()
    {
        if (_state is null || _queue.SelectedItem is not ReviewRow row) return;
        if (row.Value is CharacterDossierReviewService.Dossier dossier)
        {
            await ShowCharacterDossierAsync(dossier);
            return;
        }
        try
        {
            var review = new BookIntelligenceReviewService(_session);
            IntelligenceCommitResult result;
            switch (row.Value)
            {
                case IntelligenceCharacterSuggestion character:
                    CharacterProfile? edited = _editCharacter(character);
                    if (edited is null) return;
                    result = await review.AcceptCharacterAsync(_state, character.Id, edited, _resources.Revision);
                    break;
                case IntelligenceGlossarySuggestion glossary:
                    string translated = AskText("Xác nhận Glossary", glossary.Chinese, glossary.Vietnamese);
                    if (translated.Length == 0) return;
                    result = await review.AcceptGlossaryAsync(_state, glossary.Id, translated, _resources.Revision);
                    break;
                case IntelligenceRelationshipSuggestion relationship:
                    // Edit a detached copy. Never mutate the displayed review queue if
                    // the user cancels or the project/resource commit fails.
                    BookIntelligenceState draft = BookIntelligenceStore.Clone(_state);
                    IntelligenceRelationshipSuggestion editable =
                        draft.Relationships.Single(x => x.Id == relationship.Id);
                    if (!EditRelationship(editable)) return;
                    result = await review.AcceptRelationshipAsync(draft, editable.Id, _resources.Revision);
                    break;
                case DerivedRelationship derived:
                    result = await review.AcceptDerivedRelationshipAsync(_state, derived.Id);
                    break;
                default: return;
            }
            ApplyCommit(result); _summary.Text = "Đã lưu mục được xác nhận. Các mục khác vẫn ở hàng chờ.";
        }
        catch (Exception ex) { ShowFailure(ex); }
    }

    private static OllamaBackendProfile CreateGeminiFlashPoolProfile()
    {
        const string model = "gemini-3.8-flash";
        return new OllamaBackendProfile
        {
            Id = GeminiFlashPoolProfileId,
            Name = "Gemini Flash Pool",
            Kind = OllamaBackendKind.Remote,
            Model = model,
            Runtime = AiRuntimeProfile.CreateDefault(model),
            RemoteDisclosureAccepted = true
        };
    }

    private void PopulateModelDropdown(OllamaBackendProfile profile, string? preferred)
    {
        string[] choices = profile.Id == GeminiFlashPoolProfileId
            ? ["gemini-3.8-flash", "gemini-3.6-flash", "gemini-3.5-flash-lite", "gemini-3.1-flash-lite"]
            : [profile.Model];
        // A saved or externally configured model is not silently replaced.
        // Nonstandard models are only selectable via their backend's configured profile.
        string selected = choices.FirstOrDefault(x => string.Equals(x, preferred,
            StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        _model.ItemsSource = choices;
        _model.SelectedItem = selected;
    }

    private void SelectSurveyBackend()
    {
        if (_loading || _profile.SelectedItem is not OllamaBackendProfile profile ||
            profile.Id != GeminiFlashPoolProfileId) return;
        try
        {
            PopulateModelDropdown(profile, _model.SelectedItem as string);
            ShowGeminiConfiguration();
        }
        catch (Exception ex) { ShowFailure(ex); }
    }

    private void ShowGeminiConfiguration()
    {
        IReadOnlyList<GeminiSurveyModel> models = GeminiSurveyPoolClient.ModelsFromSettings(_model.Text);
        int? maximumRequests = GeminiSurveyPoolClient.MaximumRequestsFromSettings(
            Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_SURVEY_MAX_REQUESTS"));
        bool hasKey = !string.IsNullOrWhiteSpace(GeminiApiKeyVault.ResolveForSurvey());
        _summary.Text = $"Model khảo sát: {string.Join(", ", models.Select(x => x.Model))} · " +
            $"API key: {(hasKey ? "đã cấu hình" : "chưa cấu hình")} · " +
            $"ngân sách: {(maximumRequests is int limit ? $"tối đa {limit} HTTP request/lần chạy" : "chưa đặt giới hạn")}.";
    }

    private async Task MergeSelectedAsync()
    {
        if (_state is null || _queue.SelectedItem is not ReviewRow { Value: IntelligenceCharacterSuggestion suggestion } ||
            _mergeTarget.SelectedValue is not Guid target) return;
        try { ApplyCommit(await new BookIntelligenceReviewService(_session).MergeCharacterAsync(_state,
            suggestion.Id, target, _resources.Revision)); }
        catch (Exception ex) { ShowFailure(ex); }
    }

    private async Task SetStatusAsync(IntelligenceReviewStatus status)
    {
        if (_state is null || _queue.SelectedItems.Count == 0) return;
        try
        {
            ReviewRow[] selected = _queue.SelectedItems.Cast<ReviewRow>().Where(x => x.Value is not DerivedRelationship d ||
                d.ValidityStatus == InferenceValidityStatus.Valid).ToArray();
            foreach (ReviewRow row in selected)
                _state = await new BookIntelligenceReviewService(_session).SetReviewStatusAsync(_state, row.Id, status);
            RefreshQueue();
        }
        catch (Exception ex) { ShowFailure(ex); }
    }

    private async Task OpenSelectedEvidenceAsync()
    {
        if (_evidence.SelectedItem is EvidenceRow item) await _openEvidence(item.Evidence);
    }

    private void ApplyCommit(IntelligenceCommitResult result)
    {
        _resources = result.Resources; _state = result.State; _resourcesChanged(_resources);
        _stateChanged(_state, _translationContext.IsChecked == true); RefreshQueue();
    }

    private async Task SaveContextFeatureFlagAsync()
    {
        if (_loading || _state is null) return;
        try
        {
            var settings = new ProjectSettingsStore(_session);
            ProjectTranslationSettings translation = await settings.LoadTranslationAsync();
            translation.BookKnowledgeContextEnabled = _translationContext.IsChecked == true;
            await settings.SaveTranslationAsync(translation);
            _stateChanged(_state, translation.BookKnowledgeContextEnabled);
            _summary.Text = translation.BookKnowledgeContextEnabled
                ? "Knowledge Context đã bật. Chỉ dữ liệu đã duyệt và đúng timeline được thêm vào request mới."
                : "Knowledge Context đã tắt. Pipeline dịch dùng hành vi cũ.";
        }
        catch (Exception ex) { _translationContext.IsChecked = false; ShowFailure(ex); }
    }

    private Task ShowCharacterDossierAsync(CharacterDossierReviewService.Dossier initial)
    {
        if (_state is null) return Task.CompletedTask;

        var heading = new TextBlock
        {
            FontSize = 19, FontWeight = FontWeights.SemiBold,
            Text = "Hồ sơ: " + initial.Display, Margin = new Thickness(0, 0, 0, 6)
        };
        var characterInfo = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = "Tên Trung: " + initial.ChineseName + "   |   Tên Việt: " + initial.VietnameseName +
                   "\nBí danh: " + string.Join(", ", initial.Aliases) +
                   "\nGiới tính: " + initial.Gender + "   |   Đại từ: " +
                   initial.PrimaryPronoun + " / " + initial.SecondaryPronoun
        };
        var approveCharacter = new Button
        {
            Content = "Sửa / duyệt hồ sơ nhân vật",
            Margin = new Thickness(0, 0, 7, 5), MinHeight = 30
        };
        var characterWarning = new TextBlock
        {
            Text = "Tên, bí danh và đại từ được đề xuất từ chứng cứ. Chỉ khi bạn bấm duyệt mới lưu vào Characters. " +
                   "Quan hệ vẫn phải kiểm tra riêng; không tự duyệt theo nhân vật.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var related = new ListBox { MinWidth = 270, DisplayMemberPath =
            nameof(CharacterDossierReviewService.RelatedPerson.Display) };
        var details = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        var relationHeader = new TextBlock
        {
            Text = "Chọn một quan hệ để xem và chỉnh bản nháp", FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        details.Children.Add(relationHeader);
        TextBox Field(string name)
        {
            details.Children.Add(new TextBlock { Text = name, Margin = new Thickness(0, 7, 0, 2) });
            var field = new TextBox { MinHeight = 27, IsEnabled = false };
            details.Children.Add(field);
            return field;
        }
        TextBox type = Field("Loại quan hệ (cần chứng cứ)");
        var directionLabel = new TextBlock { Text = "Ai giữ vai trò đầu của quan hệ có chiều?",
            Margin = new Thickness(0, 7, 0, 2) };
        var direction = new ComboBox { IsEditable = false, IsEnabled = false, MinHeight = 27 };
        details.Children.Add(directionLabel); details.Children.Add(direction);
        TextBox self = Field("Nhân vật thứ nhất tự xưng");
        TextBox call = Field("Nhân vật thứ nhất gọi người thứ hai");
        TextBox reverseSelf = Field("Nhân vật thứ hai tự xưng");
        TextBox reverseCall = Field("Nhân vật thứ hai gọi người thứ nhất");
        TextBox from = Field("Có hiệu lực từ chương (để trống nếu chưa rõ)");
        TextBox to = Field("Có hiệu lực đến chương (để trống nếu chưa rõ)");
        TextBox revealed = Field("Tiết lộ từ chương (để trống nếu chưa rõ)");
        var audit = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 115, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        details.Children.Add(new TextBlock
        {
            Text = "Chứng cứ và mâu thuẫn", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 9, 0, 3)
        });
        details.Children.Add(audit);
        var saveRelation = new Button
        {
            Content = "Xác nhận quan hệ này", MinHeight = 34, IsEnabled = false,
            Margin = new Thickness(0, 10, 0, 0)
        };
        details.Children.Add(saveRelation);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        columns.Children.Add(related);
        var editorScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = details };
        Grid.SetColumn(editorScroll, 1); columns.Children.Add(editorScroll);

        var root = new DockPanel { Margin = new Thickness(14) };
        var header = new StackPanel();
        header.Children.Add(heading); header.Children.Add(characterInfo);
        header.Children.Add(approveCharacter); header.Children.Add(characterWarning);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        root.Children.Add(columns);
        var dialog = new Window
        {
            Owner = this, Title = "QT Studio · Hồ sơ nhân vật và quan hệ",
            Width = 1120, Height = 810, MinWidth = 840, MinHeight = 590,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root
        };

        CharacterDossierReviewService.Dossier current = initial;
        CharacterDossierReviewService.RelatedPerson? selectedRelation = null;
        IntelligenceRelationshipSuggestion? selectedClaim = null;

        void ReloadDossier()
        {
            if (_state is null) return;
            IReadOnlyList<CharacterDossierReviewService.Dossier> updated =
                CharacterDossierReviewService.Build(_state, _resources);
            current = updated.FirstOrDefault(x => x.IdentityKey == initial.IdentityKey)
                ?? updated.FirstOrDefault(x => x.ChineseName == initial.ChineseName ||
                     x.Aliases.Contains(initial.ChineseName, StringComparer.Ordinal))
                ?? current;
            characterInfo.Text = "Tên Trung: " + current.ChineseName + "   |   Tên Việt: " +
                current.VietnameseName + "\nBí danh: " + string.Join(", ", current.Aliases) +
                "\nGiới tính: " + current.Gender + "   |   Đại từ: " +
                current.PrimaryPronoun + " / " + current.SecondaryPronoun;
            approveCharacter.IsEnabled = current.CharacterSuggestions.Any(x => x.Status is
                IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater);
            approveCharacter.Content = current.IsCommitted
                ? "Xác nhận gộp đề xuất AI vào nhân vật đã có"
                : "Sửa / duyệt hồ sơ nhân vật";
            related.ItemsSource = current.Relations;
        }
        void SelectRelation()
        {
            selectedRelation = related.SelectedItem as CharacterDossierReviewService.RelatedPerson;
            selectedClaim = selectedRelation?.Claims.Where(x => x.Status is
                IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater)
                .OrderByDescending(x => x.EvidenceIds.Count).FirstOrDefault();
            saveRelation.IsEnabled = selectedClaim is not null;
            bool enabled = selectedClaim is not null;
            foreach (TextBox box in new[] { type, self, call, reverseSelf, reverseCall, from, to, revealed })
                box.IsEnabled = enabled;
            direction.IsEnabled = enabled;
            if (selectedRelation is null)
            {
                audit.Text = ""; relationHeader.Text = "Chọn một quan hệ để xem và chỉnh bản nháp";
                return;
            }
            relationHeader.Text = current.ChineseName + " ⇄ " +
                selectedRelation.OtherChineseName + " — " + selectedRelation.ReviewStatus;
            IntelligenceRelationshipSuggestion representative =
                selectedClaim ?? selectedRelation.Claims[0];
            type.Text = RelationshipAddressingReview.EditableLabel(representative.Relationship);
            self.Text = selectedRelation.ConsensusAddressing(representative, 0);
            call.Text = selectedRelation.ConsensusAddressing(representative, 1);
            reverseSelf.Text = selectedRelation.ConsensusAddressing(representative, 2);
            reverseCall.Text = selectedRelation.ConsensusAddressing(representative, 3);
            from.Text = selectedRelation.ConsensusChapter(x => x.ValidFromChapter)?.ToString() ?? "";
            to.Text = selectedRelation.ConsensusChapter(x => x.ValidToChapter)?.ToString() ?? "";
            revealed.Text = selectedRelation.ConsensusChapter(x => x.RevealedAtChapter)?.ToString() ?? "";
            RelationshipLabelNormalizer.Result classification =
                RelationshipLabelNormalizer.Normalize(type.Text);
            bool hasDirection = classification.Classification == RelationshipLabelNormalizer.Kind.Stable &&
                RelationshipIntelligenceQualityGate.RequiresDirection(classification.RelationType);
            directionLabel.Visibility = hasDirection ? Visibility.Visible : Visibility.Collapsed;
            direction.Visibility = hasDirection ? Visibility.Visible : Visibility.Collapsed;
            direction.ItemsSource = new[]
            {
                "Chưa xác nhận — không duyệt quan hệ có chiều",
                representative.SpeakerChineseName + " giữ vai trò đầu của quan hệ",
                representative.ListenerChineseName + " giữ vai trò đầu của quan hệ"
            };
            direction.SelectedIndex = hasDirection ? (int)representative.ConfirmedDirection : 0;
            var evidenceMap = _state!.Evidence.ToDictionary(x => x.Id);
            audit.Text = (selectedRelation.HasConflictingAddressing ||
                          selectedRelation.HasConflictingTimeline
                ? "CẢNH BÁO: Các batch có xưng hô hoặc mốc chương khác nhau. Không duyệt khi chưa kiểm chứng.\n\n"
                : "") +
                "Giá trị AI gốc: " + representative.SelfReference + " / " +
                    representative.AddresseeReference + " / " +
                    representative.ReverseSelfReference + " / " +
                    representative.ReverseAddresseeReference + "\n" +
                string.Join("\n\n", selectedRelation.EvidenceIds
                    .Where(evidenceMap.ContainsKey).Take(6)
                    .Select(id => ShortChapter(evidenceMap[id].ChapterId) + " · " +
                        evidenceMap[id].StartUtf16 + ": " +
                        evidenceMap[id].Excerpt.Replace('\r', ' ').Replace('\n', ' '))) +
                (selectedRelation.EvidenceIds.Count > 6
                    ? "\n… mở đề xuất thô để xem toàn bộ chứng cứ." : "");
        }
        related.SelectionChanged += (_, _) => SelectRelation();
        type.TextChanged += (_, _) =>
        {
            if (selectedClaim is null) return;
            var mapped = RelationshipLabelNormalizer.Normalize(type.Text);
            bool needs = mapped.Classification == RelationshipLabelNormalizer.Kind.Stable &&
                RelationshipIntelligenceQualityGate.RequiresDirection(mapped.RelationType);
            direction.Visibility = needs ? Visibility.Visible : Visibility.Collapsed;
            directionLabel.Visibility = needs ? Visibility.Visible : Visibility.Collapsed;
        };
        approveCharacter.Click += async (_, _) =>
        {
            if (_state is null) return;
            IntelligenceCharacterSuggestion? character = current.CharacterSuggestions.FirstOrDefault(x =>
                x.Status is IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater);
            if (character is null) return;
            try
            {
                var review = new BookIntelligenceReviewService(_session);
                if (current.IsCommitted)
                {
                    CharacterProfile? existing = _resources.Characters.Profiles.FirstOrDefault(p =>
                        current.IdentityKey == "P:" + p.Id.ToString("D"));
                    if (existing is null) throw new InvalidOperationException(
                        "Không xác định được hồ sơ đã lưu; hãy mở lại Project.");
                    if (MessageBox.Show(dialog,
                        "Tên Trung của ứng viên trùng hồ sơ " + existing.VietnameseName +
                        ". Chỉ gộp sau khi kiểm tra đúng người và các bí danh. " +
                        "Không tự hợp nhất quan hệ. Xác nhận gộp?",
                        "Xác nhận danh tính", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                        != MessageBoxResult.Yes) return;
                    ApplyCommit(await review.MergeCharacterAsync(
                        _state, character.Id, existing.Id, _resources.Revision));
                }
                else
                {
                    CharacterProfile? edited = _editCharacter(character);
                    if (edited is null) return;
                    ApplyCommit(await review.AcceptCharacterAsync(
                        _state, character.Id, edited, _resources.Revision));
                }
                ReloadDossier();
                MessageBox.Show(dialog, "Đã lưu danh tính được xác nhận. Quan hệ chưa được tự duyệt.",
                    "QT Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Không thể lưu hồ sơ",
                MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        saveRelation.Click += async (_, _) =>
        {
            if (_state is null || selectedClaim is null) return;
            if (!TryChapter(from.Text, out int? start) ||
                !TryChapter(to.Text, out int? end) ||
                !TryChapter(revealed.Text, out int? reveal) ||
                (start.HasValue && end.HasValue && start > end))
            {
                MessageBox.Show(dialog, "Mốc chương không hợp lệ.", "QT Studio",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var parsed = RelationshipLabelNormalizer.Normalize(type.Text);
            if (parsed.Classification != RelationshipLabelNormalizer.Kind.Stable)
            {
                MessageBox.Show(dialog, "Loại quan hệ chưa được nhận diện là quan hệ ổn định. " +
                    "Hãy chỉnh loại quan hệ hoặc mở chế độ kỹ thuật để kiểm tra.",
                    "QT Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (RelationshipIntelligenceQualityGate.RequiresDirection(parsed.RelationType) &&
                direction.SelectedIndex <= 0)
            {
                MessageBox.Show(dialog, "Phải chọn rõ người giữ vai trò đầu từ nguyên tác.",
                    "QT Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MessageBox.Show(dialog,
                "Đã đối chiếu chứng cứ và xác nhận quan hệ giữa " +
                selectedClaim.SpeakerChineseName + " với " + selectedClaim.ListenerChineseName +
                "? Các ô xưng hô trống sẽ KHÔNG được thêm vào quy tắc dịch.",
                "Xác nhận có chứng cứ", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;
            if (!_resources.Characters.Profiles.Any(p =>
                    p.ChineseName == selectedClaim.SpeakerChineseName ||
                    p.Aliases.Contains(selectedClaim.SpeakerChineseName, StringComparer.Ordinal)) ||
                !_resources.Characters.Profiles.Any(p =>
                    p.ChineseName == selectedClaim.ListenerChineseName ||
                    p.Aliases.Contains(selectedClaim.ListenerChineseName, StringComparer.Ordinal)))
            {
                MessageBox.Show(dialog, "Cần xác nhận hồ sơ CẢ HAI nhân vật trước khi duyệt " +
                    "quan hệ để Knowledge Graph dùng ID nhân vật chính thức, không dùng ID ứng viên. " +
                    "Có thể duyệt nhân vật trước trong các hồ sơ tương ứng.",
                    "Nhân vật chưa có trong Characters", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            try
            {
                BookIntelligenceState draft = BookIntelligenceStore.Clone(_state);
                IntelligenceRelationshipSuggestion editable = draft.Relationships.Single(x => x.Id == selectedClaim.Id);
                editable.Relationship = type.Text.Trim();
                editable.ConfirmedDirection = RelationshipIntelligenceQualityGate.RequiresDirection(parsed.RelationType)
                    ? (RelationshipIntelligenceQualityGate.Direction)direction.SelectedIndex
                    : RelationshipIntelligenceQualityGate.Direction.Unverified;
                editable.SelfReference = self.Text.Trim();
                editable.AddresseeReference = call.Text.Trim();
                editable.ReverseSelfReference = reverseSelf.Text.Trim();
                editable.ReverseAddresseeReference = reverseCall.Text.Trim();
                editable.ValidFromChapter = start;
                editable.ValidToChapter = end;
                editable.RevealedAtChapter = reveal;
                ApplyCommit(await new BookIntelligenceReviewService(_session).AcceptRelationshipAsync(
                    draft, editable.Id, _resources.Revision));
                ReloadDossier();
                MessageBox.Show(dialog, "Đã duyệt quan hệ đã chọn. Các giả thuyết khác vẫn được giữ.",
                    "QT Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Không thể duyệt quan hệ",
                MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        ReloadDossier();
        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    private void ShowRelationshipPairCases()
    {
        if (_state is null) return;
        IReadOnlyList<RelationshipPairReviewService.PairCase> cases =
            RelationshipPairReviewService.Build(_state, _resources);
        if (cases.Count == 0) return;
        var caseList = new ListBox
        {
            ItemsSource = cases, DisplayMemberPath = nameof(RelationshipPairReviewService.PairCase.Summary),
            MinWidth = 320
        };
        var claimList = new ListBox { DisplayMemberPath = nameof(PairClaimRow.Label), MinWidth = 320 };
        var hint = new TextBlock
        {
            Text = "Mỗi cặp nhân vật chỉ có một hồ sơ tổng hợp. Các kết luận khác nhau, " +
                "mốc thời gian và chứng cứ gốc được giữ nguyên; không tự hợp nhất quan hệ mâu thuẫn.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10)
        };
        var left = new DockPanel();
        var title = new TextBlock { Text = "Cặp nhân vật", FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(title, Dock.Top);
        left.Children.Add(title); left.Children.Add(caseList);
        var right = new DockPanel();
        var subTitle = new TextBlock { Text = "Các giả thuyết / mốc quan hệ", FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(subTitle, Dock.Top);
        right.Children.Add(subTitle); right.Children.Add(claimList);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.Children.Add(left); Grid.SetColumn(right, 1); columns.Children.Add(right);
        var jump = new Button { Content = "Mở đề xuất đã chọn để duyệt riêng", MinHeight = 32,
            Margin = new Thickness(10) };
        var root = new DockPanel();
        DockPanel.SetDock(hint, Dock.Top); DockPanel.SetDock(jump, Dock.Bottom);
        root.Children.Add(hint); root.Children.Add(jump); root.Children.Add(columns);
        var dialog = new Window { Owner = this, Title = "Hồ sơ quan hệ theo cặp",
            Width = 1000, Height = 620, MinWidth = 740, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root };
        caseList.SelectionChanged += (_, _) =>
        {
            if (caseList.SelectedItem is RelationshipPairReviewService.PairCase chosen)
                claimList.ItemsSource = chosen.Claims.Select(claim => new PairClaimRow(claim,
                    claim.Relationship + " · " + claim.Status + " · " +
                    claim.EvidenceIds.Count + " chứng cứ · " +
                    "từ chương " + (claim.ValidFromChapter?.ToString() ?? "?") +
                    " đến " + (claim.ValidToChapter?.ToString() ?? "?"))).ToArray();
        };
        void Jump()
        {
            if (claimList.SelectedItem is not PairClaimRow row) return;
            _kindFilter.SelectedIndex = 0;
            _statusFilter.SelectedIndex = 0;
            _search.Text = "";
            RefreshQueue();
            ReviewRow? target = Rows().FirstOrDefault(x => x.Id == row.Claim.Id);
            if (target is null) return;
            _queue.SelectedItem = target;
            _queue.ScrollIntoView(target);
            dialog.Close();
        }
        jump.Click += (_, _) => Jump();
        claimList.MouseDoubleClick += (_, _) => Jump();
        caseList.SelectedIndex = 0;
        dialog.ShowDialog();
    }

    private async Task ShowBlockedEvidenceAsync()
    {
        if (_state is null) return;
        List<BlockedEvidenceRow> rows = _state.Evidence.Where(x => x.IsCurrent && x.AiBlocked)
            .Select(x => new BlockedEvidenceRow(x,
                $"{ShortChapter(x.ChapterId)} · UTF-16 {x.StartUtf16}+{x.LengthUtf16} · " +
                $"Google: {x.AiBlockReason} · {Preview(x.Excerpt)}"))
            .ToList();
        if (rows.Count == 0) return;
        var list = new ListBox { ItemsSource = rows, DisplayMemberPath = nameof(BlockedEvidenceRow.Label),
            MinHeight = 250, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var explanation = new TextBlock
        {
            Text = $"{rows.Count} đoạn nằm trong batch Gemini từ chối. Không thể kết luận " +
                "đoạn cụ thể nào gây ra việc chặn. Chúng chưa được phân tích và vẫn có nguyên tác để kiểm tra thủ công.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        var open = new Button { Content = "Mở đoạn nguyên tác", Margin = new Thickness(0, 10, 0, 0) };
        var panel = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(explanation, Dock.Top);
        DockPanel.SetDock(open, Dock.Bottom);
        panel.Children.Add(explanation); panel.Children.Add(open); panel.Children.Add(list);
        var dialog = new Window { Owner = this, Title = "Các đoạn Gemini không phân tích được",
            Width = 850, Height = 520, MinWidth = 600, MinHeight = 330,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        open.Click += async (_, _) =>
        {
            if (list.SelectedItem is not BlockedEvidenceRow selected) return;
            try
            {
                await _openEvidence(selected.Evidence);
                dialog.Close();
            }
            catch (Exception ex) { ShowFailure(ex); }
        };
        dialog.ShowDialog();
        await Task.CompletedTask;
    }

    private void RefreshQueue()
    {
        _queue.ItemsSource = Rows();
        _kindFilter.IsEnabled = !_dossierMode;
        _merge.IsEnabled = !_dossierMode;
        _mergeTarget.IsEnabled = !_dossierMode;
        _later.IsEnabled = !_dossierMode;
        _reject.IsEnabled = !_dossierMode;
        _save.Content = _dossierMode ? "Mở hồ sơ nhân vật" : "Chỉnh sửa rồi lưu";
        _blocked.IsEnabled = _state?.Evidence.Any(x => x.IsCurrent && x.AiBlocked) == true;
        _pairs.IsEnabled = _state?.Relationships.Count > 0;
        _mergeTarget.ItemsSource = _resources.Characters.Profiles.OrderBy(x => x.VietnameseName).ToList();
        if (_state is not null)
        {
            IReadOnlyList<CharacterDossierReviewService.Dossier> dossiers =
                CharacterDossierReviewService.Build(_state, _resources);
            _summary.Text = _dossierMode
                ? $"{dossiers.Count} hồ sơ nhân vật · {dossiers.Count(x => x.NeedsReview)} cần duyệt · " +
                  $"{CharacterDossierReviewService.CountPendingRelationGroups(dossiers)} nhóm quan hệ chờ xác minh. " +
                  "Không tự duyệt AI."
                : $"{_state.Evidence.Count(x => x.IsCurrent)} đoạn chứng cứ · {_state.PendingCount} đề xuất thô chờ duyệt.";
        }
        ShowSelection();
    }

    private List<ReviewRow> Rows()
    {
        if (_state is null) return [];
        if (_dossierMode)
        {
            IEnumerable<ReviewRow> dossiers = CharacterDossierReviewService.Build(_state, _resources)
                .Select(x => new ReviewRow(x.Id, "Hồ sơ", x.Display + " · " + x.Description,
                    x.IdentityAmbiguous ? "Ambiguous" : "Proposed", x.ReviewStatus,
                    x.IsCommitted ? "Đã duyệt" : "AI + chứng cứ", x.EvidenceIds.Count, x));
            string dossierQuery = _search.Text.Trim();
            if (dossierQuery.Length > 0)
                dossiers = dossiers.Where(x => x.Title.Contains(dossierQuery, StringComparison.OrdinalIgnoreCase) ||
                    x.Value is CharacterDossierReviewService.Dossier d &&
                    d.Aliases.Any(a => a.Contains(dossierQuery, StringComparison.OrdinalIgnoreCase)));
            dossiers = _statusFilter.SelectedIndex switch
            {
                1 => dossiers.Where(x => x.Status == "Pending" || x.Status == "Ambiguous"),
                2 => dossiers.Where(x => x.Status == "Accepted"),
                3 => dossiers.Where(x => x.Status == "Ambiguous" ||
                    x.Value is CharacterDossierReviewService.Dossier d &&
                    d.Relations.Any(r => r.HasConflictingAddressing || r.HasConflictingTimeline)),
                _ => dossiers
            };
            return dossiers.ToList();
        }
        IEnumerable<ReviewRow> rows = _state.Characters.Select(x => new ReviewRow(x.Id, "Nhân vật", $"{x.ChineseName} → {x.VietnameseName}", x.Confidence.ToString(), x.Status.ToString(), "AI trực tiếp", x.EvidenceIds.Count, x))
            .Concat(_state.Relationships.Select(x => new ReviewRow(x.Id, "Quan hệ", $"{x.SpeakerChineseName} → {x.ListenerChineseName}: {RelationshipLabelNormalizer.Display(x.Relationship)}", x.Confidence.ToString(), x.Status.ToString(), "AI trực tiếp", x.EvidenceIds.Count, x)))
            .Concat(_state.DerivedRelationships.Select(x => new ReviewRow(x.Id, "Suy luận", $"{CharacterName(x.SubjectCharacterId)} → {CharacterName(x.TargetCharacterId)}: {RelationDisplay(x.OutputRelation)}",
                x.ValidityStatus.ToString(), x.Status.ToString(), x.RuleId, x.SupportingEvidenceIds.Count, x)))
            .Concat(_state.Glossary.Select(x => new ReviewRow(x.Id, "Glossary", $"{x.Chinese} → {x.Vietnamese}", x.Confidence.ToString(), x.Status.ToString(), GlossaryScopeDisplay(x.Scope), x.EvidenceIds.Count, x)));
        // Keep auto-merged items available under "Đã xử lý", but avoid cluttering
        // the default review list with labels that are already represented elsewhere.
        if (_statusFilter.SelectedIndex == 0)
            rows = rows.Where(x => x.Value is not IntelligenceRelationshipSuggestion r ||
                !(r.Status == IntelligenceReviewStatus.Merged &&
                  r.Reason.Contains("Trùng nhãn đã chuẩn hóa;", StringComparison.Ordinal)));
        string query = _search.Text.Trim();
        if (query.Length > 0) rows = rows.Where(x => x.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            x.Value is IntelligenceCharacterSuggestion c && c.Aliases.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)));
        if (_kindFilter.SelectedIndex > 0 && _kindFilter.SelectedItem is string kind) rows = rows.Where(x => x.Kind == kind);
        rows = _statusFilter.SelectedIndex switch
        {
            1 => rows.Where(x => x.Status.Contains("Pending", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("Review", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("Awaiting", StringComparison.OrdinalIgnoreCase)),
            2 => rows.Where(x => x.Status.Contains("Accepted", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("Merged", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("Rejected", StringComparison.OrdinalIgnoreCase)),
            3 => rows.Where(x => x.Status.Contains("Conflict", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("Stale", StringComparison.OrdinalIgnoreCase) || x.Confidence is "Conflict" or "Stale" or "Ambiguous"),
            _ => rows
        };
        return rows
            .OrderBy(x => x.Status is nameof(IntelligenceReviewStatus.Pending) or nameof(IntelligenceReviewStatus.ReviewLater) ? 0 : 1)
            .ThenBy(x => x.Kind).ThenBy(x => x.Title).ToList();
    }

    private void ShowSelection()
    {
        if (_state is null || _queue.SelectedItem is not ReviewRow row)
        { _details.Text = "Chọn một đề xuất để xem lý do và chứng cứ."; _evidence.ItemsSource = null; return; }
        string reason;
        IEnumerable<Guid> ids;
        switch (row.Value)
        {
            case CharacterDossierReviewService.Dossier x:
                reason = $"HỒ SƠ NHÂN VẬT: {x.Display}\n" +
                    $"Bí danh: {string.Join(", ", x.Aliases)}\nGiới tính: {x.Gender}\n" +
                    $"Đại từ: {x.PrimaryPronoun} / {x.SecondaryPronoun}\n" +
                    $"Quan hệ: {x.Relations.Count}, cần duyệt: {x.PendingRelations}, kết luận thô: {x.RawRelationshipClaims}\n\n" +
                    string.Join("\n", x.Relations.Select(r => "• " + r.Display));
                ids = x.EvidenceIds; break;
            case IntelligenceCharacterSuggestion x:
                reason = $"Tên Trung: {x.ChineseName}\nTên Việt đề xuất: {x.VietnameseName}\nBí danh: {string.Join(", ", x.Aliases)}\nGiới tính: {x.Gender}\nĐại từ: {x.PrimaryNarrativePronoun} / {x.SecondaryNarrativePronoun}\n\n{x.Reason}";
                ids = x.EvidenceIds; break;
            case IntelligenceRelationshipSuggestion x:
                reason = $"{x.SpeakerChineseName} → {x.ListenerChineseName}\nQuan hệ: {RelationshipLabelNormalizer.Display(x.Relationship)}\nXưng hô: {x.SelfReference} → {x.AddresseeReference}\nChiều ngược đề xuất: {x.ReverseSelfReference} → {x.ReverseAddresseeReference}\n\n{x.Reason}";
                ids = x.EvidenceIds; break;
            case IntelligenceGlossarySuggestion x:
                reason = $"{x.Chinese} → {x.Vietnamese}\nLoại: {x.Category}\nPhụ thuộc ngữ cảnh: {(x.ContextDependent ? "Có — cần thận trọng" : "Không")}\n\n{x.Reason}";
                ids = x.EvidenceIds; break;
            case DerivedRelationship x:
                reason = $"Quan hệ suy luận: {CharacterName(x.SubjectCharacterId)} → {CharacterName(x.TargetCharacterId)} = {RelationDisplay(x.OutputRelation)}\nQuy tắc: {x.RuleId}\nĐộ sâu: {x.InferenceDepth}\nValidity: {x.ValidityStatus}\nInput relations: {string.Join(", ", x.InputRelationIds)}\n\n{x.Explanation}\n\nQuan hệ thực tế này không tự tạo cách xưng hô.";
                ids = x.SupportingEvidenceIds; break;
            default: reason = ""; ids = Array.Empty<Guid>(); break;
        }
        _details.Text = reason;
        Dictionary<Guid, CorpusEvidence> map = _state.Evidence.ToDictionary(x => x.Id);
        _evidence.ItemsSource = ids.Where(map.ContainsKey).Select(id => new EvidenceRow(map[id],
            $"{ShortChapter(map[id].ChapterId)} · {map[id].StartUtf16}+{map[id].LengthUtf16} · {Preview(map[id].Excerpt)}")).ToList();
        _merge.IsEnabled = row.Value is IntelligenceCharacterSuggestion;
        _mergeTarget.IsEnabled = _merge.IsEnabled;
    }

    private string CharacterName(Guid id) => _resources.Characters.Profiles.SingleOrDefault(x => x.Id == id)?.VietnameseName
        ?? _state?.CandidateIdentities.SingleOrDefault(x => x.Id == id)?.VietnameseName
        ?? id.ToString("D");

    private bool EditRelationship(IntelligenceRelationshipSuggestion item)
    {
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = $"{item.SpeakerChineseName} → {item.ListenerChineseName}",
            FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Ô trống nghĩa là Gemini chưa tìm được cách gọi có chứng cứ, KHÔNG phải lỗi hiển thị. " +
                   "Các cách gọi Trung văn rõ nghĩa được chuyển thành bản nháp tiếng Việt để bạn kiểm tra.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10)
        });

        TextBox Field(string label, string value)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 2) });
            var box = new TextBox { Text = value, MinHeight = 26 };
            panel.Children.Add(box);
            return box;
        }

        TextBox relation = Field("Quan hệ (duyệt lại theo nguyên tác)",
            RelationshipAddressingReview.EditableLabel(item.Relationship));
        var directionLabel = new TextBlock
        {
            Text = "Chiều vai trò (bắt buộc với cha/con, thầy/trò, cấp trên/dưới)",
            Margin = new Thickness(0, 9, 0, 3)
        };
        var direction = new ComboBox { MinHeight = 28, IsEditable = false };
        panel.Children.Add(directionLabel);
        panel.Children.Add(direction);
        void UpdateDirectionChoices()
        {
            RelationshipLabelNormalizer.Result mapped =
                RelationshipLabelNormalizer.Normalize(relation.Text);
            bool directional = mapped.Classification == RelationshipLabelNormalizer.Kind.Stable &&
                RelationshipIntelligenceQualityGate.RequiresDirection(mapped.RelationType);
            directionLabel.Visibility = directional ? Visibility.Visible : Visibility.Collapsed;
            direction.Visibility = directional ? Visibility.Visible : Visibility.Collapsed;
            if (!directional) return;
            string role = mapped.RelationType switch
            {
                KnowledgeRelationType.Parent or KnowledgeRelationType.BiologicalFather or
                    KnowledgeRelationType.BiologicalMother or KnowledgeRelationType.AdoptiveParent
                    => "cha/mẹ",
                KnowledgeRelationType.Child or KnowledgeRelationType.BiologicalChild or
                    KnowledgeRelationType.AdoptiveChild => "con",
                KnowledgeRelationType.Teacher => "thầy/cô",
                KnowledgeRelationType.Disciple => "học trò",
                KnowledgeRelationType.Superior => "cấp trên",
                KnowledgeRelationType.Subordinate => "cấp dưới",
                KnowledgeRelationType.Grandfather or KnowledgeRelationType.Grandmother => "ông/bà",
                KnowledgeRelationType.Grandchild => "cháu",
                _ => "vai trò nêu trong quan hệ"
            };
            int selected = Math.Clamp((int)item.ConfirmedDirection, 0, 2);
            direction.ItemsSource = new[]
            {
                "Chưa xác minh — không thể duyệt quan hệ có chiều",
                item.SpeakerChineseName + " là " + role + " của " + item.ListenerChineseName,
                item.ListenerChineseName + " là " + role + " của " + item.SpeakerChineseName
            };
            direction.SelectedIndex = selected;
        }
        relation.TextChanged += (_, _) => UpdateDirectionChoices();
        UpdateDirectionChoices();
        string[] sourceValues =
        [
            item.SelfReference, item.AddresseeReference,
            item.ReverseSelfReference, item.ReverseAddresseeReference
        ];
        // Present a safe VIETNAMESE edit draft even for old AI results saved
        // before the new gate. Original Chinese values stay visible in notes.
        string SafeDraft(string raw) => RelationshipIntelligenceQualityGate.SafeAddressingDraft(
            RelationshipAddressingReview.VietnameseDraft(raw));
        TextBox self = Field("Người thứ nhất tự xưng", SafeDraft(sourceValues[0]));
        TextBox address = Field("Người thứ nhất gọi người thứ hai", SafeDraft(sourceValues[1]));
        TextBox reverseSelf = Field("Người thứ hai tự xưng", SafeDraft(sourceValues[2]));
        TextBox reverseAddress = Field("Người thứ hai gọi người thứ nhất", SafeDraft(sourceValues[3]));

        string[] notes = sourceValues.Select((value, index) =>
            (index + 1).ToString() + ". " + RelationshipAddressingReview.ConversionNote(value)).ToArray();
        panel.Children.Add(new TextBlock
        {
            Text = "Gợi ý AI gốc / giải thích:\n" + string.Join("\n", notes) +
                   "\n" + RelationshipAddressingReview.ManualReviewHint(item.Relationship),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 8, 0, 12)
        });
        TextBox validFrom = Field("Có hiệu lực từ chương (để trống nếu chưa rõ)",
            item.ValidFromChapter?.ToString() ?? "");
        TextBox validTo = Field("Có hiệu lực đến chương (để trống nếu chưa rõ)",
            item.ValidToChapter?.ToString() ?? "");
        TextBox revealedAt = Field("Được tiết lộ từ chương",
            item.RevealedAtChapter?.ToString() ?? "");

        if (_state is not null)
        {
            Dictionary<Guid, CorpusEvidence> evidence = _state.Evidence.ToDictionary(x => x.Id);
            string[] excerpts = item.EvidenceIds.Where(evidence.ContainsKey)
                .Take(3)
                .Select(id => ShortChapter(evidence[id].ChapterId) + " · " +
                    evidence[id].StartUtf16 + ": " +
                    evidence[id].Excerpt.Replace('\r', ' ').Replace('\n', ' ').Trim())
                .ToArray();
            if (excerpts.Length > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Chứng cứ gốc (đọc trước khi xác nhận):",
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 3)
                });
                panel.Children.Add(new TextBox
                {
                    Text = string.Join("\n\n", excerpts), IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                    MinHeight = 95, MaxHeight = 150,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                });
            }
        }

        var save = new Button { Content = "Xác nhận lưu", IsDefault = true,
            Margin = new Thickness(0, 12, 0, 0), MinHeight = 33 };
        panel.Children.Add(save);
        var dialog = new Window
        {
            Owner = this, Title = "Kiểm tra quan hệ & xưng hô (bản nháp)",
            Width = 580, Height = 790, MinHeight = 540, MinWidth = 510,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel
            }
        };
        save.Click += (_, _) =>
        {
            if (!TryChapter(validFrom.Text, out int? from) ||
                !TryChapter(validTo.Text, out int? to) ||
                !TryChapter(revealedAt.Text, out int? revealed) ||
                from is not null && to is not null && from > to)
            {
                MessageBox.Show(dialog, "Mốc chương không hợp lệ hoặc chương bắt đầu lớn hơn chương kết thúc.",
                    "QT Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(relation.Text))
            {
                MessageBox.Show(dialog, "Quan hệ không được để trống.",
                    "QT Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            RelationshipLabelNormalizer.Result mapped =
                RelationshipLabelNormalizer.Normalize(relation.Text);
            bool directional = mapped.Classification == RelationshipLabelNormalizer.Kind.Stable &&
                RelationshipIntelligenceQualityGate.RequiresDirection(mapped.RelationType);
            if (directional && direction.SelectedIndex <= 0)
            {
                MessageBox.Show(dialog,
                    "Quan hệ có chiều: hãy xác minh từ nguyên tác ai là cha/mẹ, con, thầy/trò hoặc cấp trên. " +
                    "Không được suy ra vai trò chỉ từ vị trí hai tên do AI trả về.",
                    "Cần chọn chiều quan hệ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            bool incomplete = string.IsNullOrWhiteSpace(self.Text) ||
                              string.IsNullOrWhiteSpace(address.Text);
            if (incomplete && MessageBox.Show(dialog,
                "Chưa đủ cặp 'tự xưng / gọi đối phương' của chiều đầu. " +
                "Chỉ lưu kết luận quan hệ đã xác nhận, CHƯA có quy tắc xưng hô đầy đủ " +
                "cho chiều này khi dịch truyện. Bạn vẫn muốn lưu quan hệ?",
                "Thiếu dữ liệu xưng hô", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            bool partialReverse = string.IsNullOrWhiteSpace(reverseSelf.Text) !=
                                  string.IsNullOrWhiteSpace(reverseAddress.Text);
            if (!incomplete && partialReverse && MessageBox.Show(dialog,
                "Chiều ngược chỉ có một cách xưng hô. Nếu tiếp tục, QT Studio sẽ " +
                "lưu chiều đầu nhưng chưa tạo quy tắc dịch chiều ngược. Tiếp tục?",
                "Xưng hô chiều ngược chưa đủ", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            item.Relationship = relation.Text.Trim();
            item.ConfirmedDirection = directional
                ? (RelationshipIntelligenceQualityGate.Direction)direction.SelectedIndex
                : RelationshipIntelligenceQualityGate.Direction.Unverified;
            item.SelfReference = self.Text.Trim();
            item.AddresseeReference = address.Text.Trim();
            item.ReverseSelfReference = reverseSelf.Text.Trim();
            item.ReverseAddresseeReference = reverseAddress.Text.Trim();
            item.ValidFromChapter = from;
            item.ValidToChapter = to;
            item.RevealedAtChapter = revealed;
            dialog.DialogResult = true;
        };
        return dialog.ShowDialog() == true;
    }

    private static bool TryChapter(string value, out int? chapter)
    {
        if (string.IsNullOrWhiteSpace(value)) { chapter = null; return true; }
        if (int.TryParse(value.Trim(), out int parsed) && parsed > 0) { chapter = parsed; return true; }
        chapter = null; return false;
    }

    private string AskText(string title, string chinese, string value)
    {
        var box = new TextBox { Text = value, Margin = new Thickness(10), MinWidth = 380 };
        var button = new Button { Content = "Xác nhận lưu", IsDefault = true, Margin = new Thickness(10) };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = chinese, FontSize = 18, Margin = new Thickness(10, 10, 10, 0) }); panel.Children.Add(box); panel.Children.Add(button);
        var dialog = new Window { Owner = this, Title = title, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        button.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true ? box.Text.Trim() : "";
    }

    private void SetBusy(bool busy) { _run.IsEnabled = !busy; _rerun.IsEnabled = !busy; _cancel.IsEnabled = busy; _profile.IsEnabled = !busy; _model.IsEnabled = !busy; }
    private void ShowFailure(Exception ex) { _summary.Text = "Lỗi: " + ex.Message + " Dữ liệu đã xác nhận và checkpoint trước đó vẫn được giữ."; }
    private string ShortChapter(Guid id) => _session.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == id) is { } c ? $"Chương {c.Number}" : id.ToString("D");
    private static string Preview(string text) { string one = text.Replace('\r', ' ').Replace('\n', ' ').Trim(); return one.Length <= 90 ? one : one[..90] + "…"; }

    private static string GlossaryScopeDisplay(GlossaryKnowledgeScope value) => value switch
    {
        GlossaryKnowledgeScope.Global => "Toàn truyện",
        GlossaryKnowledgeScope.ContextDependent => "Theo ngữ cảnh",
        GlossaryKnowledgeScope.CharacterName => "Tên nhân vật",
        GlossaryKnowledgeScope.AddressingKinship => "Xưng hô/họ hàng",
        _ => "Chưa rõ"
    };

    private static string RelationDisplay(KnowledgeRelationType type) => type switch
    {
        KnowledgeRelationType.UnknownSibling => "Anh/chị/em (chưa rõ vai vế)",
        KnowledgeRelationType.FullSibling => "Anh/chị/em cùng cha mẹ",
        KnowledgeRelationType.PaternalHalfSibling => "Anh/chị/em cùng cha khác mẹ",
        KnowledgeRelationType.MaternalHalfSibling => "Anh/chị/em cùng mẹ khác cha",
        KnowledgeRelationType.SwornSibling => "Anh/chị/em kết nghĩa",
        KnowledgeRelationType.Parent => "Cha/mẹ – con",
        KnowledgeRelationType.Child => "Con – cha/mẹ",
        KnowledgeRelationType.BiologicalFather => "Cha ruột",
        KnowledgeRelationType.BiologicalMother => "Mẹ ruột",
        KnowledgeRelationType.BiologicalChild => "Con ruột",
        KnowledgeRelationType.AdoptiveParent => "Cha/mẹ nuôi",
        KnowledgeRelationType.AdoptiveChild => "Con nuôi",
        KnowledgeRelationType.Grandfather => "Ông",
        KnowledgeRelationType.Grandmother => "Bà",
        KnowledgeRelationType.Grandchild => "Cháu",
        KnowledgeRelationType.Spouse => "Vợ chồng",
        KnowledgeRelationType.RomanticPartner => "Người yêu",
        KnowledgeRelationType.Teacher => "Thầy/cô",
        KnowledgeRelationType.Disciple => "Học trò",
        KnowledgeRelationType.Superior => "Cấp trên",
        KnowledgeRelationType.Subordinate => "Cấp dưới",
        KnowledgeRelationType.Social => "Quan hệ xã hội",
        _ => "Chưa xác định"
    };

    private sealed record ReviewRow(Guid Id, string Kind, string Title, string Confidence, string Status,
        string Origin, int EvidenceCount, object Value)
    {
        public string ConfidenceDisplay => Confidence switch
        {
            "Unknown" => "Chưa rõ", "Ambiguous" => "Mơ hồ", "Proposed" => "Đề xuất",
            "Valid" => "Hợp lệ", "Stale" => "Lỗi thời", "Conflict" => "Xung đột",
            _ => Confidence
        };
        public string StatusDisplay => Status switch
        {
            "Pending" => "Chờ duyệt", "ReviewLater" => "Xem xét sau",
            "Accepted" => "Đã duyệt", "Merged" => "Đã gộp",
            "Rejected" => "Đã loại", "Confirmed" => "Đã xác nhận",
            "DerivedAwaitingReview" => "Chờ duyệt suy luận",
            "Stale" => "Lỗi thời", "Conflict" => "Xung đột",
            _ => Status
        };
    }
    private sealed record PairClaimRow(IntelligenceRelationshipSuggestion Claim, string Label);
    private sealed record EvidenceRow(CorpusEvidence Evidence, string Label);
    private sealed record BlockedEvidenceRow(CorpusEvidence Evidence, string Label);
}
