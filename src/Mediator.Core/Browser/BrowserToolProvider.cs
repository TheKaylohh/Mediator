using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Core.Bus;

namespace Mediator.Core.Browser;

/// <summary>
/// V13: the browser_* tools. Each call is one request to the Mediator for Chrome extension through
/// <see cref="ChromeBridgeServer"/>, so Claude Code and Antigravity jobs drive the user's Chrome the same way, through
/// the bus's grant, gate and audit like every other Mediator tool.
///
/// Token use is the point of the design: browser_read returns a compact text view ([ref_N] role "name" @(x,y)) and
/// can filter it (query) and page it (offset); clicks and typing take those refs, so most work needs no screenshot.
///
/// Risk: reading, scrolling, screenshots and console are ReadOnly. Navigating, clicking, typing, keys and closing a tab
/// are Write and <see cref="ToolDescriptor.Repeatable"/> (clicking the same "Next" twice is normal, so the bus must not
/// de-duplicate them); a Browser job grants them Auto, any other job would ask. browser_eval runs script in the
/// user's signed-in page and is Destructive: it is always asked, with the script shown.
/// </summary>
public sealed class BrowserToolProvider(ChromeBridgeServer? bridge) : IToolProvider
{
    public const string TabsTool = "browser_tabs";
    public const string NavigateTool = "browser_navigate";
    public const string ReadTool = "browser_read";
    public const string ClickTool = "browser_click";
    public const string TypeTool = "browser_type";
    public const string KeyTool = "browser_key";
    public const string ScrollTool = "browser_scroll";
    public const string ScreenshotTool = "browser_screenshot";
    public const string ConsoleTool = "browser_console";
    public const string CloseTabTool = "browser_close_tab";
    public const string EvalTool = "browser_eval";

    /// <summary>What a Browser job gets: everything except browser_eval (granted only when the user asks for scripts).</summary>
    public static readonly string[] JobTools = [TabsTool, NavigateTool, ReadTool, ClickTool, TypeTool, KeyTool, ScrollTool, ScreenshotTool, ConsoleTool, CloseTabTool];
    public static readonly string[] Names = [.. JobTools, EvalTool];
    /// <summary>The Write tools a Browser job runs without asking (the guidance forbids buying, sending or submitting unasked).</summary>
    public static readonly string[] AutoInBrowserJobs = [NavigateTool, ClickTool, TypeTool, KeyTool, CloseTabTool];

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    /// <summary>The rules every extension Browser job gets: work in one tab, read compactly, never commit anything unasked.</summary>
    public const string Guidance =
        "You operate the user's own Chrome (signed in as them) with the browser_* tools of the mediator MCP server. " +
        "You CAN browse: these tools open sites, read pages, click and type in the user's real browser, so never answer that you cannot " +
        "browse or use websites (even if an earlier answer in this chat said so); use the tools. " +
        "Start with browser_navigate (newTab true) to the site, or browser_tabs if the user means a tab that is already open; then work in that tab. " +
        "Read pages with browser_read: it lists text and every control as [ref_N] role \"name\" @(x,y). Act with refs: browser_click {ref}, " +
        "browser_type {ref, text} (also picks an option in a select). Pass query to browser_read to see only matching lines, and offset to read on. " +
        "Read again after anything that changes the page; refs from an old read may be gone. Use browser_screenshot only when the text view " +
        "cannot answer (a picture, a canvas, a layout problem): it is slow and costs far more. For a quiz or form: read the whole question " +
        "and all its options before choosing, select the answer, and verify the selection with a new read. " +
        "Never submit, buy, pay, send, post, delete or change account settings unless the user asked for exactly that; when they want to " +
        "review (\"leave it open\", \"so I can submit\"), stop before the final submit. If a site needs a login, a CAPTCHA or a permission, " +
        "stop and tell the user. Do not read, list or change local files. Finish with what you did, what you found, and which tab is open.";

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;
    private const string TabProp = "\"tabId\":{\"type\":\"integer\",\"description\":\"Tab id from browser_tabs or browser_navigate. Default: the tab used last.\"}";

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(TabsTool, "Lists the open Chrome tabs (id, title, url, active) and which one you used last.",
            Schema("""{"type":"object","properties":{},"additionalProperties":false}"""), ToolRisk.ReadOnly, null, DefaultTimeout),
        new(NavigateTool, "Opens a URL in Chrome and waits for it to load. newTab true opens a new tab (do this first); url \"back\", \"forward\" or \"reload\" act on the tab.",
            Schema($$"""{"type":"object","properties":{"url":{"type":"string"},"newTab":{"type":"boolean"},{{TabProp}}},"required":["url"],"additionalProperties":false}"""),
            ToolRisk.Write, null, TimeSpan.FromSeconds(60), Repeatable: true),
        new(ReadTool, "Reads the page as compact text: headings, text, and each control as [ref_N] role \"name\" (state) @(x,y). mode: full (default), interactive (controls and headings only), viewport (only what is on screen). query keeps only lines containing it. Long pages are cut at maxChars (default 12000); continue with offset.",
            Schema($$"""{"type":"object","properties":{"mode":{"type":"string","enum":["full","interactive","viewport"]},"query":{"type":"string"},"offset":{"type":"integer"},"maxChars":{"type":"integer"},{{TabProp}}},"additionalProperties":false}"""),
            ToolRisk.ReadOnly, null, DefaultTimeout),
        new(ClickTool, "Clicks a control by ref from browser_read (scrolled into view first), or at x,y viewport CSS pixels. Real mouse events. Returns what was clicked and the page after it.",
            Schema($$"""{"type":"object","properties":{"ref":{"type":"string"},"x":{"type":"number"},"y":{"type":"number"},"double":{"type":"boolean"},"button":{"type":"string","enum":["left","right","middle"]},{{TabProp}}},"additionalProperties":false}"""),
            ToolRisk.Write, null, DefaultTimeout, Repeatable: true),
        new(TypeTool, "Types text into a field by ref (its old text is replaced unless clear is false), or into the focused element without ref. For a select, picks the option with that text. submit true presses Enter afterwards.",
            Schema($$"""{"type":"object","properties":{"ref":{"type":"string"},"text":{"type":"string"},"clear":{"type":"boolean"},"submit":{"type":"boolean"},{{TabProp}}},"required":["text"],"additionalProperties":false}"""),
            ToolRisk.Write, null, DefaultTimeout, Repeatable: true),
        new(KeyTool, "Presses keys in the tab, space separated: Enter, Tab, Escape, Backspace, Delete, Space, ArrowUp/Down/Left/Right, PageUp, PageDown, Home, End, one character, with Ctrl+ Shift+ Alt+ (e.g. \"Ctrl+A Backspace\").",
            Schema($$"""{"type":"object","properties":{"keys":{"type":"string"},{{TabProp}}},"required":["keys"],"additionalProperties":false}"""),
            ToolRisk.Write, null, DefaultTimeout, Repeatable: true),
        new(ScrollTool, "Scrolls the page (direction up/down/left/right, amount in screens, default 0.8; x,y to scroll an inner panel under that point) or brings a ref into view.",
            Schema($$"""{"type":"object","properties":{"direction":{"type":"string","enum":["up","down","left","right"]},"amount":{"type":"number"},"ref":{"type":"string"},"x":{"type":"number"},"y":{"type":"number"},{{TabProp}}},"additionalProperties":false}"""),
            ToolRisk.ReadOnly, null, DefaultTimeout),
        new(ScreenshotTool, "A JPEG of the visible part of the tab, one image pixel per CSS pixel (so x,y can go to browser_click). Costly: prefer browser_read.",
            Schema($$"""{"type":"object","properties":{"quality":{"type":"integer"},{{TabProp}}},"additionalProperties":false}"""),
            ToolRisk.ReadOnly, null, DefaultTimeout),
        new(ConsoleTool, "The tab's console messages and errors since Mediator started listening on it (the first call starts listening).",
            Schema($$"""{"type":"object","properties":{"clear":{"type":"boolean"},{{TabProp}}},"additionalProperties":false}"""),
            ToolRisk.ReadOnly, null, DefaultTimeout),
        new(CloseTabTool, "Closes a tab you opened.",
            Schema($$"""{"type":"object","properties":{{{TabProp}}},"additionalProperties":false}"""),
            ToolRisk.Write, null, DefaultTimeout, Repeatable: true),
        new(EvalTool, "Runs a JavaScript expression in the page and returns its JSON value. The user approves every call.",
            Schema($$"""{"type":"object","properties":{"expression":{"type":"string"},{{TabProp}}},"required":["expression"],"additionalProperties":false}"""),
            ToolRisk.Destructive, null, DefaultTimeout),
    ];

    private static readonly Dictionary<string, string> Methods = new(StringComparer.Ordinal)
    {
        [TabsTool] = "get_tabs", [NavigateTool] = "navigate", [ReadTool] = "read_page", [ClickTool] = "click", [TypeTool] = "type_text",
        [KeyTool] = "press_key", [ScrollTool] = "scroll", [ScreenshotTool] = "screenshot", [ConsoleTool] = "read_console",
        [CloseTabTool] = "close_tab", [EvalTool] = "eval_js",
    };

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct)
    {
        if (call.Tool.Name != EvalTool) return Task.FromResult<JsonNode?>(null);
        string script = call.Arguments.TryGetProperty("expression", out var e) ? e.GetString() ?? "" : "";
        return Task.FromResult<JsonNode?>(new JsonObject
        {
            ["kind"] = "browser_eval",
            ["title"] = "Run a script in your Chrome tab",
            ["summary"] = "The script runs in the page as you (signed in). Approve only if you understand it.",
            ["selectable"] = false,
            ["rows"] = new JsonArray(new JsonObject { ["key"] = "script", ["primary"] = script.Length > 2000 ? script[..2000] + "…" : script, ["detail"] = "JavaScript" }),
        });
    }

    public async Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        if (bridge is null) return ToolResult.Fail("The Chrome extension is only available while the Mediator app (overlay) is running.");
        var parameters = JsonNode.Parse(call.Arguments.GetRawText()) as JsonObject ?? new JsonObject();
        JsonNode? result;
        try
        {
            result = await bridge.CallAsync(Methods[call.Tool.Name], parameters, call.Tool.ExecutionTimeout - TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch (BrowserBridgeException ex) { return ToolResult.Fail(ex.Message); }
        return Format(call.Tool.Name, result);
    }

    /// <summary>Turns the extension's answer into what the model sees: the page text as text, a screenshot as an image, the rest as short JSON.</summary>
    internal static ToolResult Format(string tool, JsonNode? result)
    {
        switch (tool)
        {
            case ReadTool:
                return ToolResult.Ok((string?)result?["text"] ?? "(empty page)");
            case ScreenshotTool:
                string data = (string?)result?["data"] ?? "";
                byte[] bytes;
                try { bytes = Convert.FromBase64String(data); }
                catch (FormatException) { return ToolResult.Fail("The screenshot could not be decoded."); }
                return ToolResult.Ok($"Screenshot of tab {result?["tabId"]}: {result?["width"]}x{result?["height"]} CSS pixels.") with
                {
                    Images = [new ToolImage((string?)result?["mediaType"] ?? "image/jpeg", bytes)],
                };
            case TabsTool when result is JsonObject list && list["tabs"] is JsonArray tabs:
                var lines = tabs.OfType<JsonObject>().Select(t =>
                    $"{t["tabId"]}{((bool?)t["active"] == true ? " (active)" : "")}  {((string?)t["title"])}  {((string?)t["url"])}");
                return ToolResult.Ok($"Last used tab: {list["workTabId"]?.ToJsonString() ?? "none"}\n" + string.Join("\n", lines));
            default:
                return ToolResult.Ok(result?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "done");
        }
    }
}
