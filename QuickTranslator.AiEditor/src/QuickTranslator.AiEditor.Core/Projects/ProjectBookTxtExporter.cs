using System.Text;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed record ProjectBookExportProgress(int Completed, int Total, string ChapterTitle);

public sealed record ProjectBookExportResult(
    int ExportedChapters, int IncompleteChapters, int NeedsInspectionChapters,
    int MissingSegments, int ErrorSegments, long FileBytes);

/// <summary>
/// Streams one book into one UTF-8 TXT without mutating any Project files.
/// The final destination is replaced only after every chapter has been read and written.
/// </summary>
public static class ProjectBookTxtExporter
{
    public static async Task<ProjectBookExportResult> ExportAsync(
        ProjectRepository repository, ProjectSession session, string destinationPath,
        bool allowIncomplete = false, IProgress<ProjectBookExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (session.RecoveryReport.RequiresDecision)
            throw new InvalidOperationException("Dự án còn transaction cần phục hồi; không xuất truyện trước khi xác nhận.");

        var chapters = session.Manifest.Chapters
            .Where(x => x.DeletedAt is null)
            .OrderBy(x => x.Number)
            .ThenBy(x => x.ChapterId)
            .Select(x => (x.ChapterId, x.Number, x.VietnameseTitle))
            .ToArray();
        if (chapters.Length == 0)
            throw new InvalidOperationException("Dự án chưa có chương nào để xuất.");

        string destination = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Thư mục lưu TXT không tồn tại: " + directory);
        string projectRoot = Path.GetFullPath(session.RootDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (destination.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(destination, session.ManifestPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Hãy xuất TXT ra ngoài thư mục Project để không ghi đè tệp chương, backup hoặc dữ liệu dự án.");

        // Temp file in the destination directory: no partial output if a chapter fails.
        string temporary = Path.Combine(directory, "." + Path.GetFileName(destination) +
            "." + Guid.NewGuid().ToString("N") + ".tmp");

        int incomplete = 0, needsInspection = 0, missingSegments = 0, errorSegments = 0;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(false, true), 65536, leaveOpen: true))
                {
                    await writer.WriteAsync(session.Manifest.VietnameseTitle.AsMemory(), cancellationToken);
                    for (int i = 0; i < chapters.Length; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = chapters[i];
                        var chapter = (await repository.LoadChapterAsync(session, entry.ChapterId, cancellationToken)
                            .ConfigureAwait(false)).Chapter;
                        var composed = ChapterTranslationComposer.Compose(chapter);
                        bool chapterIncomplete = chapter.Segments.Count == 0 || !composed.IsComplete;
                        if (chapterIncomplete && !allowIncomplete)
                            throw new InvalidOperationException(
                                $"Chương {entry.Number} còn thiếu bản dịch hoặc có lỗi. " +
                                "Hãy xác nhận xuất bản nháp nếu muốn tiếp tục.");

                        if (chapterIncomplete) incomplete++;
                        if (composed.InspectionIds.Count > 0) needsInspection++;
                        missingSegments += composed.MissingIds.Count + (chapter.Segments.Count == 0 ? 1 : 0);
                        errorSegments += composed.ErrorIds.Count;

                        string heading = FormatHeading(entry.Number, chapter.Title);
                        await writer.WriteAsync("\n\n".AsMemory(), cancellationToken);
                        await writer.WriteAsync(heading.AsMemory(), cancellationToken);
                        if (composed.Text.Length > 0)
                        {
                            await writer.WriteAsync("\n\n".AsMemory(), cancellationToken);
                            await writer.WriteAsync(composed.Text.AsMemory(), cancellationToken);
                        }
                        progress?.Report(new(i + 1, chapters.Length, heading));
                    }
                    await writer.WriteLineAsync();
                    await writer.FlushAsync(cancellationToken);
                }
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Atomic rename on the same filesystem; an earlier export stays intact on failure.
            File.Move(temporary, destination, overwrite: true);
            return new(chapters.Length, incomplete, needsInspection,
                missingSegments, errorSegments, new FileInfo(destination).Length);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string FormatHeading(int number, string? title)
    {
        string clean = (title ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length == 0) return $"Chương {number}";
        // Do not add a second number when a recognized title already includes one.
        if ((clean.StartsWith("Chương ", StringComparison.OrdinalIgnoreCase) ||
             clean.StartsWith("Chuong ", StringComparison.OrdinalIgnoreCase) ||
             clean.StartsWith("Chapter ", StringComparison.OrdinalIgnoreCase)) &&
            clean.Any(char.IsDigit))
            return clean;
        return $"Chương {number}: {clean}";
    }
}
