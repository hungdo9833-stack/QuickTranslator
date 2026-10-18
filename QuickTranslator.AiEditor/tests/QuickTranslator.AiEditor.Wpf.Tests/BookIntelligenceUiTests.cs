using System.Windows.Controls;
using System.Windows;
using System.Reflection;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class BookIntelligenceUiTests
{
    [Fact]
    public Task Character_panel_exposes_explicit_ai_survey_action_without_starting_work() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var button = Assert.IsType<Button>(fixture.Window.FindName("SurveyBookButton"));
        Assert.Equal("Khảo sát bằng AI", button.Content);
        Assert.True(button.IsEnabled);
        Assert.Null(fixture.Get<object>("_bookIntelligenceWindow"));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Review_window_opens_modeless_with_filters_multiselect_and_feature_toggle() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        await fixture.TaskCall("EnsureOllamaBackendsLoadedAsync");
        var button = Assert.IsType<Button>(fixture.Window.FindName("SurveyBookButton"));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(80);
        var window = Assert.IsType<BookIntelligenceReviewWindow>(fixture.Get<object>("_bookIntelligenceWindow"));
        Assert.True(window.IsVisible);
        Assert.Equal(DataGridSelectionMode.Extended, Field<DataGrid>(window, "_queue").SelectionMode);
        var viewToggle = Field<Button>(window, "_viewToggle");
        Assert.Equal("Xem đề xuất thô", viewToggle.Content);
        Assert.True(Field<bool>(window, "_dossierMode"));
        viewToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Xem hồ sơ nhân vật", viewToggle.Content);
        Assert.False(Field<bool>(window, "_dossierMode"));
        viewToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(Field<bool>(window, "_dossierMode"));
        Assert.NotNull(Field<TextBox>(window, "_search"));
        Assert.Equal("Khảo sát lại", Field<Button>(window, "_rerun").Content);
        Assert.Equal("Bắt đầu / Tiếp tục", Field<Button>(window, "_run").Content);
        Assert.Equal("Đoạn bị chặn", Field<Button>(window, "_blocked").Content);
        Assert.Equal("Hồ sơ theo cặp", Field<Button>(window, "_pairs").Content);
        Assert.False(Field<Button>(window, "_blocked").IsEnabled);
        Assert.NotNull(Field<ComboBox>(window, "_kindFilter"));
        Assert.Contains("Knowledge Context", Field<CheckBox>(window, "_translationContext").Content?.ToString());
        window.Close();

        static T Field<T>(BookIntelligenceReviewWindow window, string name) where T : class =>
            (T)typeof(BookIntelligenceReviewWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    });

    [Fact]
    public Task Review_window_exposes_Gemini_Flash_Pool_with_safe_configuration_status() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        await fixture.TaskCall("EnsureOllamaBackendsLoadedAsync");
        ((Button)fixture.Window.FindName("SurveyBookButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(80);
        var window = Assert.IsType<BookIntelligenceReviewWindow>(fixture.Get<object>("_bookIntelligenceWindow"));
        var profile = Field<ComboBox>(window, "_profile");
        object gemini = Assert.Single(profile.Items.Cast<object>(), item =>
            item is QuickTranslator.AiEditor.Core.Translation.OllamaBackendProfile backend &&
            backend.Id == BookIntelligenceReviewWindow.GeminiFlashPoolProfileId);

        profile.SelectedItem = gemini;
        await System.Windows.Threading.Dispatcher.Yield();

        var modelDropdown = Field<ComboBox>(window, "_model");
        Assert.False(modelDropdown.IsEditable);
        Assert.Equal("gemini-3.8-flash", modelDropdown.SelectedItem);
        Assert.Contains("gemini-3.6-flash", modelDropdown.Items.Cast<string>());
        Assert.Contains("gemini-3.5-flash-lite", modelDropdown.Items.Cast<string>());
        Assert.Contains("gemini-3.1-flash-lite", modelDropdown.Items.Cast<string>());
        string status = Field<TextBlock>(window, "_summary").Text;
        Assert.Contains("gemini-3.8-flash", status);
        Assert.Contains("API key:", status);
        Assert.Contains("ngân sách:", status);
        Assert.DoesNotContain(Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_API_KEY") ?? "__missing__", status);
        window.Close();

        static T Field<T>(BookIntelligenceReviewWindow window, string name) where T : class =>
            (T)typeof(BookIntelligenceReviewWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    });
}
