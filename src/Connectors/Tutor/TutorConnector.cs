using System.Globalization;
using System.Text.Json;
using Mediator.Core.Connectors;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Core.Tools;

namespace Mediator.Connectors.Tutor;

/// <summary>V18 connector: answers and lessons from the user's PDFs, with exact matrix arithmetic checks.</summary>
public sealed class TutorConnector : IMediatorConnector
{
    public string Id => "tutor";
    public string DisplayName => "Tutor";
    public string Category => "Learning";
    public string Summary => "Ask any question or learn a topic; reads PDF chapters and checks matrix results exactly.";

    /// <summary>Python for PDF text extraction. Null: the runtime's.</summary>
    public IPythonRunner? Python { get; init; }

    public static readonly string[] Tools = [TutorToolProvider.ExtractTool, TutorToolProvider.VerifyTool];
    public IReadOnlyList<string> ChatTools => Tools;

    public ConnectorInstance Start(ConnectorContext context) => new([new TutorToolProvider(Python ?? context.Python, context.Paths.WorkspacesDirectory)]);

    public IReadOnlyList<ConnectorCommand> Commands { get; } =
        [new("matrix", "mediator-cli matrix   \"[[2,1,1],[1,2,1],[1,1,2]]\" [--b \"<matrix>\"]            (exact det/inverse/RREF/eigenvalues; no AI)", Matrix)];

    /// <summary>`mediator-cli matrix`: exact determinant, RREF, inverse and eigenvalues, without any model.</summary>
    private static int Matrix(IReadOnlyList<string> positional, Func<string, string?> option)
    {
        string a = positional.Count > 0 ? positional[0] : throw new ArgumentException("Give a matrix, e.g. \"[[2,1],[1,2]]\".");
        try
        {
            using var docA = JsonDocument.Parse(a);
            var A = ExactMatrix.From(docA.RootElement);
            Console.WriteLine($"A is {A.Rows}×{A.Cols}, rank {A.Rank}");
            var (rref, _, det) = A.Reduce();
            if (det is { } d) Console.WriteLine($"det(A) = {d}");
            Console.WriteLine("rref(A) = " + rref.ToJson().ToJsonString());
            if (A.IsSquare)
            {
                Console.WriteLine(A.Inverse() is { } inv ? "A⁻¹ = " + inv.ToJson().ToJsonString() : "A is singular (no inverse).");
                if (A.Rows <= 8)
                {
                    var cp = A.CharacteristicPolynomial();
                    Console.WriteLine("det(λI − A) coefficients = " + string.Join(", ", cp));
                    Console.WriteLine("eigenvalues ≈ " + string.Join(", ", ExactMatrix.NumericRoots(cp).Select(r =>
                        Math.Abs(r.Imaginary) < 1e-9 ? r.Real.ToString("0.######", CultureInfo.InvariantCulture)
                            : string.Create(CultureInfo.InvariantCulture, $"{r.Real:0.######}{(r.Imaginary < 0 ? "-" : "+")}{Math.Abs(r.Imaginary):0.######}i"))));
                }
            }
            if (option("b") is { } b)
            {
                using var docB = JsonDocument.Parse(b);
                Console.WriteLine("A·B = " + A.Multiply(ExactMatrix.From(docB.RootElement)).ToJson().ToJsonString());
            }
            return 0;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new ArgumentException(ex.Message, ex);
        }
    }

    public IReadOnlyList<ConnectorRoute> Routes { get; } =
        [new(90, TaskCategory.Tutor, Tools, r => r.AnyWord(["tutor", "chapter", "eigen", "determinant", "matrix"]))];

    public IReadOnlyList<ConnectorRecipe> Recipes { get; } =
    [
        new("tutor", 60, "Ask & learn", "Ask any question or learn a topic. Reads PDFs you attach or select; math results are checked exactly.", "\uE82D",
            TaskCategory.Tutor, false, ["tutor", "teach me", "study", "chapter", "textbook", "linear algebra", "matrix", "eigen", "determinant"], Tools,
            r => Tutor(r.Folder, r.Prompt.Length > 0 ? r.Prompt : "help me study a topic", r.Launch)),
    ];

    /// <summary>Prompt 5: tutoring from a textbook chapter, with exact arithmetic checks.</summary>
    public static JobSpec Tutor(string? folder, string request, LaunchMode launch)
    {
        string? full = folder is null ? null : Path.GetFullPath(folder);
        string work = full ?? Path.Combine(Path.GetTempPath(), "Mediator-chat");
        Directory.CreateDirectory(work);
        string source = full is null
            ? "If the user attached PDFs or documents, read them and answer from them."
            : $"Their documents (PDFs, notes) are in {full}; read them when the question is about them.";
        return new JobSpec
        {
            Kind = "tutor", Category = TaskCategory.Tutor, Title = "Ask & learn",
            Tools = Tools, Roots = full is null ? [] : [full], WorkingDirectory = work, Launch = launch, MaxTurns = 40,
            Lifetime = TimeSpan.FromHours(4),
            UserText = request,
            Prompt = $$"""
                Answer the user's question or teach them the topic they ask about, on any subject: {{request}}
                {{source}} For a PDF, use tutor_extract_pdf_chapter on the pages that matter (ask for page numbers if unclear).
                Be clear and direct. When teaching, go one idea at a time with a worked example, and offer a check question.
                Before you state any matrix or linear-algebra result, or grade an answer that contains one, call tutor_verify_matrix
                and use its exact result.
                """,
        };
    }
}
