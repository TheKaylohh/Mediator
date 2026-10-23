using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;
using Cockpit.Core.Security;
using Cockpit.Core.Tools;

namespace Cockpit.Core.Tutor;

/// <summary>
/// Tools for tutoring from a textbook: pull a chapter's text out of a PDF into a study pack, and check matrix
/// arithmetic exactly before grading an answer or presenting a worked example (so the model never guesses numbers).
/// Both are read-only for your files; the study pack goes into the job's own artifacts folder.
/// </summary>
public sealed class TutorToolProvider(IPythonRunner python, string workspacesRoot) : IToolProvider
{
    public const string ExtractTool = "tutor_extract_pdf_chapter";
    public const string VerifyTool = "tutor_verify_matrix";
    private const int MaxPages = 60;
    private const int MaxTextChars = 120_000;

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(ExtractTool,
            "Extract the text of a page range of a PDF (e.g. a textbook chapter) with its outline entries, and save it as a " +
            "study_pack.md in the job's artifacts. Page numbers are the PDF's physical pages, starting at 1. Up to 60 pages per call.",
            Schema("""{ "type": "object", "required": ["pdfPath", "firstPage", "lastPage"], "properties": { "pdfPath": { "type": "string" }, "firstPage": { "type": "integer" }, "lastPage": { "type": "integer" } }, "additionalProperties": false }"""),
            ToolRisk.ReadOnly, null, TimeSpan.FromMinutes(5)),

        new(VerifyTool,
            "Exact (rational) linear algebra. ALWAYS call this before stating a numeric result or grading a student's matrix work. " +
            "Entries are numbers or strings like \"-3/4\". operations: determinant, inverse, rref, rank, transpose, multiply (A·B), " +
            "charpoly (det(λI−A)), eigenvalues (numeric roots of the characteristic polynomial). eigenpairs checks A·v = λ·v exactly. " +
            "claims checks a student's answers (determinant, inverse, rref, rank, product) and says which are right. sympy=true also " +
            "writes a SymPy script the student can run.",
            Schema("""
            {
              "type": "object",
              "required": ["A"],
              "properties": {
                "A": { "type": "array", "items": { "type": "array" } },
                "B": { "type": "array", "items": { "type": "array" } },
                "operations": { "type": "array", "items": { "type": "string", "enum": ["determinant", "inverse", "rref", "rank", "transpose", "multiply", "charpoly", "eigenvalues"] } },
                "eigenpairs": { "type": "array", "items": { "type": "object", "required": ["lambda", "vector"], "properties": { "lambda": {}, "vector": { "type": "array" } } } },
                "claims": { "type": "object", "properties": { "determinant": {}, "inverse": { "type": "array" }, "rref": { "type": "array" }, "rank": { "type": "integer" }, "product": { "type": "array" } } },
                "sympy": { "type": "boolean" }
              },
              "additionalProperties": false
            }
            """), ToolRisk.ReadOnly, null, TimeSpan.FromSeconds(30)),
    ];

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct) => Task.FromResult<JsonNode?>(null);

    public async Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        try
        {
            return call.Tool.Name switch
            {
                ExtractTool => await ExtractAsync(call, ct).ConfigureAwait(false),
                VerifyTool => Verify(call),
                _ => ToolResult.Fail($"Unknown tool {call.Tool.Name}."),
            };
        }
        catch (Exception ex) when (ex is FormatException or DivideByZeroException or KeyNotFoundException or InvalidOperationException)
        {
            return ToolResult.Fail(ex.Message);
        }
    }

    private string ArtifactsFor(string jobId)
    {
        string dir = Path.Combine(workspacesRoot, jobId, "artifacts");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── PDF chapter ───────────────────────────────────────────────────────────────────────────────────

    private const string PyMuPdfScript = """
import json, sys
try:
    import pymupdf
except ImportError:
    try:
        import fitz as pymupdf
    except ImportError:
        print(json.dumps({"ok": False, "error": "no_pymupdf"}))
        sys.exit(4)
path, first, last = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
doc = pymupdf.open(path)
if doc.needs_pass:
    print(json.dumps({"ok": False, "error": "encrypted"})); sys.exit(5)
last = min(last, doc.page_count)
pages = [{"number": i + 1, "text": doc[i].get_text("text")} for i in range(first - 1, last)]
toc = [[lvl, title, page] for lvl, title, page in doc.get_toc() if first <= page <= last]
print(json.dumps({"ok": True, "pageCount": doc.page_count, "title": (doc.metadata or {}).get("title") or None, "pages": pages, "toc": toc}))
""";

    private async Task<ToolResult> ExtractAsync(ToolCallContext call, CancellationToken ct)
    {
        var a = call.Arguments;
        var pdfVerdict = call.Job.Paths.Check(a.GetProperty("pdfPath").GetString(), PathAccess.Read);
        if (!pdfVerdict.Allowed) return ToolResult.Fail(pdfVerdict.Message);
        string pdf = pdfVerdict.FullPath;
        if (!pdf.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return ToolResult.Fail($"{pdf} is not a PDF.");
        int first = a.GetProperty("firstPage").GetInt32(), last = a.GetProperty("lastPage").GetInt32();
        if (first < 1 || last < first) return ToolResult.Fail("firstPage must be ≥ 1 and lastPage ≥ firstPage.");
        if (last - first + 1 > MaxPages) return ToolResult.Fail($"At most {MaxPages} pages per call; split the chapter.");

        string engine;
        int pageCount;
        string? title;
        List<(int Number, string Text)> pages;
        List<(int Level, string Title, int Page)> toc = [];
        JsonNode? report = null;
        if (python.IsAvailable && python.MapPath(pdf) is string mapped && call.Job.Paths.VerifyExisting(pdf, PathAccess.Read).Allowed)
        {
            string script = Path.Combine(ArtifactsFor(call.Job.JobId), "extract_pdf.py");
            if (!File.Exists(script)) await File.WriteAllTextAsync(script, PyMuPdfScript, ct).ConfigureAwait(false);
            var run = await python.RunScriptAsync(script, [mapped, first.ToString(CultureInfo.InvariantCulture), last.ToString(CultureInfo.InvariantCulture)], null, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
            try { report = JsonNode.Parse(run.Stdout.Trim().Split('\n')[^1]); } catch (JsonException) { }
        }
        if ((bool?)report?["ok"] == true)
        {
            engine = "PyMuPDF";
            pageCount = (int)report!["pageCount"]!;
            title = (string?)report["title"];
            pages = report["pages"]!.AsArray().Select(p => ((int)p!["number"]!, (string)p["text"]!)).ToList();
            toc = report["toc"]!.AsArray().Select(t => ((int)t![0]!, (string)t[1]!, (int)t[2]!)).ToList();
        }
        else
        {
            if ((string?)report?["error"] == "encrypted") return ToolResult.Fail("The PDF is password-protected.");
            byte[] bytes;
            using (var stream = call.Job.Paths.OpenRead(pdf)) // read through the verified handle, not the path
                bytes = PdfTextExtractor.ReadBounded(stream);
            var doc = PdfTextExtractor.Extract(bytes, first, last);
            engine = "built-in reader" + (python.IsAvailable ? " (PyMuPDF not installed: pip install pymupdf for better results)" : "");
            pageCount = doc.PageCount;
            title = doc.Title;
            pages = doc.Pages.Select(p => (p.Number, p.Text)).ToList();
        }
        if (pages.Count == 0) return ToolResult.Fail($"The PDF has {pageCount} pages; {first}–{last} is outside it.");

        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# Study pack: {title ?? Path.GetFileNameWithoutExtension(pdf)}, pages {pages[0].Number}–{pages[^1].Number}");
        md.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Source: `{pdf}` ({pageCount} pages; text by {engine}).").AppendLine();
        if (toc.Count > 0)
        {
            md.AppendLine("## Outline").AppendLine();
            foreach (var (level, t, page) in toc) md.AppendLine(CultureInfo.InvariantCulture, $"{new string(' ', 2 * (level - 1))}- {t} (p. {page})");
            md.AppendLine();
        }
        foreach (var (n, text) in pages) md.AppendLine(CultureInfo.InvariantCulture, $"## Page {n}").AppendLine().AppendLine(text.Trim()).AppendLine();
        string packPath = SafeFiles.Unique(ArtifactsFor(call.Job.JobId), "study_pack.md");
        SafeFiles.WriteNew(packPath, md.ToString());

        string all = md.ToString();
        bool truncated = all.Length > MaxTextChars;
        int empty = pages.Count(p => p.Text.Trim().Length < 20);
        string note = empty > pages.Count / 2 ? " Most pages have no extractable text: the PDF is probably scanned images (OCR is not included)." : "";
        return ToolResult.Ok((truncated ? all[..MaxTextChars] + "\n…[truncated; the full text is in the study pack]" : all) + $"\n\n(Study pack saved to {packPath}.{note})",
            new JsonObject
            {
                ["studyPack"] = packPath, ["engine"] = engine, ["pageCount"] = pageCount, ["pages"] = pages.Count, ["truncated"] = truncated,
                ["outline"] = new JsonArray(toc.Select(t => (JsonNode)new JsonObject { ["level"] = t.Level, ["title"] = t.Title, ["page"] = t.Page }).ToArray()),
            });
    }

    // ── Exact matrix checks ───────────────────────────────────────────────────────────────────────────

    private ToolResult Verify(ToolCallContext call)
    {
        var a = call.Arguments;
        var A = ExactMatrix.From(a.GetProperty("A"));
        ExactMatrix? B = a.TryGetProperty("B", out var bj) ? ExactMatrix.From(bj) : null;
        var ops = a.TryGetProperty("operations", out var o) ? o.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(A.IsSquare ? ["determinant", "rank", "rref", "inverse"] : ["rank", "rref"], StringComparer.Ordinal);
        var results = new JsonObject { ["size"] = $"{A.Rows}×{A.Cols}" };
        var text = new StringBuilder();
        var (rref, pivots, det) = A.Reduce();

        if (ops.Contains("determinant"))
        {
            if (!A.IsSquare) throw new FormatException("The determinant needs a square matrix.");
            results["determinant"] = det!.Value.ToString();
            text.AppendLine(CultureInfo.InvariantCulture, $"det(A) = {det.Value}");
        }
        if (ops.Contains("rank")) { results["rank"] = pivots.Count; text.AppendLine(CultureInfo.InvariantCulture, $"rank(A) = {pivots.Count}"); }
        if (ops.Contains("rref"))
        {
            results["rref"] = rref.ToJson();
            results["pivotColumns"] = new JsonArray(pivots.Select(p => (JsonNode)(p + 1)).ToArray());
            text.AppendLine(CultureInfo.InvariantCulture, $"rref(A) = {Show(rref)} (pivot columns {string.Join(", ", pivots.Select(p => p + 1))})");
        }
        ExactMatrix? inverse = null;
        if (ops.Contains("inverse"))
        {
            inverse = A.Inverse();
            results["inverse"] = inverse?.ToJson();
            results["invertible"] = inverse is not null;
            text.AppendLine(inverse is null ? "A is singular: no inverse (det = 0)." : $"A⁻¹ = {Show(inverse)}");
        }
        if (ops.Contains("transpose")) { results["transpose"] = A.Transpose().ToJson(); text.AppendLine($"Aᵀ = {Show(A.Transpose())}"); }
        ExactMatrix? product = null;
        if (ops.Contains("multiply"))
        {
            product = A.Multiply(B ?? throw new FormatException("multiply needs B."));
            results["product"] = product.ToJson();
            text.AppendLine($"A·B = {Show(product)}");
        }
        IReadOnlyList<Rational>? charpoly = null;
        if (ops.Contains("charpoly") || ops.Contains("eigenvalues") || a.TryGetProperty("eigenpairs", out _))
        {
            if (!A.IsSquare) throw new FormatException("Eigenvalues need a square matrix.");
            if (A.Rows > 8) throw new FormatException("Eigenvalue checks are limited to 8×8.");
            charpoly = A.CharacteristicPolynomial();
            if (ops.Contains("charpoly"))
            {
                results["charpoly"] = new JsonArray(charpoly.Select(c => (JsonNode)c.ToString()).ToArray());
                text.AppendLine($"det(λI − A) = {Polynomial(charpoly)}");
            }
            if (ops.Contains("eigenvalues"))
            {
                var roots = ExactMatrix.NumericRoots(charpoly);
                results["eigenvaluesNumeric"] = new JsonArray(roots.Select(r => (JsonNode)FormatComplex(r)).ToArray());
                var rationalRoots = RationalRoots(charpoly);
                results["eigenvaluesExactRational"] = new JsonArray(rationalRoots.Select(r => (JsonNode)r.ToString()).ToArray());
                text.AppendLine($"eigenvalues ≈ {string.Join(", ", roots.Select(FormatComplex))}" + (rationalRoots.Count > 0 ? $" (exact rational: {string.Join(", ", rationalRoots)})" : ""));
            }
        }

        if (a.TryGetProperty("eigenpairs", out var pairs))
        {
            var checks = new JsonArray();
            foreach (var pair in pairs.EnumerateArray())
            {
                var lambda = Rational.From(pair.GetProperty("lambda"));
                var v = pair.GetProperty("vector").EnumerateArray().Select(Rational.From).ToArray();
                if (v.Length != A.Rows) throw new FormatException($"Eigenvector length {v.Length} does not match {A.Rows}.");
                bool zero = v.All(x => x.IsZero);
                var av = A.Multiply(Column(v));
                bool exact = !zero && Enumerable.Range(0, v.Length).All(i => av[i, 0] == lambda * v[i]);
                bool root = charpoly is not null && ExactMatrix.Evaluate(charpoly, lambda).IsZero;
                double residual = Math.Sqrt(Enumerable.Range(0, v.Length).Sum(i => Math.Pow((av[i, 0] - lambda * v[i]).ToDouble(), 2)))
                    / Math.Max(1e-300, Math.Sqrt(v.Sum(x => x.ToDouble() * x.ToDouble())));
                checks.Add(new JsonObject
                {
                    ["lambda"] = lambda.ToString(), ["vector"] = new JsonArray(v.Select(x => (JsonNode)x.ToString()).ToArray()),
                    ["isEigenpair"] = exact, ["lambdaIsEigenvalue"] = root, ["relativeResidual"] = residual,
                    ["note"] = zero ? "the zero vector is never an eigenvector" : exact ? "A·v = λ·v exactly" : root ? "λ is an eigenvalue but v is not one of its eigenvectors" : "λ is not an eigenvalue",
                });
                text.AppendLine(CultureInfo.InvariantCulture, $"λ = {lambda}, v = [{string.Join(", ", v)}]: {(exact ? "correct eigenpair" : zero ? "zero vector — not an eigenvector" : root ? "λ is an eigenvalue, but v is not an eigenvector for it" : "λ is not an eigenvalue")}");
            }
            results["eigenpairs"] = checks;
        }

        if (a.TryGetProperty("claims", out var claims) && claims.ValueKind == JsonValueKind.Object)
        {
            var verdicts = new JsonObject();
            foreach (var claim in claims.EnumerateObject())
            {
                bool? ok = claim.Name switch
                {
                    "determinant" => A.IsSquare ? Rational.From(claim.Value) == det!.Value : null,
                    "rank" => claim.Value.GetInt32() == pivots.Count,
                    "rref" => ExactMatrix.From(claim.Value).SameAs(rref),
                    "inverse" => (inverse ??= A.IsSquare ? A.Inverse() : null) is { } inv && ExactMatrix.From(claim.Value).SameAs(inv),
                    "product" => B is not null && ExactMatrix.From(claim.Value).SameAs(product ??= A.Multiply(B)),
                    _ => throw new FormatException($"Unknown claim '{claim.Name}'."),
                };
                verdicts[claim.Name] = ok;
                text.AppendLine(CultureInfo.InvariantCulture, $"claim {claim.Name}: {(ok == true ? "correct" : ok == false ? "INCORRECT" : "not applicable")}");
            }
            results["claims"] = verdicts;
        }

        if (a.TryGetProperty("sympy", out var sp) && sp.ValueKind == JsonValueKind.True)
        {
            string path = SafeFiles.Unique(ArtifactsFor(call.Job.JobId), "check_matrix.py");
            SafeFiles.WriteNew(path, SympyScript(A, B));
            results["sympyScript"] = path;
            text.AppendLine($"SymPy script: {path}");
        }
        results["latexA"] = A.ToLatex();
        return ToolResult.Ok(text.ToString().TrimEnd(), results);
    }

    private static ExactMatrix Column(Rational[] v)
    {
        var c = new Rational[v.Length, 1];
        for (int i = 0; i < v.Length; i++) c[i, 0] = v[i];
        return new ExactMatrix(c);
    }

    private static string Show(ExactMatrix m) =>
        "[" + string.Join("; ", Enumerable.Range(0, m.Rows).Select(i => string.Join(", ", Enumerable.Range(0, m.Cols).Select(j => m[i, j].ToString())))) + "]";

    private static string FormatComplex(Complex c) => Math.Abs(c.Imaginary) < 1e-9
        ? c.Real.ToString("0.######", CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{c.Real:0.######} {(c.Imaginary < 0 ? "-" : "+")} {Math.Abs(c.Imaginary):0.######}i");

    private static string Polynomial(IReadOnlyList<Rational> c)
    {
        int n = c.Count - 1;
        var terms = new List<string>();
        for (int i = 0; i <= n; i++)
        {
            if (c[i].IsZero) continue;
            int power = n - i;
            string coef = c[i] == Rational.One && power > 0 ? "" : c[i] == -Rational.One && power > 0 ? "-" : c[i].ToString();
            terms.Add(coef + (power == 0 ? "" : power == 1 ? "λ" : $"λ^{power}"));
        }
        return string.Join(" + ", terms).Replace("+ -", "- ", StringComparison.Ordinal);
    }

    /// <summary>Exact rational eigenvalues by the rational root theorem on the integer-scaled characteristic polynomial.</summary>
    private static List<Rational> RationalRoots(IReadOnlyList<Rational> coeffs)
    {
        var lcm = coeffs.Aggregate(BigInteger.One, (acc, c) => acc / BigInteger.GreatestCommonDivisor(acc, c.Den) * c.Den);
        var ints = coeffs.Select(c => c.Num * (lcm / c.Den)).ToList();
        var roots = new List<Rational>();
        while (ints.Count > 1 && ints[^1].IsZero) { roots.Add(Rational.Zero); ints.RemoveAt(ints.Count - 1); }
        if (ints.Count <= 1) return roots;
        var lead = BigInteger.Abs(ints[0]);
        var tail = BigInteger.Abs(ints[^1]);
        if (tail > 1_000_000 || lead > 1_000_000) return roots; // too many candidates; numeric roots still reported
        foreach (var p in Divisors(tail))
            foreach (var q in Divisors(lead))
                foreach (var cand in new[] { new Rational(p, q), new Rational(-p, q) })
                    if (!roots.Contains(cand) && ExactMatrix.Evaluate(coeffs, cand).IsZero) roots.Add(cand);
        roots.Sort();
        return roots;
    }

    private static IEnumerable<BigInteger> Divisors(BigInteger n)
    {
        for (BigInteger d = 1; d * d <= n; d++)
            if (n % d == 0) { yield return d; if (d * d != n) yield return n / d; }
    }

    private static string SympyScript(ExactMatrix a, ExactMatrix? b)
    {
        static string M(ExactMatrix m) => "Matrix([" + string.Join(", ", Enumerable.Range(0, m.Rows).Select(i =>
            "[" + string.Join(", ", Enumerable.Range(0, m.Cols).Select(j => m[i, j].IsInteger ? m[i, j].ToString() : $"Rational({m[i, j].Num}, {m[i, j].Den})")) + "]")) + "])";
        var sb = new StringBuilder("# Check the Cockpit's results yourself: pip install sympy, then run this file.\nfrom sympy import Matrix, Rational, symbols, pprint\n");
        sb.AppendLine(CultureInfo.InvariantCulture, $"A = {M(a)}");
        if (b is not null) sb.AppendLine(CultureInfo.InvariantCulture, $"B = {M(b)}\nprint('A*B ='); pprint(A * B)");
        sb.AppendLine("print('rref ='); pprint(A.rref())");
        if (a.IsSquare)
            sb.AppendLine("print('det =', A.det())\nif A.det() != 0:\n    print('inverse ='); pprint(A.inv())\nlam = symbols('lambda')\nprint('charpoly =', A.charpoly(lam).as_expr())\nprint('eigenvects ='); pprint(A.eigenvects())");
        return sb.ToString();
    }
}
