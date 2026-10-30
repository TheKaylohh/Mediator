using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;

namespace Mediator.Tests;

/// <summary>
/// Static checks for the WPF overlay's XAML, standing in for the compiler and the binding engine on Linux:
///  • every .xaml file (recursively) is well-formed XML; no element sets a property both as an attribute and as a
///    property element (a XAML load error);
///  • themes (V7): Themes/&lt;name&gt;.xaml exists for every MediatorTheme value; all of them define exactly the same keys,
///    with the same kind of value per key (Color, a Brush, FontFamily, …); inside a theme a StaticResource refers to
///    a key defined earlier in that theme;
///  • every {DynamicResource K} is defined by every theme (or by App.xaml);
///  • every {StaticResource K} is defined (App.xaml, earlier in document order, or in the same file), and never
///    refers to a theme key outside Themes/ (a StaticResource would not follow a theme switch);
///  • no colour literal (#AARRGGBB or a named colour other than Transparent) outside Themes/;
///  • every {Binding path} resolves to public properties of the ViewModel type in scope, where scope follows the
///    root d:DataContext, DataContext="{Binding X}", and ItemsSource item types for item templates/columns;
///    RelativeSource AncestorType=Window bindings resolve against the root type after "DataContext.";
///    ElementName bindings resolve to a named element, and on a UserControl's own root to a dependency property
///    declared in its code-behind;
///  • *Command paths are ICommand; two-way-by-default targets (TextBox.Text, IsChecked, SelectedIndex) have setters;
///  • local and controls: elements exist as classes, and x:Name elements used from code-behind exist.
/// </summary>
static partial class XamlChecker
{
    public sealed record Report(List<string> Errors, int BindingsChecked, int ResourcesChecked, int ThemeKeys, int Files, int PackUrisChecked = 0);

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

    [GeneratedRegex(@"\{DynamicResource\s+([^}\s]+)\s*\}")]
    private static partial Regex DynamicResource();

    [GeneratedRegex(@"\bclass\s+(\w+)")]
    private static partial Regex ClassDecl();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{3,8}$")]
    private static partial Regex HexColor();

    static readonly HashSet<string> NamedColours = new(StringComparer.OrdinalIgnoreCase)
    {
        "Black", "White", "Red", "Green", "Blue", "Yellow", "Orange", "Gray", "Grey", "DarkGray", "LightGray", "Cyan", "Magenta",
        "Purple", "Pink", "Brown", "Navy", "Teal", "Lime", "Silver", "Gold", "Crimson", "Aqua", "Maroon", "Olive",
    };
    static readonly HashSet<string> ColourProperties = new(StringComparer.Ordinal)
    {
        "Foreground", "Background", "BorderBrush", "Fill", "Stroke", "Color", "CaretBrush", "SelectionBrush", "RowBackground", "OpacityMask",
    };

    static IEnumerable<string> XamlFiles(string folder) => Directory.EnumerateFiles(folder, "*.xaml", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")).OrderBy(f => f, StringComparer.Ordinal);

    static IEnumerable<string> CodeFiles(string folder) => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    /// <summary>The kind of value a key holds, for theme parity: brushes of any type are interchangeable.</summary>
    static string KindOf(XElement el) => el.Name.LocalName.EndsWith("Brush", StringComparison.Ordinal) ? "Brush" : el.Name.LocalName;

    public static Report Check(string wpfFolder, Assembly viewModels)
    {
        var errors = new List<string>();
        int bindings = 0, resources = 0, files = 0;
        string Rel(string f) => Path.GetRelativePath(wpfFolder, f).Replace('\\', '/');

        string code = string.Join("\n", CodeFiles(wpfFolder).Select(File.ReadAllText));
        var classes = ClassDecl().Matches(code).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        // ── Load everything once ──
        var docs = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        foreach (var f in XamlFiles(wpfFolder))
        {
            files++;
            try { docs[f] = XDocument.Load(f, LoadOptions.SetLineInfo); }
            catch (System.Xml.XmlException ex) { errors.Add($"{Rel(f)}: not well-formed: {ex.Message}"); }
        }
        foreach (var (f, doc) in docs) DuplicateProperties(doc, Rel(f), errors);

        // ── Themes ──
        var themeEnum = viewModels.GetTypes().FirstOrDefault(t => t.IsEnum && t.Name == "MediatorTheme");
        var themeNames = themeEnum is null ? [] : Enum.GetNames(themeEnum);
        if (themeEnum is null) errors.Add("MediatorTheme enum not found in the ViewModel assembly");
        var themes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal); // theme → key → kind
        foreach (var name in themeNames)
        {
            string rel = ThemePath(name);
            string path = Path.Combine([wpfFolder, .. rel.Split('/')]);
            if (!docs.TryGetValue(path, out var doc)) { errors.Add($"{rel} is missing (MediatorTheme.{name})"); continue; }
            if (doc.Root!.Name.LocalName != "ResourceDictionary") { errors.Add($"{rel}: root must be a ResourceDictionary"); continue; }
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            var defined = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in doc.Root.Descendants())
            {
                foreach (var attr in el.Attributes())
                    foreach (Match m in StaticResource().Matches(attr.Value))
                    {
                        resources++;
                        if (!defined.Contains(m.Groups[1].Value)) errors.Add($"{rel}:{Line(el)} StaticResource '{m.Groups[1].Value}' is not defined earlier in the theme");
                    }
                if (el.Parent == doc.Root && el.Attribute(X + "Key") is { } k)
                {
                    if (!keys.TryAdd(k.Value, KindOf(el))) errors.Add($"{rel}:{Line(el)} key '{k.Value}' is defined twice");
                    defined.Add(k.Value);
                }
            }
            themes[name] = keys;
        }
        var themeKeys = themes.Values.SelectMany(k => k.Keys).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, keys) in themes)
        {
            foreach (var missing in themeKeys.Where(k => !keys.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
                errors.Add($"{ThemePath(name)} does not define '{missing}' (another theme does)");
            foreach (var (key, kind) in keys)
                foreach (var (other, otherKeys) in themes)
                    if (string.CompareOrdinal(name, other) < 0 && otherKeys.TryGetValue(key, out var otherKind) && otherKind != kind)
                        errors.Add($"'{key}' is a {kind} in {name} but a {otherKind} in {other}");
        }
        // ── Pack URIs (V8) ──
        int packUris = PackUris(wpfFolder, viewModels, themeEnum, themeNames, docs, code, errors);

        // ── App.xaml: StaticResource before use; merged default theme ──
        string appPath = Path.Combine(wpfFolder, "App.xaml");
        if (!docs.TryGetValue(appPath, out var app)) { errors.Add("App.xaml missing or malformed"); return new Report(errors, bindings, resources, themeKeys.Count, files); }
        var appKeys = new List<(string Key, XElement El)>();
        foreach (var el in app.Descendants()) if (el.Attribute(X + "Key") is { } k) appKeys.Add((k.Value, el));
        var merged = app.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute("Source") is not null).ToList();
        if (merged.Count != 1 || !Regex.IsMatch(merged[0].Attribute("Source")!.Value, @"^(?:Themes/\w+\.xaml|pack://application:,,,/\w+;component/Themes/\w+\.xaml)$"))
            errors.Add("App.xaml must merge exactly one theme dictionary (Themes/<name>.xaml, or its pack URI) as MergedDictionaries[0]");
        foreach (var dup in appKeys.Where(k => themeKeys.Contains(k.Key)))
            errors.Add($"App.xaml:{Line(dup.El)} defines theme key '{dup.Key}' (it would shadow every theme)");

        var order = app.Descendants().Select((e, i) => (e, i)).ToDictionary(t => t.e, t => t.i);
        foreach (var el in app.Descendants())
        {
            foreach (var value in el.Attributes().Select(a => a.Value).Concat(el.HasElements ? [] : [el.Value]))
            {
                foreach (Match m in StaticResource().Matches(value))
                {
                    resources++;
                    string key = m.Groups[1].Value;
                    if (themeKeys.Contains(key)) { errors.Add($"App.xaml:{Line(el)} StaticResource '{key}' is a theme key: use DynamicResource so theme switches apply"); continue; }
                    var def = appKeys.FirstOrDefault(k => k.Key == key);
                    if (def.El is null) errors.Add($"App.xaml:{Line(el)} StaticResource '{key}' is not defined");
                    else if (order[def.El] >= order[el] && !el.AncestorsAndSelf().Contains(def.El)) errors.Add($"App.xaml:{Line(el)} StaticResource '{key}' is used before it is defined");
                }
                foreach (Match m in DynamicResource().Matches(value))
                {
                    resources++;
                    if (!themeKeys.Contains(m.Groups[1].Value) && !appKeys.Any(k => k.Key == m.Groups[1].Value))
                        errors.Add($"App.xaml:{Line(el)} DynamicResource '{m.Groups[1].Value}' is not defined by the themes");
                }
            }
            if (el.Name.NamespaceName.StartsWith("clr-namespace:Mediator.Wpf", StringComparison.Ordinal) && !classes.Contains(el.Name.LocalName))
                errors.Add($"App.xaml:{Line(el)} local:{el.Name.LocalName} has no class in Mediator.Wpf");
        }
        ColourLiterals(app, "App.xaml", errors);

        // ── Windows and controls ──
        foreach (var (file, doc) in docs)
        {
            string name = Rel(file);
            if (file == appPath || name.StartsWith("Themes/", StringComparison.Ordinal)) continue;
            var root = doc.Root!;
            if (root.Name.LocalName == "ResourceDictionary") { errors.Add($"{name}: stray ResourceDictionary outside Themes/"); continue; }
            ColourLiterals(doc, name, errors);

            var localKeys = doc.Descendants().Select(e => e.Attribute(X + "Key")?.Value).Where(k => k is not null).ToHashSet();
            foreach (var el in doc.Descendants())
                foreach (var attr in el.Attributes())
                {
                    foreach (Match m in StaticResource().Matches(attr.Value))
                    {
                        resources++;
                        string key = m.Groups[1].Value;
                        if (themeKeys.Contains(key)) errors.Add($"{name}:{Line(el)} StaticResource '{key}' is a theme key: use DynamicResource so theme switches apply");
                        else if (!appKeys.Any(k => k.Key == key) && !localKeys.Contains(key))
                            errors.Add($"{name}:{Line(el)} StaticResource '{key}' is not defined");
                    }
                    foreach (Match m in DynamicResource().Matches(attr.Value))
                    {
                        resources++;
                        string key = m.Groups[1].Value;
                        foreach (var (theme, keys) in themes)
                            if (!keys.ContainsKey(key) && !appKeys.Any(k => k.Key == key) && !localKeys.Contains(key))
                                errors.Add($"{name}:{Line(el)} DynamicResource '{key}' is not defined by theme {theme}");
                    }
                }

            // Code-behind: dependency properties (for ElementName bindings to the root) and named elements it uses.
            string behind = File.Exists(file + ".cs") ? File.ReadAllText(file + ".cs") : "";
            var names = doc.Descendants().Select(e => e.Attribute(X + "Name")?.Value ?? e.Attribute("Name")?.Value).Where(n => n is not null).ToHashSet();
            string? rootName = root.Attribute(X + "Name")?.Value;

            string? design = root.Attribute(D + "DataContext")?.Value;
            var typeName = design is null ? null : Regex.Match(design, @"Type=\w+:(\w+)").Groups[1].Value;
            var rootType = typeName is null ? null : viewModels.GetTypes().FirstOrDefault(t => t.Name == typeName);
            if (design is not null && rootType is null) { errors.Add($"{name}: root d:DataContext DesignInstance type not found"); continue; }

            void Walk(XElement el, Type? context)
            {
                // A DataTemplate with a DataType (ws:ChatListItem) scopes its bindings to that view-model type, as WPF does.
                if (el.Name.LocalName == "DataTemplate" && el.Attribute("DataType")?.Value is { } dataType
                    && viewModels.GetTypes().FirstOrDefault(t => t.Name == dataType[(dataType.IndexOf(':') + 1)..]) is { } templateType)
                    context = templateType;
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
                    if (b.ElementName is { } target)
                    {
                        if (!names.Contains(target)) errors.Add($"{where}: ElementName '{target}' has no x:Name");
                        else if (target == rootName && root.Attribute(X + "Class") is not null)
                        {
                            string first = b.Path.Split('.')[0];
                            if (first.Length == 0 || !Regex.IsMatch(behind, $@"DependencyProperty\.Register\(\s*nameof\({first}\)|DependencyProperty\.Register\(\s*""{first}"""))
                                errors.Add($"{where}: '{first}' is not a dependency property registered in {Path.GetFileName(file)}.cs");
                        }
                        continue;
                    }
                    var resolved = Resolve(b, context, rootType, where, errors, out var prop);
                    if (prop is not null && attr.Name.LocalName.EndsWith("Command", StringComparison.Ordinal) && !typeof(ICommand).IsAssignableFrom(prop.PropertyType))
                        errors.Add($"{where}: {b.Path} is not an ICommand");
                    bool twoWayTarget = (el.Name.LocalName, attr.Name.LocalName) is ("TextBox", "Text") or ("CheckBox", "IsChecked") or ("ToggleButton", "IsChecked") or ("TabControl", "SelectedIndex");
                    if (twoWayTarget && !b.OneWay && prop is not null && prop.SetMethod is not { IsPublic: true })
                        errors.Add($"{where}: two-way binding to read-only {b.Path}");
                    if (attr.Name.LocalName == "ItemsSource") itemType = ElementType(resolved);
                }
                if (el.Name.NamespaceName.StartsWith("clr-namespace:Mediator.Wpf", StringComparison.Ordinal) && !el.Name.LocalName.Contains('.') && !classes.Contains(el.Name.LocalName))
                    errors.Add($"{name}:{Line(el)} {el.Name.LocalName} has no class in Mediator.Wpf");
                foreach (var child in el.Elements()) Walk(child, itemType ?? context);
            }
            Walk(root, rootType);

            foreach (Match m in Regex.Matches(behind, @"\b([A-Z]\w*)\.(?:Focus|SelectAll|ScrollIntoView|BeginAnimation|Opacity\b|Angle\b|ScaleX\b|ScaleY\b)"))
                if (!names.Contains(m.Groups[1].Value) && !IsKnownType(m.Groups[1].Value)) errors.Add($"{name}.cs uses {m.Groups[1].Value}, which has no x:Name");
            if (root.Attribute(X + "Class") is not null && !behind.Contains("InitializeComponent()", StringComparison.Ordinal)) errors.Add($"{name}.cs does not call InitializeComponent()");
        }
        return new Report(errors, bindings, resources, themeKeys.Count, files, packUris);
    }

    [GeneratedRegex(@"^pack://application:,,,/(?:(?<asm>[^;/]+);component/)?(?<path>[^?#]+)$")]
    private static partial Regex PackUriShape();

    [GeneratedRegex(@"pack://application:,,,/[^""'\s<>]+")]
    private static partial Regex PackUriLiteral();

    /// <summary>
    /// V8 pack-URI rules, so a theme can never fail with "Cannot locate resource" on Windows:
    ///  • ThemeSources.WpfAssemblyName equals &lt;AssemblyName&gt; in Mediator.Wpf.csproj;
    ///  • ThemeSources.PackUri(theme) is an absolute application pack URI to that assembly whose file is the theme's
    ///    ResourceDictionary, for every MediatorTheme;
    ///  • ThemeManager builds its Uri from ThemeSources.PackUri with UriKind.Absolute, and no code builds a relative
    ///    ";component/" URI;
    ///  • every pack:// literal and every relative Source="…" in the XAML resolves to a file of the project;
    ///  • the loose fallback exists: the csproj copies Themes\*.xaml next to the exe, and the theme files can be loaded
    ///    loose (no x:Class, every clr-namespace names its assembly).
    /// Returns the number of URIs checked.
    /// </summary>
    static int PackUris(string wpfFolder, Assembly viewModels, Type? themeEnum, string[] themeNames, Dictionary<string, XDocument> docs, string code, List<string> errors)
    {
        int count = 0;
        string csprojPath = Path.Combine(wpfFolder, "Mediator.Wpf.csproj");
        string csproj = File.Exists(csprojPath) ? File.ReadAllText(csprojPath) : "";
        string? assemblyName = Regex.Match(csproj, @"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>") is { Success: true } a ? a.Groups[1].Value : null;
        if (assemblyName is null) { errors.Add("Mediator.Wpf.csproj: <AssemblyName> not found (pack URIs name it)"); return count; }

        var sources = viewModels.GetType("Mediator.UI.ThemeSources");
        string? declared = sources?.GetField("WpfAssemblyName")?.GetRawConstantValue() as string;
        if (sources is null || declared is null) errors.Add("Mediator.UI.ThemeSources (WpfAssemblyName, PackUri) not found");
        else if (declared != assemblyName) errors.Add($"ThemeSources.WpfAssemblyName is '{declared}' but Mediator.Wpf.csproj builds '{assemblyName}': every theme pack URI would fail");

        // Resolves an application pack URI to a project file; null (and an error) when it cannot.
        string? ResolvePack(string uri, string where)
        {
            count++;
            var m = PackUriShape().Match(uri);
            if (!m.Success) { errors.Add($"{where}: '{uri}' is not an application pack URI"); return null; }
            if (m.Groups["asm"].Success && !m.Groups["asm"].Value.Equals(assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{where}: '{uri}' names assembly '{m.Groups["asm"].Value}', but the overlay is '{assemblyName}'");
                return null;
            }
            string file = Path.GetFullPath(Path.Combine(wpfFolder, m.Groups["path"].Value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(file)) { errors.Add($"{where}: '{uri}' points at {m.Groups["path"].Value}, which is not in the project"); return null; }
            return file;
        }

        var packUri = sources?.GetMethod("PackUri", BindingFlags.Public | BindingFlags.Static);
        if (themeEnum is not null && packUri is not null)
        {
            foreach (var name in themeNames)
            {
                string uri = (string)packUri.Invoke(null, [Enum.Parse(themeEnum, name)])!;
                if (!uri.StartsWith("pack://application:,,,/", StringComparison.Ordinal)) { errors.Add($"ThemeSources.PackUri({name}) = '{uri}' is not absolute"); continue; }
                if (ResolvePack(uri, $"ThemeSources.PackUri({name})") is { } file &&
                    (!docs.TryGetValue(file, out var doc) || doc.Root!.Name.LocalName != "ResourceDictionary" || Path.GetFileNameWithoutExtension(file) != name))
                    errors.Add($"ThemeSources.PackUri({name}) does not point at {ThemePath(name)}");
            }
        }

        // ThemeManager: absolute URI built from ThemeSources; nothing builds a relative component URI.
        if (!Regex.IsMatch(code, @"new\s*(?:Uri)?\(\s*ThemeSources\.PackUri\(\s*\w+\s*\)\s*,\s*UriKind\.Absolute\s*\)"))
            errors.Add("ThemeManager does not build its Uri from ThemeSources.PackUri(theme) with UriKind.Absolute");
        foreach (Match m in Regex.Matches(code, @"""/?[^""]*;component/[^""]*""\s*,\s*UriKind\.Relative"))
            errors.Add($"code builds a relative component URI ({m.Value}): use an absolute pack:// URI");

        // Every literal pack URI in code and XAML, and every relative Source in XAML.
        foreach (Match m in PackUriLiteral().Matches(code))
            if (!m.Value.Contains('{')) ResolvePack(m.Value, "code");
        foreach (var (file, doc) in docs)
        {
            string rel = Path.GetRelativePath(wpfFolder, file).Replace('\\', '/');
            foreach (var attr in doc.Descendants().SelectMany(e => e.Attributes()))
            {
                string v = attr.Value.Trim();
                if (v.StartsWith("pack://", StringComparison.Ordinal)) ResolvePack(v, $"{rel}:{Line(attr.Parent!)}");
                else if (attr.Name.LocalName == "Source" && attr.Parent!.Name.LocalName == "ResourceDictionary" && !v.StartsWith('{'))
                {
                    count++;
                    string target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, v.Replace('/', Path.DirectorySeparatorChar)));
                    if (!File.Exists(target)) errors.Add($"{rel}:{Line(attr.Parent!)} Source '{v}' is not in the project");
                }
            }
        }

        // Loose fallback: copied next to the exe, and loadable without compilation.
        if (!Regex.IsMatch(csproj, @"Include=""[^""]*Themes\\\*\*\\\*\.xaml""") || !csproj.Contains("'$(OutDir)Themes\\%(RecursiveDir)", StringComparison.Ordinal) ||
            !csproj.Contains("'$(PublishDir)Themes\\%(RecursiveDir)", StringComparison.Ordinal))
            errors.Add("Mediator.Wpf.csproj does not copy Themes\\**\\*.xaml to the build and publish output (ThemeManager's loose fallback)");
        if (Regex.IsMatch(csproj, @"<Page\s+Remove=""[^""]*Themes", RegexOptions.IgnoreCase))
            errors.Add("Mediator.Wpf.csproj removes Themes from Page: the dictionaries would not be compiled into the assembly");
        foreach (var name in themeNames)
        {
            if (!docs.TryGetValue(Path.Combine([wpfFolder, .. ThemePath(name).Split('/')]), out var doc)) continue;
            if (doc.Root!.Attribute(X + "Class") is not null) errors.Add($"{ThemePath(name)} has x:Class: it could not be loaded loose");
            foreach (var ns in doc.Root.Attributes().Where(at => at.IsNamespaceDeclaration && at.Value.StartsWith("clr-namespace:", StringComparison.Ordinal) && !at.Value.Contains(";assembly=", StringComparison.Ordinal)))
                errors.Add($"{ThemePath(name)}: xmlns {ns.Value} has no ;assembly=, so the loose fallback could not load it");
        }
        return count;
    }

    /// <summary>V18: "Themes/Dark.xaml", "Themes/Legacy/HoloHud.xaml" (from ThemeSources, as the app loads them).</summary>
    static string ThemePath(string name) => Mediator.UI.ThemeSources.RelativePath(Enum.Parse<Mediator.UI.MediatorTheme>(name));

    /// <summary>Type names that legitimately appear before .BeginAnimation and friends in code-behind.</summary>
    static bool IsKnownType(string s) => s is "RotateTransform" or "ScaleTransform" or "UIElement" or "Keyboard" or "FocusManager";

    /// <summary>Attribute Style="…" together with &lt;X.Style&gt; (or any property set twice) is a XAML load error.</summary>
    static void DuplicateProperties(XDocument doc, string name, List<string> errors)
    {
        foreach (var el in doc.Descendants())
        {
            string local = el.Name.LocalName;
            if (local.Contains('.')) continue;
            foreach (var child in el.Elements().Where(c => c.Name.LocalName.StartsWith(local + ".", StringComparison.Ordinal)))
            {
                string prop = child.Name.LocalName[(local.Length + 1)..];
                if (el.Attribute(prop) is not null) errors.Add($"{name}:{Line(el)} {local}.{prop} is set both as an attribute and as a property element");
            }
        }
    }

    static void ColourLiterals(XDocument doc, string name, List<string> errors)
    {
        foreach (var el in doc.Descendants())
            foreach (var attr in el.Attributes())
            {
                string v = attr.Value.Trim();
                if (HexColor().IsMatch(v) || (ColourProperties.Contains(attr.Name.LocalName) && NamedColours.Contains(v)))
                    errors.Add($"{name}:{Line(el)} colour literal {attr.Name.LocalName}=\"{v}\": use a theme brush (DynamicResource)");
            }
    }

    sealed record BindingInfo(string Path, bool AncestorWindow, bool OneWay, string? ElementName);

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
        string? element = null;
        foreach (var p in parts)
        {
            if (!p.Contains('=', StringComparison.Ordinal) && path.Length == 0) path = p;
            else if (p.StartsWith("Path=", StringComparison.Ordinal)) path = p[5..];
            else if (p.StartsWith("ElementName=", StringComparison.Ordinal)) element = p[12..].Trim();
            else if (p.StartsWith("RelativeSource=", StringComparison.Ordinal)) ancestor = p.Contains("AncestorType=Window", StringComparison.Ordinal);
            else if (p is "Mode=OneWay" or "Mode=OneTime") oneWay = true;
        }
        return new BindingInfo(path, ancestor, oneWay, element);
    }

    static Type? Resolve(BindingInfo b, Type? context, Type? rootType, string where, List<string> errors, out PropertyInfo? last)
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
