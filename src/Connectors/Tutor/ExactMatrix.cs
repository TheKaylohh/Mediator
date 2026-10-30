using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mediator.Connectors.Tutor;

/// <summary>An exact rational number. Decimals are read exactly ("0.1" = 1/10), so no floating-point error enters a check.</summary>
public readonly struct Rational : IEquatable<Rational>, IComparable<Rational>
{
    public BigInteger Num { get; }
    public BigInteger Den { get; }

    public Rational(BigInteger num, BigInteger den)
    {
        if (den.IsZero) throw new DivideByZeroException();
        if (den.Sign < 0) { num = -num; den = -den; }
        var g = BigInteger.GreatestCommonDivisor(num, den);
        if (!g.IsZero && !g.IsOne) { num /= g; den /= g; }
        Num = num;
        Den = den.IsZero ? BigInteger.One : den;
    }

    public static readonly Rational Zero = new(0, 1);
    public static readonly Rational One = new(1, 1);
    public static implicit operator Rational(long n) => new(n, 1);

    public bool IsZero => Num.IsZero;
    public bool IsInteger => Den.IsOne;
    public static Rational operator +(Rational a, Rational b) => new(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den);
    public static Rational operator -(Rational a, Rational b) => new(a.Num * b.Den - b.Num * a.Den, a.Den * b.Den);
    public static Rational operator -(Rational a) => new(-a.Num, a.Den);
    public static Rational operator *(Rational a, Rational b) => new(a.Num * b.Num, a.Den * b.Den);
    public static Rational operator /(Rational a, Rational b) => b.IsZero ? throw new DivideByZeroException() : new(a.Num * b.Den, a.Den * b.Num);
    public static bool operator ==(Rational a, Rational b) => a.Equals(b);
    public static bool operator !=(Rational a, Rational b) => !a.Equals(b);
    public static bool operator <(Rational a, Rational b) => a.CompareTo(b) < 0;
    public static bool operator >(Rational a, Rational b) => a.CompareTo(b) > 0;
    public static bool operator <=(Rational a, Rational b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Rational a, Rational b) => a.CompareTo(b) >= 0;
    public bool Equals(Rational other) => Num == other.Num && Den == other.Den;
    public override bool Equals(object? obj) => obj is Rational r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(Num, Den);
    public int CompareTo(Rational other) => (Num * other.Den).CompareTo(other.Num * Den);
    public double ToDouble() => (double)Num / (double)Den;
    public override string ToString() => Den.IsOne ? Num.ToString(CultureInfo.InvariantCulture) : $"{Num.ToString(CultureInfo.InvariantCulture)}/{Den.ToString(CultureInfo.InvariantCulture)}";
    public string ToLatex() => Den.IsOne ? Num.ToString(CultureInfo.InvariantCulture)
        : $"{(Num.Sign < 0 ? "-" : "")}\\frac{{{BigInteger.Abs(Num).ToString(CultureInfo.InvariantCulture)}}}{{{Den.ToString(CultureInfo.InvariantCulture)}}}";

    /// <summary>"3", "-2/5", "0.25", "1.5e-3", or a JSON number.</summary>
    /// <summary>Largest exponent and digit count accepted: "1e999999999" would otherwise allocate a billion-digit integer.</summary>
    public const int MaxExponent = 400, MaxDigits = 400;

    public static Rational Parse(string text)
    {
        string t = text.Trim().Replace(" ", "", StringComparison.Ordinal);
        if (t.Length > 2 * MaxDigits + 10) throw new FormatException($"Number too long (over {MaxDigits} digits).");
        int slash = t.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            var den = Parse(t[(slash + 1)..]);
            if (den.IsZero) throw new FormatException($"'{text}' divides by zero.");
            return Parse(t[..slash]) / den;
        }
        int e = t.IndexOfAny(['e', 'E']);
        int exp = 0;
        if (e > 0)
        {
            if (!int.TryParse(t[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exp) || Math.Abs(exp) > MaxExponent)
                throw new FormatException($"Exponent in '{text}' is outside ±{MaxExponent}.");
            t = t[..e];
        }
        bool neg = t.StartsWith('-');
        t = t.TrimStart('-', '+');
        int dot = t.IndexOf('.', StringComparison.Ordinal);
        string digits = dot < 0 ? t : t.Remove(dot, 1);
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) throw new FormatException($"Not a number: '{text}'. Use integers, decimals or fractions like -3/4.");
        int scale = (dot < 0 ? 0 : t.Length - dot - 1) - exp;
        var n = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        var r = scale >= 0 ? new Rational(n, BigInteger.Pow(10, scale)) : new Rational(n * BigInteger.Pow(10, -scale), 1);
        return neg ? -r : r;
    }

    public static Rational From(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => Parse(e.GetRawText()),
        JsonValueKind.String => Parse(e.GetString()!),
        _ => throw new FormatException("Matrix entries must be numbers or strings like \"3/4\"."),
    };
}

/// <summary>Exact linear algebra over the rationals, for checking work rather than estimating it.</summary>
public sealed class ExactMatrix
{
    private readonly Rational[,] _a;
    public int Rows { get; }
    public int Cols { get; }

    public ExactMatrix(Rational[,] a) { _a = a; Rows = a.GetLength(0); Cols = a.GetLength(1); }
    public Rational this[int r, int c] => _a[r, c];

    public static ExactMatrix From(JsonElement json, int maxSize = 12)
    {
        if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() == 0) throw new FormatException("A matrix is a non-empty array of rows.");
        var rows = json.EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(Rational.From).ToArray() : throw new FormatException("Each row must be an array.")).ToList();
        int cols = rows[0].Length;
        if (cols == 0 || rows.Any(r => r.Length != cols)) throw new FormatException("All rows must have the same, non-zero length.");
        if (rows.Count > maxSize || cols > maxSize) throw new FormatException($"Matrices are limited to {maxSize}×{maxSize}.");
        var a = new Rational[rows.Count, cols];
        for (int i = 0; i < rows.Count; i++) for (int j = 0; j < cols; j++) a[i, j] = rows[i][j];
        return new ExactMatrix(a);
    }

    public static ExactMatrix Identity(int n)
    {
        var a = new Rational[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) a[i, j] = i == j ? Rational.One : Rational.Zero;
        return new ExactMatrix(a);
    }

    public bool IsSquare => Rows == Cols;
    private Rational[,] Copy() => (Rational[,])_a.Clone();

    public ExactMatrix Transpose()
    {
        var t = new Rational[Cols, Rows];
        for (int i = 0; i < Rows; i++) for (int j = 0; j < Cols; j++) t[j, i] = _a[i, j];
        return new ExactMatrix(t);
    }

    public ExactMatrix Multiply(ExactMatrix b)
    {
        if (Cols != b.Rows) throw new FormatException($"Cannot multiply {Rows}×{Cols} by {b.Rows}×{b.Cols}: inner sizes differ.");
        var c = new Rational[Rows, b.Cols];
        for (int i = 0; i < Rows; i++)
            for (int j = 0; j < b.Cols; j++)
            {
                var s = Rational.Zero;
                for (int k = 0; k < Cols; k++) s += _a[i, k] * b._a[k, j];
                c[i, j] = s;
            }
        return new ExactMatrix(c);
    }

    public ExactMatrix Scale(Rational k)
    {
        var c = Copy();
        for (int i = 0; i < Rows; i++) for (int j = 0; j < Cols; j++) c[i, j] *= k;
        return new ExactMatrix(c);
    }

    public ExactMatrix Subtract(ExactMatrix b)
    {
        var c = Copy();
        for (int i = 0; i < Rows; i++) for (int j = 0; j < Cols; j++) c[i, j] -= b._a[i, j];
        return new ExactMatrix(c);
    }

    /// <summary>Reduced row-echelon form, the pivot columns, and the determinant if square (from the same elimination).</summary>
    public (ExactMatrix Rref, IReadOnlyList<int> Pivots, Rational? Determinant) Reduce()
    {
        var m = Copy();
        var pivots = new List<int>();
        Rational det = Rational.One;
        int row = 0;
        for (int col = 0; col < Cols && row < Rows; col++)
        {
            int p = -1;
            for (int r = row; r < Rows; r++) if (!m[r, col].IsZero) { p = r; break; }
            if (p < 0) { det = Rational.Zero; continue; }
            if (p != row) { Swap(m, p, row); det = -det; }
            var pv = m[row, col];
            det *= pv;
            for (int j = 0; j < Cols; j++) m[row, j] /= pv;
            for (int r = 0; r < Rows; r++)
            {
                if (r == row || m[r, col].IsZero) continue;
                var f = m[r, col];
                for (int j = 0; j < Cols; j++) m[r, j] -= f * m[row, j];
            }
            pivots.Add(col);
            row++;
        }
        Rational? determinant = IsSquare ? (pivots.Count == Rows ? det : Rational.Zero) : null;
        return (new ExactMatrix(m), pivots, determinant);
    }

    public int Rank => Reduce().Pivots.Count;

    public Rational Determinant => IsSquare ? Reduce().Determinant!.Value : throw new FormatException("The determinant needs a square matrix.");

    /// <summary>Gauss–Jordan on [A | I]; null when A is singular.</summary>
    public ExactMatrix? Inverse()
    {
        if (!IsSquare) throw new FormatException("Only square matrices have inverses.");
        int n = Rows;
        var aug = new Rational[n, 2 * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < 2 * n; j++)
                aug[i, j] = j < n ? _a[i, j] : (j - n == i ? Rational.One : Rational.Zero);
        var (rref, pivots, _) = new ExactMatrix(aug).Reduce();
        if (pivots.Count < n || pivots[n - 1] != n - 1) return null;
        var inv = new Rational[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) inv[i, j] = rref[i, n + j];
        return new ExactMatrix(inv);
    }

    /// <summary>Coefficients of det(λI − A), highest power first (Faddeev–LeVerrier, exact).</summary>
    public IReadOnlyList<Rational> CharacteristicPolynomial()
    {
        if (!IsSquare) throw new FormatException("The characteristic polynomial needs a square matrix.");
        int n = Rows;
        var coeffs = new Rational[n + 1];
        coeffs[0] = Rational.One;
        var m = new ExactMatrix(new Rational[n, n].Fill(Rational.Zero));
        var id = Identity(n);
        for (int k = 1; k <= n; k++)
        {
            m = Multiply(m).Subtract(id.Scale(-coeffs[k - 1])); // M_k = A·M_{k-1} + c_{k-1}·I
            var am = Multiply(m);
            var trace = Rational.Zero;
            for (int i = 0; i < n; i++) trace += am[i, i];
            coeffs[k] = -trace / k;
        }
        return coeffs;
    }

    public static Rational Evaluate(IReadOnlyList<Rational> coeffs, Rational x)
    {
        var y = Rational.Zero;
        foreach (var c in coeffs) y = y * x + c;
        return y;
    }

    /// <summary>All roots of a polynomial (highest power first) in double precision, by Durand–Kerner.</summary>
    public static IReadOnlyList<Complex> NumericRoots(IReadOnlyList<Rational> coeffs)
    {
        int n = coeffs.Count - 1;
        if (n < 1) return [];
        var a = coeffs.Select(c => c.ToDouble() / coeffs[0].ToDouble()).ToArray();
        var roots = Enumerable.Range(0, n).Select(k => Complex.Pow(new Complex(0.4, 0.9), k)).ToArray();
        for (int iter = 0; iter < 2000; iter++)
        {
            double change = 0;
            for (int i = 0; i < n; i++)
            {
                Complex num = Horner(a, roots[i]);
                Complex den = 1;
                for (int j = 0; j < n; j++) if (j != i) den *= roots[i] - roots[j];
                var delta = num / den;
                roots[i] -= delta;
                change = Math.Max(change, delta.Magnitude);
            }
            if (change < 1e-14) break;
        }
        return roots.Select(r => new Complex(Clean(r.Real), Clean(r.Imaginary))).OrderBy(r => r.Real).ThenBy(r => r.Imaginary).ToList();

        static Complex Horner(double[] a, Complex x) { Complex y = 0; foreach (var c in a) y = y * x + c; return y; }
        static double Clean(double v) => Math.Abs(v) < 1e-10 ? 0 : Math.Round(v, 10);
    }

    public JsonArray ToJson() => new(Enumerable.Range(0, Rows).Select(i => (JsonNode)new JsonArray(Enumerable.Range(0, Cols).Select(j => (JsonNode)_a[i, j].ToString()).ToArray())).ToArray());

    public string ToLatex()
    {
        var sb = new StringBuilder("\\begin{bmatrix}");
        for (int i = 0; i < Rows; i++)
        {
            sb.Append(string.Join(" & ", Enumerable.Range(0, Cols).Select(j => _a[i, j].ToLatex())));
            if (i < Rows - 1) sb.Append(" \\\\ ");
        }
        return sb.Append("\\end{bmatrix}").ToString();
    }

    public bool SameAs(ExactMatrix other)
    {
        if (Rows != other.Rows || Cols != other.Cols) return false;
        for (int i = 0; i < Rows; i++) for (int j = 0; j < Cols; j++) if (_a[i, j] != other._a[i, j]) return false;
        return true;
    }

    private static void Swap(Rational[,] m, int r1, int r2)
    {
        for (int j = 0; j < m.GetLength(1); j++) (m[r1, j], m[r2, j]) = (m[r2, j], m[r1, j]);
    }
}

internal static class ArrayFill
{
    public static T[,] Fill<T>(this T[,] a, T value)
    {
        for (int i = 0; i < a.GetLength(0); i++) for (int j = 0; j < a.GetLength(1); j++) a[i, j] = value;
        return a;
    }
}
