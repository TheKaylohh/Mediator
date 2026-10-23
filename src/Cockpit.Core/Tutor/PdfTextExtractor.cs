using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Cockpit.Core.Tutor;

/// <summary>
/// A small, dependency-free PDF text reader: the fallback when PyMuPDF is not installed. It handles what most
/// textbook PDFs use — classic and compressed object streams (ObjStm), FlateDecode content, simple and Type0 fonts
/// with ToUnicode maps, literal/hex strings, TJ kerning (wide gaps become spaces) and line moves. It does not do
/// layout analysis, encrypted files, Form XObjects or fonts without a ToUnicode map beyond Latin-1; equations come
/// out as their glyph text. PyMuPDF is preferred whenever it is available.
/// </summary>
public static partial class PdfTextExtractor
{
    public sealed record PdfPageText(int Number, string Text);
    public sealed record PdfDocumentText(int PageCount, IReadOnlyList<PdfPageText> Pages, string? Title);

    private sealed record PdfRef(int Num, int Gen);
    private sealed record PdfName(string Value) { public override string ToString() => Value; }
    private sealed record PdfStream(Dictionary<string, object?> Dict, byte[] Raw);
    private sealed record PdfOperator(string Op);

    /// <summary>Largest PDF the built-in reader accepts.</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    public static PdfDocumentText Extract(string path, int firstPage, int lastPage)
    {
        using var fs = File.OpenRead(path);
        return Extract(ReadBounded(fs), firstPage, lastPage);
    }

    /// <summary>Reads a stream fully, refusing anything over <see cref="MaxFileBytes"/> before allocating it.</summary>
    public static byte[] ReadBounded(Stream stream)
    {
        if (stream.CanSeek && stream.Length > MaxFileBytes) throw new FormatException($"The PDF is larger than {MaxFileBytes / (1024 * 1024)} MiB.");
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + n > MaxFileBytes) throw new FormatException($"The PDF is larger than {MaxFileBytes / (1024 * 1024)} MiB.");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    public static PdfDocumentText Extract(byte[] data, int firstPage, int lastPage)
    {
        if (data.Length < 8 || Encoding.ASCII.GetString(data, 0, 5) != "%PDF-") throw new FormatException("Not a PDF file.");
        var objects = ReadObjects(data);
        if (objects.Values.OfType<Dictionary<string, object?>>().Any(d => d.ContainsKey("Filter") && Name(d, "Filter") == "Standard")
            || Encoding.ASCII.GetString(data).Contains("/Encrypt", StringComparison.Ordinal))
            throw new FormatException("The PDF is encrypted; install PyMuPDF (pip install pymupdf) to read it.");

        object? Resolve(object? o) { for (int i = 0; i < 32 && o is PdfRef r; i++) o = objects.GetValueOrDefault(r.Num); return o; }
        Dictionary<string, object?>? Dict(object? o) => Resolve(o) switch { Dictionary<string, object?> d => d, PdfStream s => s.Dict, _ => null };

        var catalog = objects.Values.Select(Dict).FirstOrDefault(d => d is not null && Name(d, "Type") == "Catalog")
            ?? throw new FormatException("No document catalog found.");
        var pages = new List<(Dictionary<string, object?> Page, Dictionary<string, object?>? Resources)>();
        void Walk(Dictionary<string, object?>? node, Dictionary<string, object?>? inherited, int depth)
        {
            if (node is null || depth > 64) return;
            var resources = Dict(node.GetValueOrDefault("Resources")) ?? inherited;
            if (Name(node, "Type") == "Page" || (!node.ContainsKey("Kids") && node.ContainsKey("Contents"))) { pages.Add((node, resources)); return; }
            if (Resolve(node.GetValueOrDefault("Kids")) is List<object?> kids)
                foreach (var kid in kids) Walk(Dict(kid), resources, depth + 1);
        }
        Walk(Dict(catalog.GetValueOrDefault("Pages")), null, 0);
        if (pages.Count == 0) throw new FormatException("No pages found.");

        string? title = null;
        var info = objects.Values.Select(Dict).FirstOrDefault(d => d is not null && d.ContainsKey("Producer") || d is not null && d.ContainsKey("Title"));
        if (info?.GetValueOrDefault("Title") is byte[] tb) title = DecodeTextString(tb);

        int from = Math.Max(1, firstPage), to = Math.Min(pages.Count, lastPage);
        var result = new List<PdfPageText>();
        for (int n = from; n <= to; n++)
        {
            var (page, resources) = pages[n - 1];
            var fonts = new Dictionary<string, Font>(StringComparer.Ordinal);
            if (Dict(resources?.GetValueOrDefault("Font")) is { } fontDict)
                foreach (var (fname, fref) in fontDict)
                    if (Dict(fref) is { } f) fonts[fname] = Font.From(f, o => Resolve(o));
            var content = new MemoryStream();
            var contents = Resolve(page.GetValueOrDefault("Contents"));
            foreach (var part in contents is List<object?> list ? list.Select(Resolve) : [contents])
                if (part is PdfStream s && Decode(s) is { } bytes) { content.Write(bytes); content.WriteByte((byte)'\n'); }
            result.Add(new PdfPageText(n, Clean(Interpret(content.ToArray(), fonts))));
        }
        return new PdfDocumentText(pages.Count, result, title);
    }

    // ── Objects ───────────────────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"(?<![0-9])(\d+)\s+(\d+)\s+obj\b")]
    private static partial Regex ObjHeader();

    private static Dictionary<int, object?> ReadObjects(byte[] data)
    {
        var objects = new Dictionary<int, object?>();
        string latin = Encoding.Latin1.GetString(data);
        foreach (Match m in ObjHeader().Matches(latin))
        {
            int num = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var lexer = new Lexer(data, m.Index + m.Length);
            try { objects[num] = lexer.ReadObjectBody(objects); } // later definitions (incremental updates) win
            catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
        }
        // Compressed object streams (PDF 1.5+): /Type /ObjStm /N count /First offset.
        foreach (var stream in objects.Values.OfType<PdfStream>().Where(s => Name(s.Dict, "Type") == "ObjStm").ToList())
        {
            var decoded = Decode(stream);
            if (decoded is null) continue;
            int count = (int)Num(stream.Dict.GetValueOrDefault("N")), first = (int)Num(stream.Dict.GetValueOrDefault("First"));
            var header = new Lexer(decoded, 0);
            var pairs = new List<(int Num, int Offset)>();
            for (int i = 0; i < count; i++)
                if (header.Next() is double on && header.Next() is double off) pairs.Add(((int)on, (int)off));
            foreach (var (on, off) in pairs)
            {
                if (objects.ContainsKey(on) && objects[on] is not null) continue;
                try { objects[on] = new Lexer(decoded, first + off).ReadValue(); }
                catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
            }
        }
        return objects;
    }

    private static string? Name(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key) is PdfName n ? n.Value : null;
    private static double Num(object? o) => o is double d ? d : 0;

    private static byte[]? Decode(PdfStream s)
    {
        var filter = s.Dict.GetValueOrDefault("Filter");
        var filters = filter switch { PdfName n => [n.Value], List<object?> l => l.OfType<PdfName>().Select(x => x.Value).ToList(), _ => new List<string>() };
        byte[] bytes = s.Raw;
        foreach (var f in filters)
        {
            if (f is not ("FlateDecode" or "Fl")) return null; // DCT, JBIG2, LZW…: not text content
            try
            {
                using var z = new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress);
                var outStream = new MemoryStream();
                try { z.CopyTo(outStream); } catch (InvalidDataException) when (outStream.Length > 0) { } // truncated trailing bytes
                bytes = outStream.ToArray();
            }
            catch (InvalidDataException) { return null; }
        }
        return bytes;
    }

    private sealed class Lexer(byte[] data, int position)
    {
        private int _pos = position;
        private object? _peeked;
        private bool _hasPeek;

        private static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
        private static bool IsDelim(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        public bool AtEnd { get { SkipWhite(); return _pos >= data.Length; } }

        private void SkipWhite()
        {
            while (_pos < data.Length)
            {
                if (IsWhite(data[_pos])) _pos++;
                else if (data[_pos] == '%') { while (_pos < data.Length && data[_pos] != '\n' && data[_pos] != '\r') _pos++; }
                else break;
            }
        }

        /// <summary>Next token: double, PdfName, byte[] (string), "[" "]" "<<" ">>" as PdfOperator, or another keyword as PdfOperator.</summary>
        public object? Next()
        {
            if (_hasPeek) { _hasPeek = false; return _peeked; }
            SkipWhite();
            if (_pos >= data.Length) return null;
            byte b = data[_pos];
            if (b == '/')
            {
                int start = ++_pos;
                while (_pos < data.Length && !IsWhite(data[_pos]) && !IsDelim(data[_pos])) _pos++;
                string raw = Encoding.Latin1.GetString(data, start, _pos - start);
                return new PdfName(Regex.Replace(raw, "#([0-9A-Fa-f]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()));
            }
            if (b == '(') return LiteralString();
            if (b == '<')
            {
                if (_pos + 1 < data.Length && data[_pos + 1] == '<') { _pos += 2; return new PdfOperator("<<"); }
                return HexString();
            }
            if (b == '>' && _pos + 1 < data.Length && data[_pos + 1] == '>') { _pos += 2; return new PdfOperator(">>"); }
            if (b is (byte)'[' or (byte)']' or (byte)'{' or (byte)'}') { _pos++; return new PdfOperator(((char)b).ToString()); }
            int s0 = _pos;
            while (_pos < data.Length && !IsWhite(data[_pos]) && !IsDelim(data[_pos])) _pos++;
            if (_pos == s0) { _pos++; return new PdfOperator(((char)b).ToString()); }
            string word = Encoding.Latin1.GetString(data, s0, _pos - s0);
            return double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : new PdfOperator(word);
        }

        private object? Peek()
        {
            if (!_hasPeek) { _peeked = Next(); _hasPeek = true; }
            return _peeked;
        }

        private byte[] LiteralString()
        {
            _pos++;
            var bytes = new List<byte>();
            int depth = 1;
            while (_pos < data.Length)
            {
                byte b = data[_pos++];
                if (b == '\\' && _pos < data.Length)
                {
                    byte e = data[_pos++];
                    switch (e)
                    {
                        case (byte)'n': bytes.Add(10); break;
                        case (byte)'r': bytes.Add(13); break;
                        case (byte)'t': bytes.Add(9); break;
                        case (byte)'b': bytes.Add(8); break;
                        case (byte)'f': bytes.Add(12); break;
                        case (byte)'\r': if (_pos < data.Length && data[_pos] == '\n') _pos++; break; // line continuation
                        case (byte)'\n': break;
                        case >= (byte)'0' and <= (byte)'7':
                            int v = e - '0';
                            for (int k = 0; k < 2 && _pos < data.Length && data[_pos] is >= (byte)'0' and <= (byte)'7'; k++) v = v * 8 + (data[_pos++] - '0');
                            bytes.Add((byte)v);
                            break;
                        default: bytes.Add(e); break;
                    }
                }
                else if (b == '(') { depth++; bytes.Add(b); }
                else if (b == ')') { if (--depth == 0) break; bytes.Add(b); }
                else bytes.Add(b);
            }
            return bytes.ToArray();
        }

        private byte[] HexString()
        {
            _pos++;
            var hex = new StringBuilder();
            while (_pos < data.Length && data[_pos] != '>') { if (Uri.IsHexDigit((char)data[_pos])) hex.Append((char)data[_pos]); _pos++; }
            _pos++;
            if (hex.Length % 2 == 1) hex.Append('0');
            return Convert.FromHexString(hex.ToString());
        }

        /// <summary>A full value: arrays, dictionaries and "n g R" references assembled.</summary>
        public object? ReadValue()
        {
            var t = Next();
            switch (t)
            {
                case PdfOperator { Op: "[" }:
                    var list = new List<object?>();
                    while (Peek() is not PdfOperator { Op: "]" } && Peek() is not null) list.Add(ReadValue());
                    Next();
                    return list;
                case PdfOperator { Op: "<<" }:
                    var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    while (Peek() is PdfName key) { Next(); dict[key.Value] = ReadValue(); }
                    if (Next() is not PdfOperator { Op: ">>" }) throw new FormatException("bad dictionary");
                    return dict;
                case double d:
                    // "n g R": look ahead without consuming unless it is a reference
                    int save = _pos;
                    bool hadPeek = _hasPeek;
                    object? savedPeek = _peeked;
                    if (!_hasPeek && Next() is double g && Next() is PdfOperator { Op: "R" }) return new PdfRef((int)d, (int)g);
                    _pos = save; _hasPeek = hadPeek; _peeked = savedPeek;
                    return d;
                case PdfOperator { Op: "true" }: return true;
                case PdfOperator { Op: "false" }: return false;
                case PdfOperator { Op: "null" }: return null;
                default: return t;
            }
        }

        public object? ReadObjectBody(Dictionary<int, object?> known)
        {
            var value = ReadValue();
            if (value is Dictionary<string, object?> dict && Peek() is PdfOperator { Op: "stream" })
            {
                Next();
                _hasPeek = false;
                // stream data starts after the EOL that follows the keyword
                if (_pos < data.Length && data[_pos] == '\r') _pos++;
                if (_pos < data.Length && data[_pos] == '\n') _pos++;
                int length = dict.GetValueOrDefault("Length") switch
                {
                    double d => (int)d,
                    PdfRef r when known.GetValueOrDefault(r.Num) is double d => (int)d,
                    _ => -1,
                };
                if (length < 0 || _pos + length > data.Length || !Encoding.ASCII.GetString(data, Math.Min(_pos + length, data.Length - 1), Math.Min(20, data.Length - Math.Min(_pos + length, data.Length - 1))).Contains("endstream", StringComparison.Ordinal))
                {
                    int end = IndexOf(data, "endstream"u8.ToArray(), _pos);
                    length = (end < 0 ? data.Length : end) - _pos;
                    while (length > 0 && data[_pos + length - 1] is 10 or 13) length--;
                }
                return new PdfStream(dict, data.AsSpan(_pos, length).ToArray());
            }
            return value;
        }

        private static int IndexOf(byte[] hay, byte[] needle, int from)
        {
            for (int i = from; i <= hay.Length - needle.Length; i++)
                if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
            return -1;
        }
    }

    // ── Fonts ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Font
    {
        public int CodeBytes = 1;
        public Dictionary<int, string>? ToUnicode;

        public static Font From(Dictionary<string, object?> dict, Func<object?, object?> resolve)
        {
            var font = new Font();
            if (dict.GetValueOrDefault("Subtype") is PdfName { Value: "Type0" }) font.CodeBytes = 2;
            if (resolve(dict.GetValueOrDefault("ToUnicode")) is PdfStream cmap && PdfTextExtractor.Decode(cmap) is { } bytes)
                font.ToUnicode = ParseCMap(Encoding.Latin1.GetString(bytes), out _) is { Count: > 0 } map ? map : null;
            return font;
        }

        public string Decode(byte[] s)
        {
            var sb = new StringBuilder();
            int step = CodeBytes;
            for (int i = 0; i + step <= s.Length; i += step)
            {
                int code = step == 2 ? (s[i] << 8) | s[i + 1] : s[i];
                if (ToUnicode is not null && ToUnicode.TryGetValue(code, out var u)) sb.Append(u);
                else if (step == 1) sb.Append(WinAnsi(s[i]));
            }
            return sb.ToString();
        }

        private static char WinAnsi(byte b) => b switch
        {
            0x91 => '‘', 0x92 => '’', 0x93 => '“', 0x94 => '”', 0x96 => '–', 0x97 => '—', 0x95 => '•', 0x85 => '…',
            < 32 => ' ',
            _ => (char)b,
        };
    }

    [GeneratedRegex(@"begin(bfchar|bfrange)(.*?)end\1", RegexOptions.Singleline)]
    private static partial Regex CMapSections();

    // bfchar: "<src> <dst>" pairs. bfrange: "<lo> <hi> <dst>" or "<lo> <hi> [<d1> <d2> …]" triples. Separate patterns, so a
    // pair can never swallow the next entry's source code as an optional third element.
    [GeneratedRegex(@"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>")]
    private static partial Regex CMapPair();

    [GeneratedRegex(@"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*(<[0-9A-Fa-f]+>|\[[^\]]*\])")]
    private static partial Regex CMapTriple();

    private static Dictionary<int, string> ParseCMap(string text, out int codeBytes)
    {
        var map = new Dictionary<int, string>();
        codeBytes = 1;
        foreach (Match section in CMapSections().Matches(text))
        {
            bool range = section.Groups[1].Value == "bfrange";
            foreach (Match e in (range ? CMapTriple() : CMapPair()).Matches(section.Groups[2].Value))
            {
                codeBytes = Math.Max(codeBytes, e.Groups[1].Value.Length / 2);
                int lo = Convert.ToInt32(e.Groups[1].Value, 16);
                if (!range) { map[lo] = Utf16(e.Groups[2].Value); continue; }
                int hi = Convert.ToInt32(e.Groups[2].Value, 16);
                string dst = e.Groups[3].Value;
                if (hi - lo > 65535) continue;
                if (dst.StartsWith('['))
                {
                    var items = Regex.Matches(dst, "<([0-9A-Fa-f]+)>").Select(x => x.Groups[1].Value).ToList();
                    for (int c = lo; c <= hi && c - lo < items.Count; c++) map[c] = Utf16(items[c - lo]);
                }
                else if (dst.Length > 2)
                {
                    string hex = dst[1..^1];
                    int baseValue = Convert.ToInt32(hex[^Math.Min(4, hex.Length)..], 16);
                    string prefix = hex.Length > 4 ? Utf16(hex[..^4]) : "";
                    for (int c = lo; c <= hi; c++)
                    {
                        int cp = baseValue + (c - lo);
                        map[c] = prefix + (cp is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(cp) : "\uFFFD");
                    }
                }
            }
        }
        return map;
    }

    private static string Utf16(string hex)
    {
        if (hex.Length % 4 != 0) hex = hex.PadLeft((hex.Length + 3) / 4 * 4, '0');
        try { return Encoding.BigEndianUnicode.GetString(Convert.FromHexString(hex)); }
        catch (FormatException) { return ""; }
    }

    private static string DecodeTextString(byte[] b) =>
        b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2) : Encoding.Latin1.GetString(b);

    // ── Content streams ───────────────────────────────────────────────────────────────────────────────

    private static string Interpret(byte[] content, Dictionary<string, Font> fonts)
    {
        var sb = new StringBuilder();
        var lexer = new Lexer(content, 0);
        var operands = new List<object?>();
        Font font = new();
        double lastY = double.NaN;

        void Show(byte[] s) => sb.Append(font.Decode(s));
        void NewLine() { if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n'); }

        while (!lexer.AtEnd)
        {
            var t = lexer.ReadValue();
            if (t is not PdfOperator op) { operands.Add(t); continue; }
            switch (op.Op)
            {
                case "BT": break;
                case "ET": if (sb.Length > 0 && sb[^1] != '\n') sb.Append(' '); break;
                case "Tf" when operands.Count >= 2 && operands[0] is PdfName fn: font = fonts.GetValueOrDefault(fn.Value) ?? new Font(); break;
                case "Td" or "TD" when operands.Count >= 2 && operands[1] is double ty:
                    if (Math.Abs(ty) > 0.01) NewLine();
                    else if (operands[0] is double tx && tx > 0 && sb.Length > 0 && sb[^1] != ' ' && sb[^1] != '\n') sb.Append(' ');
                    break;
                case "Tm" when operands.Count >= 6 && operands[5] is double y:
                    if (!double.IsNaN(lastY) && Math.Abs(y - lastY) > 0.5) NewLine();
                    else if (sb.Length > 0 && sb[^1] != ' ' && sb[^1] != '\n') sb.Append(' ');
                    lastY = y;
                    break;
                case "T*": NewLine(); break;
                case "Tj" when operands.Count >= 1 && operands[^1] is byte[] s: Show(s); break;
                case "'" when operands.Count >= 1 && operands[^1] is byte[] s1: NewLine(); Show(s1); break;
                case "\"" when operands.Count >= 1 && operands[^1] is byte[] s2: NewLine(); Show(s2); break;
                case "TJ" when operands.Count >= 1 && operands[^1] is List<object?> items:
                    foreach (var item in items)
                    {
                        if (item is byte[] bs) Show(bs);
                        else if (item is double kern && kern < -180 && sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); // a gap wider than ~0.18 em reads as a space
                    }
                    break;
                case "BI": // inline image: skip to EI
                    while (!lexer.AtEnd && lexer.Next() is not PdfOperator { Op: "EI" }) { }
                    break;
            }
            operands.Clear();
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();

    private static string Clean(string text) =>
        string.Join("\n", text.Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0));
}
