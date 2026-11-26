using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Studio;

internal sealed record TextSegment(long Offset, string Text, long Chapter, bool Heading);

internal static partial class LongText
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    [GeneratedRegex(@"^(?:#{1,6}[ \t]+\S|第[零〇一二两三四五六七八九十百千万0-9]+[章节卷部回篇]|(?:chapter|part|book)[ \t]+[0-9IVXLCDM]+\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterHeading();

    public static IEnumerable<TextSegment> Read(string path, int budget, int target, CancellationToken ct)
    {
        using var reader = new StreamReader(path, Utf8, false);
        using var lookahead = File.OpenRead(path);
        var block = new char[4096];
        string window = "";
        int pos = 0;
        bool eof = false, lineStart = true;
        long offset = 0, chapter = 0, start = 0;
        var plainUntil = new Dictionary<char, long>();
        var buffer = new StringBuilder();
        int bytes = 0, elements = 0, boundary = 0, boundaryBytes = 0, boundaryElements = 0;
        bool heading = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!eof && window.Length - pos < block.Length)
            {
                int count = reader.ReadBlock(block);
                window = window[pos..] + new string(block, 0, count);
                pos = 0;
                eof = count < block.Length;
            }
            if (pos == window.Length) break;
            int headingStart = offset == 0 && window[pos] == '﻿' ? pos + 1 : pos;
            bool nextHeading = lineStart && ChapterHeading().IsMatch(window.AsSpan(headingStart, Math.Min(256, window.Length - headingStart)));
            if (nextHeading)
            {
                if (bytes > 0) yield return new(start, buffer.ToString(), chapter, heading);
                if (offset > 0) chapter++;
                start = offset;
                buffer.Clear();
                bytes = elements = boundary = 0;
                heading = true;
            }

            int length = StringInfo.GetNextTextElementLength(window.AsSpan(pos));
            char close = window[pos] switch { '[' => ']', '(' => ')', '（' => '）', '【' => '】', _ => '\0' };
            if (close != '\0' && (!plainUntil.TryGetValue(close, out long until) || offset >= until))
            {
                int closing = window.AsSpan(pos + length).IndexOfAny(close, '\r', '\n');
                if (closing >= 0)
                {
                    closing += pos + length;
                    if (window[closing] == close)
                    {
                        int tagLength = closing - pos + StringInfo.GetNextTextElementLength(window.AsSpan(closing));
                        length = tagLength;
                    }
                    else plainUntil[close] = offset + Utf8.GetByteCount(window.AsSpan(pos, closing - pos));
                }
                else if (!eof)
                {
                    long ahead = offset + Utf8.GetByteCount(window.AsSpan(pos, window.Length - pos - (char.IsHighSurrogate(window[^1]) ? 1 : 0)));
                    lookahead.Position = ahead;
                    char[] markers = [close, '\r', '\n'];
                    var scan = new byte[8192];
                    int n;
                    bool end = false;
                    while (!end && (n = lookahead.Read(scan)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        // UTF-8 ASCII markers are single bytes. The fullwidth closers
                        // need overlap so a marker crossing a read boundary is visible.
                        int found = scan.AsSpan(0, n).IndexOfAny((byte)'\r', (byte)'\n');
                        var marker = Utf8.GetBytes(markers.AsSpan(0, 1).ToString());
                        int closingByte = scan.AsSpan(0, n).IndexOf(marker);
                        if (closingByte >= 0 && (found < 0 || closingByte < found))
                            throw Oversize(offset, "句内标签");
                        if (found >= 0) { ahead += found; end = true; }
                        else
                        {
                            int advance = n == scan.Length ? n - (marker.Length - 1) : n;
                            ahead += advance;
                            lookahead.Position = ahead;
                        }
                    }
                    plainUntil[close] = ahead;
                }
            }
            // A window boundary can only bisect a grapheme or closing tag
            // after more UTF-16 code units than a single request can hold.
            if (length > budget) throw Oversize(offset, close == '\0' ? "Unicode 文本元素" : "句内标签或 Unicode 文本元素");
            var unit = window.AsSpan(pos, length);
            int size = Utf8.GetByteCount(unit);
            if (size > budget) throw Oversize(offset, close == '\0' ? "Unicode 文本元素" : "句内标签或 Unicode 文本元素");
            int countElements = length == 1 ? 1 : StringInfo.ParseCombiningCharacters(unit.ToString()).Length;
            if (bytes > 0 && (bytes + size > budget || elements + countElements > target))
            {
                // A natural boundary in the first half would produce very small WAV files.
                if (boundary > 0 && boundaryBytes * 2 >= bytes)
                {
                    yield return new(start, buffer.ToString(0, boundary), chapter, heading);
                    buffer.Remove(0, boundary);
                    start += boundaryBytes;
                    bytes -= boundaryBytes;
                    elements -= boundaryElements;
                }
                else
                {
                    yield return new(start, buffer.ToString(), chapter, heading);
                    buffer.Clear();
                    start = offset;
                    bytes = elements = 0;
                }
                heading = false;
                boundary = 0;
                continue;
            }
            buffer.Append(unit);
            bytes += size;
            elements += countElements;
            offset += size;
            pos += length;
            lineStart = unit[^1] is '\n' or '\r';
            if (lineStart || unit.Length == 1 && "。！？!?；;. ".Contains(unit[0]))
                (boundary, boundaryBytes, boundaryElements) = (buffer.Length, bytes, elements);
        }
        if (bytes > 0) yield return new(start, buffer.ToString(), chapter, heading);
    }

    private static InputError Oversize(long offset, string unit) => new($"正文 UTF-8 字节偏移 {offset} 处的{unit}超过单段字节预算。");
}
