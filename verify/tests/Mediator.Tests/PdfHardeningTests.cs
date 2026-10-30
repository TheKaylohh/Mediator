using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Mediator.Core.Runtime;
using Mediator.Connectors.Tutor;

namespace Mediator.Tests;

/// <summary>
/// V6 Phases 4–5: the built-in PDF reader on hostile input. Anything that could kill the process (stack overflow) runs
/// in a child process, so a regression fails this test instead of taking the whole runner down.
/// </summary>
static class PdfHardeningTests
{
    public static string TestsDll = "";

    static byte[] Pdf(params string[] objectBodies)
    {
        var ms = new MemoryStream();
        ms.Write("%PDF-1.5\n"u8);
        for (int i = 0; i < objectBodies.Length; i++) ms.Write(Encoding.Latin1.GetBytes($"{i + 1} 0 obj\n{objectBodies[i]}\nendobj\n"));
        ms.Write("trailer << /Root 1 0 R >>\n%%EOF\n"u8);
        return ms.ToArray();
    }

    static byte[] PdfWithStream(string pageContentDict, byte[] data)
    {
        var ms = new MemoryStream();
        ms.Write("%PDF-1.5\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>\nendobj\n"u8);
        ms.Write(Encoding.Latin1.GetBytes($"4 0 obj\n<< {pageContentDict} /Length {data.Length} >>\nstream\n"));
        ms.Write(data);
        ms.Write("\nendstream\nendobj\ntrailer << /Root 1 0 R >>\n%%EOF\n"u8);
        return ms.ToArray();
    }

    static byte[] Flate(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }

    /// <summary>Child mode (--pdf-child kind seed iterations): runs one hostile workload and prints a summary line.</summary>
    public static int Child(string kind, int seed, int iterations)
    {
        switch (kind)
        {
            case "nesting":
            {
                var deep = Pdf("<< /Type /Catalog /Pages 2 0 R /X " + new string('[', 200_000) + " >>", "<< /Type /Pages /Kids [] /Count 0 >>");
                try { PdfTextExtractor.Extract(deep, 1, 1); Console.WriteLine("RESULT no-exception"); }
                catch (FormatException ex) { Console.WriteLine("RESULT format " + ex.Message); }
                return 0;
            }
            case "fuzz":
            {
                var rng = new Random(seed);
                var baseline = TestPdf.Build();
                int ok = 0, format = 0;
                long worst = 0;
                for (int i = 0; i < iterations; i++)
                {
                    var data = (byte[])baseline.Clone();
                    switch (rng.Next(5))
                    {
                        case 0: for (int k = 0; k < 1 + rng.Next(20); k++) data[rng.Next(data.Length)] = (byte)rng.Next(256); break;
                        case 1: data = data[..rng.Next(9, data.Length)]; break;
                        case 2:
                        {
                            int at = rng.Next(data.Length);
                            byte[] junk = Encoding.Latin1.GetBytes(new[] { "[[[[[[", "<<<<", "999999999 0 R", "/Length -5", "stream\n", "/N 2147483647", "<FFFFFFFFFFFF>", "((((((", "BT BT" }[rng.Next(9)]);
                            data = [.. data[..at], .. junk, .. data[at..]];
                            break;
                        }
                        case 3: { int a = rng.Next(data.Length), b = rng.Next(a, Math.Min(data.Length, a + 400)); data = [.. data[..a], .. data[b..]]; break; }
                        default: { int a = rng.Next(data.Length - 50); Array.Copy(data, a, data, rng.Next(data.Length - 50), 40); break; }
                    }
                    var sw = Stopwatch.StartNew();
                    try { PdfTextExtractor.Extract(data, rng.Next(-2, 4), rng.Next(-1, 6)); ok++; }
                    catch (FormatException) { format++; }
                    worst = Math.Max(worst, sw.ElapsedMilliseconds);
                }
                Console.WriteLine($"RESULT ok={ok} format={format} worstMs={worst}");
                return 0;
            }
        }
        return 2;
    }

    static async Task<(int Exit, string Out)> RunChild(params string[] args)
    {
        var psi = new ProcessStartInfo(ComponentLocator.DotnetHost()) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(TestsDll);
        psi.ArgumentList.Add("--pdf-child");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        return (p.ExitCode, output + await err);
    }

    public static async Task DeepNestingIsAFormatError()
    {
        var (exit, output) = await RunChild("nesting", "0", "0");
        Check.True(exit == 0, $"the reader survived 200 000 nested arrays (before V6 this overflowed the stack and killed the process): exit {exit} {output[..Math.Min(300, output.Length)]}");
        // The over-deep object is dropped (like any unreadable object), so the file is reported as having no catalog.
        Check.True(output.Contains("RESULT format", StringComparison.Ordinal), "reported as a format error: " + output.Trim());
    }

    public static Task ResourceLimits()
    {
        // Decompression bomb: 300 MiB of zeros in a ~300 KiB stream.
        var zeros = new byte[300 * 1024 * 1024];
        var bomb = PdfWithStream("/Filter /FlateDecode", Flate(zeros));
        Check.True(bomb.Length < 2 * 1024 * 1024, $"bomb fixture is small ({bomb.Length} bytes)");
        var sw = Stopwatch.StartNew();
        try { PdfTextExtractor.Extract(bomb, 1, 1); throw new Exception("Assertion failed: bomb accepted"); }
        catch (FormatException ex) { Check.True(ex.Message.Contains("decompresses to more than 64 MiB", StringComparison.Ordinal), "per-stream cap: " + ex.Message); }
        Check.True(sw.ElapsedMilliseconds < 20_000, $"stopped promptly ({sw.ElapsedMilliseconds} ms)");

        // Total budget across many streams (small limits to keep the test fast).
        var tight = new PdfTextExtractor.Limits { MaxDecodedBytesPerStream = 4 << 20, MaxDecodedBytesTotal = 6 << 20 };
        var two = Flate(new byte[3 << 20]);
        var many = Pdf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /Contents [4 0 R 5 0 R 6 0 R] >>",
            $"<< /Filter /FlateDecode /Length {two.Length} >>\nstream\n{Encoding.Latin1.GetString(two)}\nendstream",
            $"<< /Filter /FlateDecode /Length {two.Length} >>\nstream\n{Encoding.Latin1.GetString(two)}\nendstream",
            $"<< /Filter /FlateDecode /Length {two.Length} >>\nstream\n{Encoding.Latin1.GetString(two)}\nendstream");
        try { PdfTextExtractor.Extract(many, 1, 1, tight); throw new Exception("Assertion failed: total budget not enforced"); }
        catch (FormatException ex) { Check.True(ex.Message.Contains("in total", StringComparison.Ordinal), "document-wide cap: " + ex.Message); }

        // Millions of operands with no operator: bounded memory, still finishes.
        var operands = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("1 ", 2_000_000)) + "BT (ok) Tj ET");
        var flood = PdfWithStream("/Filter /FlateDecode", Flate(operands));
        long before = GC.GetTotalMemory(true);
        var text = PdfTextExtractor.Extract(flood, 1, 1);
        Check.True(text.Pages.Single().Text.Contains("ok", StringComparison.Ordinal), "operand flood still extracts the text");
        Check.True(GC.GetTotalMemory(false) - before < 400L << 20, "operand flood stays bounded");

        // Time budget.
        var slow = new PdfTextExtractor.Limits { MaxTime = TimeSpan.FromMilliseconds(1) };
        try { PdfTextExtractor.Extract(flood, 1, 1, slow); throw new Exception("Assertion failed: time budget not enforced"); }
        catch (FormatException ex) { Check.True(ex.Message.Contains("took longer than", StringComparison.Ordinal), "time budget: " + ex.Message); }

        // ObjStm claiming 2^31 objects: refused quickly, not looped.
        var objStm = Pdf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>",
            "<< /Type /ObjStm /N 2147483647 /First 4 /Length 8 >>\nstream\n1 0 2 0 \nendstream", "<< /Length 13 >>\nstream\nBT (hi) Tj ET\nendstream");
        sw.Restart();
        var r = PdfTextExtractor.Extract(objStm, 1, 1);
        Check.True(sw.ElapsedMilliseconds < 5000 && r.Pages.Single().Text == "hi", $"huge /N bounded ({sw.ElapsedMilliseconds} ms)");

        // Encrypted, unsupported compression, page ranges.
        try { PdfTextExtractor.Extract(Pdf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Filter /Standard /V 2 >>"), 1, 1); throw new Exception("Assertion failed"); }
        catch (FormatException ex) { Check.True(ex.Message.Contains("encrypted", StringComparison.Ordinal), "encrypted → clear refusal"); }
        var dct = PdfTextExtractor.Extract(PdfWithStream("/Filter /DCTDecode", new byte[64]), 1, 1);
        Check.True(dct.Pages.Single().Text.Length == 0, "unsupported compression yields no text, no crash");
        var good = TestPdf.Build();
        Check.True(PdfTextExtractor.Extract(good, 5, 2).Pages.Count == 0 && PdfTextExtractor.Extract(good, -3, 1).Pages.Single().Number == 1
            && PdfTextExtractor.Extract(good, 2, 99).Pages.Count == 2, "page ranges clamp; inverted ranges return nothing");
        return Task.CompletedTask;
    }

    public static async Task MutationFuzz()
    {
        const int iterations = 3000;
        var (exit, output) = await RunChild("fuzz", "6106", iterations.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Check.True(exit == 0, $"fuzzing never crashed the process (exit {exit}): {output[..Math.Min(600, output.Length)]}");
        var line = output.Split('\n').Single(l => l.StartsWith("RESULT", StringComparison.Ordinal));
        Console.WriteLine($"      {iterations} mutated PDFs (seed 6106): {line[7..]}");
        var parts = line[7..].Split(' ').Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => long.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture));
        Check.Equal((long)iterations, parts["ok"] + parts["format"], "every input either parsed or failed with FormatException");
        Check.True(parts["worstMs"] < 5000, $"no input took more than 5 s (worst {parts["worstMs"]} ms)");
    }
}

/// <summary>V6: exactness and pathological input for the tutor's matrix verifier, through the real bus.</summary>
static class TutorExactnessTests
{
    static System.Text.Json.Nodes.JsonArray M(params string[][] rows) =>
        new(rows.Select(r => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonArray(r.Select(x => (System.Text.Json.Nodes.JsonNode)x).ToArray())).ToArray());

    public static async Task ExactnessAndPathologicalInput(string bridgeDll)
    {
        await using var runtime = await ProviderTests.Runtime(bridgeDll, new UntickPrompt());
        string folder = Path.Combine(Path.GetTempPath(), "mediator-tutor", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var job = await runtime.StartJobAsync(TutorConnector.Tutor(folder, "ch 3", LaunchMode.None) with { JobId = "tut-x" });
        await using var client = await runtime.ConnectClientAsync(job);
        async Task<McpToolCallResult> V(System.Text.Json.Nodes.JsonObject args) => await client.CallToolAsync(TutorToolProvider.VerifyTool, args, TimeSpan.FromSeconds(30));
        System.Text.Json.Nodes.JsonObject Args(System.Text.Json.Nodes.JsonArray a, params string[] ops) => new()
        {
            ["A"] = a, ["operations"] = new System.Text.Json.Nodes.JsonArray(ops.Select(o => (System.Text.Json.Nodes.JsonNode)o).ToArray()),
        };

        // Nearly singular: det = 1e-15 exactly; a double-precision solver would call it singular.
        var near = await V(Args(M(["1", "1"], ["1", "1.000000000000001"]), "determinant", "inverse"));
        Check.True((string?)near.Structured!["determinant"] == "1/1000000000000000" && (bool)near.Structured["invertible"]!, "nearly singular: exact det 10^-15, invertible");
        // Singular: no inverse, claim of an inverse is wrong; rank 1.
        var sing = await V(new() { ["A"] = M(["1", "2"], ["2", "4"]), ["operations"] = new System.Text.Json.Nodes.JsonArray("inverse", "rank"),
            ["claims"] = new System.Text.Json.Nodes.JsonObject { ["inverse"] = M(["1", "0"], ["0", "1"]), ["rank"] = 1 } });
        Check.True(!(bool)sing.Structured!["invertible"]! && (int)sing.Structured["rank"]! == 1 && (bool?)sing.Structured["claims"]!["inverse"] == false
            && (bool?)sing.Structured["claims"]!["rank"] == true, "singular: no inverse, inverse claim rejected, rank claim accepted");
        // Rational input and output.
        var rat = await V(Args(M(["1/2", "1/3"], ["1/4", "1/5"]), "determinant", "inverse"));
        Check.Equal("1/60", (string?)rat.Structured!["determinant"], "rational determinant exact");
        Check.Equal("[[\"12\",\"-20\"],[\"-15\",\"30\"]]", rat.Structured["inverse"]!.ToJsonString(), "rational inverse exact");
        // Repeated eigenvalue with a defective matrix: 2 is an eigenvalue, [0,1] is not its eigenvector.
        var rep = await V(new() { ["A"] = M(["2", "1"], ["0", "2"]), ["operations"] = new System.Text.Json.Nodes.JsonArray("charpoly", "eigenvalues"),
            ["eigenpairs"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["lambda"] = 2, ["vector"] = new System.Text.Json.Nodes.JsonArray(1, 0) },
                new System.Text.Json.Nodes.JsonObject { ["lambda"] = 2, ["vector"] = new System.Text.Json.Nodes.JsonArray(0, 1) },
                new System.Text.Json.Nodes.JsonObject { ["lambda"] = 2, ["vector"] = new System.Text.Json.Nodes.JsonArray(0, 0) }) });
        var ep = rep.Structured!["eigenpairs"]!.AsArray();
        Check.True((bool)ep[0]!["isEigenpair"]! && !(bool)ep[1]!["isEigenpair"]! && (bool)ep[1]!["lambdaIsEigenvalue"]! && !(bool)ep[2]!["isEigenpair"]!,
            "defective matrix: [1,0] yes, [0,1] no (λ right, v wrong), zero vector never");
        Check.Equal("[\"1\",\"-4\",\"4\"]", rep.Structured["charpoly"]!.ToJsonString(), "charpoly λ²−4λ+4 exact");
        Check.Equal("[\"2\"]", rep.Structured["eigenvaluesExactRational"]!.ToJsonString(), "repeated root reported once, exactly");
        // Complex eigenvalues: no rational roots; numeric ones labelled approximate (≈).
        var rot = await V(Args(M(["0", "-1"], ["1", "0"]), "eigenvalues"));
        Check.True(rot.Structured!["eigenvaluesExactRational"]!.AsArray().Count == 0 && rot.Structured["eigenvaluesNumeric"]!.ToJsonString().Contains('i')
            && rot.Text.Contains("eigenvalues ≈", StringComparison.Ordinal), "rotation: ±i numerically, marked approximate, no exact rational claim");
        // Very large values stay exact.
        string big = "1" + new string('0', 60);
        var huge = await V(Args(M([big, "1"], ["1", big]), "determinant"));
        Check.Equal((System.Numerics.BigInteger.Pow(10, 120) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            (string?)huge.Structured!["determinant"], "10^120 − 1 exactly");
        // Pathological input: refused with a reason, never a hang or a crash.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (bad, why) in new[] { ("1e999999999", "Exponent"), ("1/0", "divides by zero"), (new string('9', 5000), "too long"), ("abc", "Not a number") })
        {
            var r = await V(Args(M([bad, "1"], ["1", "1"]), "determinant"));
            Check.True(r.IsError && r.Text.Contains(why, StringComparison.Ordinal), $"'{bad[..Math.Min(12, bad.Length)]}' refused: {r.Text}");
        }
        Check.True(sw.ElapsedMilliseconds < 10_000, "pathological numbers refused quickly");
        var claimBad = await V(new() { ["A"] = M(["1", "0"], ["0", "1"]), ["claims"] = new System.Text.Json.Nodes.JsonObject { ["rank"] = "two" } });
        Check.True(claimBad.IsError && claimBad.Text.Contains("whole number", StringComparison.Ordinal), "malformed claim → clear error");
        var unknownClaim = await V(new() { ["A"] = M(["1", "0"], ["0", "1"]), ["claims"] = new System.Text.Json.Nodes.JsonObject { ["trace"] = 2 } });
        Check.True(unknownClaim.IsError && unknownClaim.Text.Contains("Unknown claim", StringComparison.Ordinal), "unknown claim → clear error");
        var tooBig = await V(Args(new System.Text.Json.Nodes.JsonArray(Enumerable.Range(0, 13).Select(i => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonArray(Enumerable.Range(0, 13).Select(j => (System.Text.Json.Nodes.JsonNode)(i == j ? 1 : 0)).ToArray())).ToArray()), "determinant"));
        Check.True(tooBig.IsError, "13×13 refused (12×12 limit)");
    }
}
