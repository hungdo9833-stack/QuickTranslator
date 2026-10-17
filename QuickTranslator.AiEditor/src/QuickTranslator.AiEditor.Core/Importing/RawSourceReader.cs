using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace QuickTranslator.AiEditor.Core.Importing;

public static class RawSourceReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static RawSourceReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static RawSourceSnapshot FromText(string text, string sourceName = "clipboard")
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes;
        try { bytes = StrictUtf8.GetBytes(text); }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("Nguồn chứa UTF-16 không hợp lệ; không thể giữ nguyên khi lưu UTF-8.", nameof(text), ex);
        }
        return Create(sourceName, text, bytes, "UTF-8");
    }

    public static RawSourceSnapshot Decode(ReadOnlySpan<byte> bytes, SourceTextEncoding encoding = SourceTextEncoding.Auto,
        string sourceName = "source.txt")
    {
        byte[] original = bytes.ToArray();
        bool hasUtf8Bom = original.Length >= 3 && original[0] == 0xef && original[1] == 0xbb && original[2] == 0xbf;
        bool hasUtf16LeBom = original.Length >= 2 && original[0] == 0xff && original[1] == 0xfe;
        bool hasUtf16BeBom = original.Length >= 2 && original[0] == 0xfe && original[1] == 0xff;
        if (encoding == SourceTextEncoding.Auto)
            return DecodeAutomatically(original, sourceName, hasUtf8Bom, hasUtf16LeBom, hasUtf16BeBom);
        Encoding decoder;
        int preambleLength = 0;
        string encodingName;

        switch (encoding)
        {
            case SourceTextEncoding.Utf8:
                decoder = StrictUtf8;
                if (hasUtf8Bom) { preambleLength = 3; encodingName = "UTF-8 BOM"; }
                else encodingName = "UTF-8";
                break;
            case SourceTextEncoding.Gbk:
                decoder = StrictCodePage(936);
                encodingName = "GBK (CP936)";
                break;
            case SourceTextEncoding.Gb18030:
                decoder = StrictCodePage(54936);
                encodingName = "GB18030";
                break;
            case SourceTextEncoding.Utf16Le:
                decoder = new UnicodeEncoding(false, false, true);
                preambleLength = hasUtf16LeBom ? 2 : 0;
                encodingName = "UTF-16 LE";
                break;
            case SourceTextEncoding.Utf16Be:
                decoder = new UnicodeEncoding(true, false, true);
                preambleLength = hasUtf16BeBom ? 2 : 0;
                encodingName = "UTF-16 BE";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(encoding));
        }

        try
        {
            string text = decoder.GetString(original, preambleLength, original.Length - preambleLength);
            return Create(sourceName, text, original, encodingName);
        }
        catch (DecoderFallbackException ex)
        {
            throw new SourceDecodingException($"Dữ liệu không hợp lệ theo bảng mã {encodingName}; không có ký tự thay thế nào được chèn.", ex);
        }
    }

    private static RawSourceSnapshot DecodeAutomatically(byte[] original, string sourceName, bool hasUtf8Bom,
        bool hasUtf16LeBom, bool hasUtf16BeBom)
    {
        if (hasUtf16LeBom || hasUtf16BeBom)
        {
            Encoding decoder = new UnicodeEncoding(hasUtf16BeBom, false, true);
            try
            {
                string utf16 = decoder.GetString(original, 2, original.Length - 2);
                return Create(sourceName, utf16, original, hasUtf16BeBom ? "UTF-16 BE BOM" : "UTF-16 LE BOM",
                    autoDetected: true, uncertain: false,
                    alternatives: [hasUtf16BeBom ? "UTF-16 BE" : "UTF-16 LE"]);
            }
            catch (DecoderFallbackException ex)
            {
                throw new SourceDecodingException("Dữ liệu có BOM UTF-16 nhưng chứa chuỗi mã không hợp lệ.", ex);
            }
        }
        try
        {
            int preamble = hasUtf8Bom ? 3 : 0;
            string utf8 = StrictUtf8.GetString(original, preamble, original.Length - preamble);
            return Create(sourceName, utf8, original, hasUtf8Bom ? "UTF-8 BOM" : "UTF-8",
                autoDetected: true, uncertain: LooksSuspicious(utf8), alternatives: ["UTF-8", "GB18030"]);
        }
        catch (DecoderFallbackException)
        {
            // Continue with strict Chinese code pages. GB18030 is a superset of GBK,
            // but both are evaluated so the preview can disclose ambiguity instead of
            // silently inserting replacement characters.
        }

        var candidates = new List<(string Name, string Text, double Score)>();
        TryDecodeCandidate(original, 54936, "GB18030", candidates);
        TryDecodeCandidate(original, 936, "GBK (CP936)", candidates);
        if (candidates.Count == 0)
            throw new SourceDecodingException("Không thể giải mã tệp bằng UTF-8, GBK hoặc GB18030 mà không làm mất ký tự. Hãy kiểm tra lại tệp nguồn.");

        (string Name, string Text, double Score) best = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .First();
        string[] distinctAlternatives = candidates.Select(candidate => candidate.Name).Distinct(StringComparer.Ordinal).ToArray();
        bool sameText = candidates.All(candidate => string.Equals(candidate.Text, best.Text, StringComparison.Ordinal));
        double secondScore = candidates.Where(candidate => !string.Equals(candidate.Name, best.Name, StringComparison.Ordinal))
            .Select(candidate => candidate.Score).DefaultIfEmpty(double.NegativeInfinity).Max();
        bool uncertain = LooksSuspicious(best.Text) || (!sameText && best.Score - secondScore < 8d);
        string displayName = sameText && distinctAlternatives.Length > 1
            ? "GB18030 / GBK (tự nhận diện)"
            : best.Name + " (tự nhận diện)";
        return Create(sourceName, best.Text, original, displayName, autoDetected: true, uncertain,
            distinctAlternatives.ToImmutableArray());
    }

    private static void TryDecodeCandidate(byte[] bytes, int codePage, string name,
        ICollection<(string Name, string Text, double Score)> candidates)
    {
        try
        {
            string text = StrictCodePage(codePage).GetString(bytes);
            candidates.Add((name, text, PlausibilityScore(text)));
        }
        catch (DecoderFallbackException) { }
    }

    private static double PlausibilityScore(string text)
    {
        if (text.Length == 0) return 0;
        int cjk = 0, printable = 0, controls = 0, suspicious = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            if (value is >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x2fa1f) cjk++;
            if (value is '\r' or '\n' or '\t' || !Rune.IsControl(rune)) printable++; else controls++;
            if (value is 0xfffd or 0x0000) suspicious++;
        }
        return cjk * 4d + printable - controls * 20d - suspicious * 100d;
    }

    private static bool LooksSuspicious(string text)
    {
        if (text.IndexOf('\uFFFD') >= 0 || text.IndexOf('\0') >= 0) return true;
        int badControls = text.Count(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t');
        return badControls > Math.Max(2, text.Length / 500);
    }

    public static async Task<RawSourceSnapshot> ReadFileAsync(string path, SourceTextEncoding encoding = SourceTextEncoding.Auto,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return Decode(bytes, encoding, Path.GetFileName(fullPath));
    }

    private static Encoding StrictCodePage(int codePage)
    {
        try { return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
        catch (ArgumentException ex)
        {
            throw new SourceDecodingException($"Runtime không hỗ trợ code page {codePage}; không thể giải mã an toàn.", ex);
        }
    }

    private static RawSourceSnapshot Create(string sourceName, string text, byte[] originalBytes, string encodingName,
        bool autoDetected = false, bool uncertain = false, ImmutableArray<string> alternatives = default)
    {
        string name = string.IsNullOrWhiteSpace(sourceName) ? "source.txt" : sourceName;
        byte[] textBytes;
        try { textBytes = StrictUtf8.GetBytes(text); }
        catch (EncoderFallbackException ex)
        {
            throw new SourceDecodingException("Văn bản giải mã thành UTF-16 không hợp lệ.", ex);
        }
        return new(name, text, originalBytes.ToImmutableArray(), encodingName,
            Convert.ToHexString(SHA256.HashData(originalBytes)), Convert.ToHexString(SHA256.HashData(textBytes)),
            DetectNewLines(text), autoDetected, uncertain,
            alternatives.IsDefault ? ImmutableArray<string>.Empty : alternatives);
    }

    private static SourceNewLineStyle DetectNewLines(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') { crlf++; index++; }
                else cr++;
            }
            else if (text[index] == '\n') lf++;
        }
        int kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        if (kinds == 0) return SourceNewLineStyle.None;
        if (kinds > 1) return SourceNewLineStyle.Mixed;
        if (crlf > 0) return SourceNewLineStyle.CrLf;
        return lf > 0 ? SourceNewLineStyle.Lf : SourceNewLineStyle.Cr;
    }
}

public sealed class SourceDecodingException : IOException
{
    public SourceDecodingException(string message, Exception? innerException = null) : base(message, innerException) { }
}
