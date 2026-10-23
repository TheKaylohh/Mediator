using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;

namespace Cockpit.Tests;

/// <summary>
/// Static checks for the WPF overlay's XAML, standing in for the compiler and the binding engine on Linux:
///  • every file is well-formed XML with declared namespaces;
///  • every {StaticResource K} is defined (in App.xaml, earlier in document order, or in the same file);
///  • every {Binding path} resolves to public properties of the ViewModel type in scope, where scope follows
///    the root d:DataContext, DataContext="{Binding X}", and ItemsSource item types for item templates/columns;
///    RelativeSource AncestorType=Window bindings resolve against the root type after "DataContext.";
///  • *Command paths are ICommand; two-way-by-default targets (TextBox.Text, IsChecked, SelectedIndex) have setters;
///  • local:Converters used in XAML exist as classes, and x:Name elements used from code-behind exist.
/// </summary>
static partial class XamlChecker
{
    public sealed record Report(List<string> Errors, int BindingsChecked, int ResourcesChecked);

    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    static readonly XNamespace D = "http://schemas.microsoft.com/expression/blend/2008";

    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SPEC.md")) && Directory.Exists(Path.Combine(dir.FullName, "src"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root not found");
    }

    [GeneratedRegex(@"\{StaticResource\s+([^}\s]+)\s*\}")]
    private static partial Regex StaticResource();

    [GeneratedRegex(@"\bclass\s+(\w+)")]
    private static partial Regex ClassDecl();

    public static Report Check(string wpfFolder, Assembly viewModels)
    {
        var errors = new List<string>();
        int bindings = 0, resources = 0;

        var app = XDocument.Load(Path.Combine(wpfFolder, "App.xaml"), LoadOptions.SetLineInfo);
        var appKeys = new List<(string Key, XElement El)>();
        foreach (var el in app.Descendants()) if (el.Attribute(X + "Key") is { } k) appKeys.Add((k.Value, el));

        string code = string.Join("\n", Directory.GetFiles(wpfFolder, "*.cs").Select(File.ReadAllText));
        var classes = ClassDecl().Matches(code).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        // App.xaml: StaticResource must be defined before use (the XAML loader resolves them in order).
        var order = app.Descendants().Select((e, i) => (e, i)).ToDictionary(t => t.e, t => t.i);
        foreach (var el in app.Descendants())
        {
            foreach (var value in el.Attributes().Select(a => a.Value).Concat(el.HasElements ? [] : [el.Value]))
            {
                foreach (Match m in StaticResource().Matches(value))
                {
                    resources++;
                    var def = appKeys.FirstOrDefault(k => k.Key == m.Groups[1].Value);
                    if (def.El is null) errors.Add($"App.xaml:{Line(el)} StaticResource '{m.Groups[1].Value}' is not defined");
                    else if (order[def.El] >= order[el] && !el.AncestorsAndSelf().Contains(def.El)) errors.Add($"App.xaml:{Line(el)} StaticResource '{m.Groups[1].Value}' is used before it is defined");
                }
            }
            if (el.Name.NamespaceName.StartsWith("clr-namespace:Cockpit.Wpf", StringComparison.Ordinal) && !classes.Contains(el.Name.LocalName))
                errors.Add($"App.xaml:{Line(el)} local:{el.Name.LocalName} has no class in Cockpit.Wpf");
        }

        foreach (var file in Directory.GetFiles(wpfFolder, "*.xaml").Where(f => !f.EndsWith("App.xaml", StringComparison.Ordinal)))
        {
            string name = Path.GetFileName(file);
            XDocument doc;
            try { doc = XDocument.Load(file, LoadOptions.SetLineInfo); }
            catch (System.Xml.XmlException ex) { errors.Add($"{name}: not well-formed: {ex.Message}"); continue; }

            var localKeys = doc.Descendants().Select(e => e.Attribute(X + "Key")?.Value).Where(k => k is not null).ToHashSet();
            foreach (var el in doc.Descendants())
                foreach (var attr in el.Attributes())
                    foreach (Match m in StaticResource().Matches(attr.Value))
                    {
                        resources++;
                        if (!appKeys.Any(k => k.Key == m.Groups[1].Value) && !localKeys.Contains(m.Groups[1].Value))
                            errors.Add($"{name}:{Line(el)} StaticResource '{m.Groups[1].Value}' is not defined");
                    }

            var root = doc.Root!;
            string? design = root.Attribute(D + "DataContext")?.Value;
            var typeName = design is null ? null : Regex.Match(design, @"Type=\w+:(\w+)").Groups[1].Value;
            var rootType = typeName is null ? null : viewModels.GetTypes().FirstOrDefault(t => t.Name == typeName);
            if (rootType is null) { errors.Add($"{name}: root d:DataContext DesignInstance type not found"); continue; }

            void Walk(XElement el, Type? context)
            {
                // DataContext="{Binding X}" changes the scope for the element's own other bindings and its children.
                if (el.Attribute("DataContext") is { } dc && ParseBinding(dc.Value) is { } dcb)
                {
                    bindings++;
                    context = Resolve(dcb, context, rootType, $"{name}:{Line(el)} DataContext", errors, out _);
                }
                Type? itemType = null;
                foreach (var attr in el.Attributes().Where(a => a.Name.LocalName != "DataContext" && !a.IsNamespaceDeclaration && a.Name.Namespace != D))
                {
                    if (ParseBinding(attr.Value) is not { } b) continue;
                    bindings++;
                    string where = $"{name}:{Line(el)} {el.Name.LocalName}.{attr.Name.LocalName}";
                    var resolved = Resolve(b, context, rootType, where, errors, out var prop);
                    if (prop is not null && attr.Name.LocalName.EndsWith("Command", StringComparison.Ordinal) && !typeof(ICommand).IsAssignableFrom(prop.PropertyType))
                        errors.Add($"{where}: {b.Path} is not an ICommand");
                    bool twoWayTarget = (el.Name.LocalName, attr.Name.LocalName) is ("TextBox", "Text") or ("CheckBox", "IsChecked") or ("ToggleButton", "IsChecked") or ("TabControl", "SelectedIndex");
                    if (twoWayTarget && !b.OneWay && prop is not null && prop.SetMethod is not { IsPublic: true })
                        errors.Add($"{where}: two-way binding to read-only {b.Path}");
                    if (attr.Name.LocalName == "ItemsSource") itemType = ElementType(resolved);
                }
                if (el.Name.NamespaceName.StartsWith("clr-namespace:Cockpit.Wpf", StringComparison.Ordinal) && !classes.Contains(el.Name.LocalName))
                    errors.Add($"{name}:{Line(el)} local:{el.Name.LocalName} has no class");
                foreach (var child in el.Elements()) Walk(child, itemType ?? context);
            }
            Walk(root, rootType);

            // Named elements the code-behind uses.
            string behind = File.Exists(file + ".cs") ? File.ReadAllText(file + ".cs") : "";
            var names = doc.Descendants().Select(e => e.Attribute(X + "Name")?.Value).Where(n => n is not null).ToHashSet();
            foreach (Match m in Regex.Matches(behind, @"\b([A-Z]\w*Box|[A-Z]\w*Grid|[A-Z]\w*List)\.(?:Focus|SelectAll|ScrollIntoView)\("))
                if (!names.Contains(m.Groups[1].Value)) errors.Add($"{name}.cs uses {m.Groups[1].Value}, which has no x:Name");
            if (!behind.Contains("InitializeComponent()", StringComparison.Ordinal)) errors.Add($"{name}.cs does not call InitializeComponent()");
        }
        return new Report(errors, bindings, resources);
    }

    sealed record BindingInfo(string Path, bool AncestorWindow, bool OneWay);

    /// <summary>Parses {Binding ...}; nested markup extensions (Converter={StaticResource X}) are skipped as units.</summary>
    static BindingInfo? ParseBinding(string value)
    {
        string v = value.Trim();
        if (!v.StartsWith("{Binding", StringComparison.Ordinal)) return null;
        string inner = v[8..^1].Trim();
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '{') depth++;
            else if (inner[i] == '}') depth--;
            else if (inner[i] == ',' && depth == 0) { parts.Add(inner[start..i].Trim()); start = i + 1; }
        }
        if (inner.Length > 0) parts.Add(inner[start..].Trim());
        string path = "";
        bool ancestor = false, oneWay = false;
        foreach (var p in parts)
        {
            if (!p.Contains('=', StringComparison.Ordinal) && path.Length == 0) path = p;
            else if (p.StartsWith("Path=", StringComparison.Ordinal)) path = p[5..];
            else if (p.StartsWith("RelativeSource=", StringComparison.Ordinal)) ancestor = p.Contains("AncestorType=Window", StringComparison.Ordinal);
            else if (p is "Mode=OneWay" or "Mode=OneTime") oneWay = true;
        }
        return new BindingInfo(path, ancestor, oneWay);
    }

    static Type? Resolve(BindingInfo b, Type? context, Type rootType, string where, List<string> errors, out PropertyInfo? last)
    {
        last = null;
        string path = b.Path;
        Type? type = context;
        if (b.AncestorWindow)
        {
            if (!path.StartsWith("DataContext.", StringComparison.Ordinal)) { errors.Add($"{where}: Window-relative binding must go through DataContext"); return null; }
            path = path["DataContext.".Length..];
            type = rootType;
        }
        if (path.Length == 0) return type;
        if (type is null) { errors.Add($"{where}: no data context for '{b.Path}'"); return null; }
        foreach (var segment in path.Split('.'))
        {
            var prop = type!.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null) { errors.Add($"{where}: '{segment}' is not a public property of {type.Name} (path '{b.Path}')"); return null; }
            last = prop;
            type = prop.PropertyType;
        }
        return type;
    }

    static Type? ElementType(Type? collection)
    {
        if (collection is null) return null;
        var enumerable = collection.GetInterfaces().Append(collection)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0] ?? (typeof(IEnumerable).IsAssignableFrom(collection) ? typeof(object) : null);
    }

    static int Line(XElement el) => ((System.Xml.IXmlLineInfo)el).LineNumber;
}
