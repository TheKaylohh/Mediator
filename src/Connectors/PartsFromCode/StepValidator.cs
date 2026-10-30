using System.Text;
using System.Text.RegularExpressions;

namespace Mediator.Connectors.PartsFromCode;

/// <summary>
/// Structural check of a STEP (ISO 10303-21) file that runs in Mediator itself, independent of the code that wrote it.
/// It does not prove the geometry is right; it proves the file is a complete Part 21 exchange file that declares at
/// least one solid body (not only surfaces or wireframe), and it bounds the size before anything else reads it.
/// The geometric check (solid count, volume, bounding box) is a second, separate sandboxed re-import.
/// </summary>
public static partial class StepValidator
{
    public const long MaxBytes = 200L * 1024 * 1024;

    public sealed record Report(bool Ok, string? Error, int SolidEntities, int SurfaceOnlyEntities, string? Schema);

    [GeneratedRegex(@"FILE_SCHEMA\s*\(\s*\(\s*'([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaRx();

    [GeneratedRegex(@"=\s*(MANIFOLD_SOLID_BREP|BREP_WITH_VOIDS|FACETED_BREP)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex SolidRx();

    [GeneratedRegex(@"=\s*(SHELL_BASED_SURFACE_MODEL|GEOMETRIC_CURVE_SET)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex SurfaceRx();

    public static Report Check(Stream stream)
    {
        if (stream.CanSeek && stream.Length > MaxBytes) return Fail($"larger than {MaxBytes / (1024 * 1024)} MiB");
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + n > MaxBytes) return Fail($"larger than {MaxBytes / (1024 * 1024)} MiB");
            ms.Write(buf, 0, n);
        }
        string text = Encoding.Latin1.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        string trimmed = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (!trimmed.StartsWith("ISO-10303-21;", StringComparison.Ordinal)) return Fail("does not start with ISO-10303-21;");
        if (!text.TrimEnd().EndsWith("END-ISO-10303-21;", StringComparison.Ordinal)) return Fail("truncated: no END-ISO-10303-21; at the end");
        int header = text.IndexOf("HEADER;", StringComparison.Ordinal), data = text.IndexOf("DATA;", StringComparison.Ordinal);
        if (header < 0 || data < 0 || data < header) return Fail("missing HEADER or DATA section");
        if (CountOf(text, "ENDSEC;") < 2) return Fail("sections are not closed (ENDSEC;)");
        string? schema = SchemaRx().Match(text) is { Success: true } m ? m.Groups[1].Value : null;
        int solids = SolidRx().Matches(text).Count, surfaces = SurfaceRx().Matches(text).Count;
        if (solids == 0)
            return new Report(false, surfaces > 0 ? "contains only surface or wireframe geometry, no solid body" : "declares no solid body", 0, surfaces, schema);
        return new Report(true, null, solids, surfaces, schema);
    }

    private static int CountOf(string s, string token)
    {
        int c = 0;
        for (int i = s.IndexOf(token, StringComparison.Ordinal); i >= 0; i = s.IndexOf(token, i + token.Length, StringComparison.Ordinal)) c++;
        return c;
    }

    private static Report Fail(string why) => new(false, why, 0, 0, null);
}
