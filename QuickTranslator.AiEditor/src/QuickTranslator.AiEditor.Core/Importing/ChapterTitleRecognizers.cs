namespace QuickTranslator.AiEditor.Core.Importing;

internal sealed record HeadingRecognition(
    ChapterHeadingKind Kind,
    int? Number,
    string NumberText,
    string Title,
    string Marker,
    string Recognizer,
    bool HasExplicitSeparator,
    double BaseConfidence);

internal interface IChapterTitleRecognizer
{
    bool TryRecognize(string line, out HeadingRecognition recognition);
}

internal static class HeadingText
{
    private static readonly char[] Separators = [' ', '\t', ':', '：', '-', '—', '_', '.', '．', '、', '·', ']', '】', '〕', '）', ')'];

    public static string NormalizeOuter(string line)
    {
        string value = line.Trim();
        if (value.Length >= 2 && ((value[0] == '【' && value[^1] == '】') || (value[0] == '[' && value[^1] == ']') ||
            (value[0] == '〖' && value[^1] == '〗'))) value = value[1..^1].Trim();
        else if (value.Length >= 3 && value[0] is '【' or '[' or '〖')
        {
            char close = value[0] switch { '【' => '】', '[' => ']', _ => '〗' };
            int closeIndex = value.IndexOf(close, 1);
            if (closeIndex > 1) value = (value[1..closeIndex] + value[(closeIndex + 1)..]).Trim();
        }
        if (value.StartsWith("正文", StringComparison.Ordinal) && value.Length > 2 && IsSeparator(value[2]))
            value = value[3..].TrimStart();
        return value;
    }

    public static (string Title, bool HadSeparator) ReadTitle(string rest)
    {
        bool separated = rest.Length == 0 || IsSeparator(rest[0]);
        return (rest.TrimStart(Separators).TrimEnd(), separated);
    }

    public static bool IsSeparator(char value) => Separators.Contains(value);
}

internal sealed class ChineseOrdinalHeadingRecognizer : IChapterTitleRecognizer
{
    public bool TryRecognize(string line, out HeadingRecognition recognition)
    {
        recognition = null!;
        string text = HeadingText.NormalizeOuter(line);
        if (text.Length < 2) return false;

        if (text[0] is '卷' or '部' or '篇' or '集')
        {
            int numberStart = 1;
            while (numberStart < text.Length && char.IsWhiteSpace(text[numberStart])) numberStart++;
            int end = numberStart;
            while (end < text.Length && (ChineseNumberParser.IsNumberCharacter(text[end]) || char.IsWhiteSpace(text[end]))) end++;
            string numberText = text[numberStart..end].TrimEnd();
            if (numberText.Length == 0 || !ChineseNumberParser.TryParse(numberText, out int suffixNumber)) return false;
            var suffixTitle = HeadingText.ReadTitle(text[end..]);
            recognition = new(ChapterHeadingKind.Volume, suffixNumber, numberText, suffixTitle.Title,
                text[..1], nameof(ChineseOrdinalHeadingRecognizer), suffixTitle.HadSeparator, 0.76);
            return true;
        }

        if (text[0] != '第') return false;
        int markerIndex = 1;
        while (markerIndex < text.Length && char.IsWhiteSpace(text[markerIndex])) markerIndex++;
        int numberStartIndex = markerIndex;
        while (markerIndex < text.Length && (ChineseNumberParser.IsNumberCharacter(text[markerIndex]) || char.IsWhiteSpace(text[markerIndex]))) markerIndex++;
        string ordinalText = text[numberStartIndex..markerIndex].TrimEnd();
        while (markerIndex < text.Length && char.IsWhiteSpace(text[markerIndex])) markerIndex++;
        if (ordinalText.Length == 0 || markerIndex >= text.Length || !ChineseNumberParser.TryParse(ordinalText, out int number)) return false;
        char marker = text[markerIndex];

        // In Chinese prose, 第N节课 / 第N節課 means "the N-th school lesson", NOT
        // a section heading. Treating it as a chapter boundary fragments genuine
        // chapters and creates spurious duplicate chapter numbers (2, 3, ...).
        // A real 第N节 heading remains supported when followed by whitespace,
        // punctuation or another standalone title.
        if ((marker is '节' or '節') && markerIndex + 1 < text.Length &&
            (text[markerIndex + 1] is '课' or '課'))
            return false;

        ChapterHeadingKind kind;
        if (marker is '章' or '回' or '节' or '節') kind = ChapterHeadingKind.Chapter;
        else if (marker is '卷' or '部' or '篇' or '集') kind = ChapterHeadingKind.Volume;
        else return false;
        var title = HeadingText.ReadTitle(text[(markerIndex + 1)..]);
        recognition = new(kind, number, ordinalText, title.Title, marker.ToString(),
            nameof(ChineseOrdinalHeadingRecognizer), title.HadSeparator, kind == ChapterHeadingKind.Volume ? 0.76 : 0.74);
        return true;
    }
}

internal sealed class LatinNumberedHeadingRecognizer : IChapterTitleRecognizer
{
    private static readonly string[] Prefixes = ["Chapter", "Chương", "Chuong"];

    public bool TryRecognize(string line, out HeadingRecognition recognition)
    {
        recognition = null!;
        string text = HeadingText.NormalizeOuter(line);
        foreach (string prefix in Prefixes)
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            int index = prefix.Length;
            if (index < text.Length && !char.IsWhiteSpace(text[index]) && !ChineseNumberParser.IsNumberCharacter(text[index])) continue;
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            int numberStart = index;
            while (index < text.Length && text[index] is >= '0' and <= '9' or >= '０' and <= '９') index++;
            if (numberStart == index || !ChineseNumberParser.TryParse(text[numberStart..index], out int number)) continue;
            var title = HeadingText.ReadTitle(text[index..]);
            recognition = new(ChapterHeadingKind.Chapter, number, text[numberStart..index], title.Title, prefix,
                nameof(LatinNumberedHeadingRecognizer), title.HadSeparator, 0.73);
            return true;
        }
        return false;
    }
}

internal sealed class SpecialChapterHeadingRecognizer : IChapterTitleRecognizer
{
    private static readonly string[] Markers = ["番外篇", "大结局", "大結局", "序章", "楔子", "引子", "前言", "番外", "终章", "終章", "尾声", "尾聲", "后记", "後記"];

    public bool TryRecognize(string line, out HeadingRecognition recognition)
    {
        recognition = null!;
        string text = HeadingText.NormalizeOuter(line);
        foreach (string marker in Markers)
        {
            if (!text.StartsWith(marker, StringComparison.Ordinal)) continue;
            string rest = text[marker.Length..];
            int numberLength = 0;
            if (marker.StartsWith("番外", StringComparison.Ordinal))
                while (numberLength < rest.Length && ChineseNumberParser.IsNumberCharacter(rest[numberLength])) numberLength++;
            int? number = null;
            string numberText = "";
            if (numberLength > 0 && ChineseNumberParser.TryParse(rest[..numberLength], out int parsed))
            {
                number = parsed; numberText = rest[..numberLength]; rest = rest[numberLength..];
            }
            var title = HeadingText.ReadTitle(rest);
            recognition = new(ChapterHeadingKind.SpecialChapter, number, numberText, title.Title, marker,
                nameof(SpecialChapterHeadingRecognizer), title.HadSeparator, 0.68);
            return true;
        }
        return false;
    }
}
