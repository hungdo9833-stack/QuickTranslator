using System.IO;
using System.Windows;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private async void OnExportWholeProject(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null)
        {
            MessageBox.Show(this, "Hãy mở một Project trước khi xuất toàn bộ truyện.",
                "Xuất truyện", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!BeginUiOperation()) return;
        try
        {
            if (HasPendingSourceEdit)
                throw new InvalidOperationException("Hãy lưu hoặc hủy sửa nguyên văn trước khi xuất truyện.");
            await SaveCurrentAsync();

            ProjectSession session = _projectSession;
            var active = session.Manifest.Chapters.Where(x => x.DeletedAt is null).ToArray();
            if (active.Length == 0)
                throw new InvalidOperationException("Dự án chưa có chương nào.");

            // Manifest summary is only a preflight indicator: the exporter validates
            // every composed chapter and must never silently suppress corrupt files.
            int suspect = active.Count(x => x.CompletedSegmentCount < x.SegmentCount ||
                x.SegmentCount == 0 || x.State is ProjectChapterState.Error or ProjectChapterState.Untranslated);
            bool allowIncomplete = false;
            if (suspect > 0)
            {
                var answer = MessageBox.Show(this,
                    $"Có {suspect} chương có thể chưa hoàn tất theo mục lục.\n" +
                    "Có: xuất bản nháp với các phần dịch hiện có; Không: chỉ xuất nếu tất cả chương hoàn chỉnh; Hủy: dừng.",
                    "Xuất toàn bộ truyện", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Cancel) return;
                allowIncomplete = answer == MessageBoxResult.Yes;
            }

            string fileName = string.Concat(session.Manifest.VietnameseTitle.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var dialog = new SaveFileDialog
            {
                Title = "Xuất toàn bộ truyện thành một file TXT",
                Filter = "Văn bản UTF-8 (*.txt)|*.txt",
                FileName = fileName + ".txt",
                DefaultExt = ".txt",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;
            SetStatus($"Đang xuất {active.Length} chương thành TXT…");
            var progress = new Progress<ProjectBookExportProgress>(p =>
                SetStatus($"Xuất TXT: {p.Completed}/{p.Total} — {p.ChapterTitle}"));
            var result = await ProjectBookTxtExporter.ExportAsync(_projectRepository, session,
                dialog.FileName, allowIncomplete, progress);
            SetStatus($"Đã xuất {result.ExportedChapters} chương thành TXT ({result.FileBytes:N0} byte). " +
                $"Chưa hoàn tất: {result.IncompleteChapters}; cần kiểm tra: {result.NeedsInspectionChapters}. " +
                "Project gốc không thay đổi.");
            MessageBox.Show(this,
                $"Đã xuất {result.ExportedChapters} chương.\n" +
                $"Chương chưa hoàn tất: {result.IncompleteChapters}\n" +
                $"Chương cần kiểm tra: {result.NeedsInspectionChapters}\n\n{dialog.FileName}",
                "Xuất TXT hoàn tất", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }
}
