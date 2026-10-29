#if COCKPIT_WEBVIEW
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cockpit.UI;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Cockpit.Wpf;

/// <summary>
/// Full view's conversation as a web page: Markdown (marked) and LaTeX math (KaTeX: $..$, $$..$$, \(..\), \[..\]),
/// themed from the current WPF theme. Replaces the plain-text ScrollViewer; if WebView2 cannot start, that stays.
/// </summary>
internal sealed class ChatRenderer
{
    private readonly WebView2 _web = new();
    private readonly CockpitOverlayViewModel _vm;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _ready;

    private ChatRenderer(CockpitOverlayViewModel vm)
    {
        _vm = vm;
        _debounce.Tick += (_, _) => { _debounce.Stop(); Render(); };
    }

    public static async Task<bool> TryAttachAsync(ScrollViewer fallback, CockpitOverlayViewModel vm)
    {
        if (fallback.Parent is not Panel host) return false;
        var r = new ChatRenderer(vm);
        int index = host.Children.IndexOf(fallback);
        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiCockpit", "webview");
            r._web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = data };
            host.Children.RemoveAt(index);
            host.Children.Insert(index, r._web);
            await r._web.EnsureCoreWebView2Async();
        }
        catch (Exception)
        {
            host.Children.Remove(r._web);
            if (!host.Children.Contains(fallback)) host.Children.Insert(index, fallback);
            return false;
        }

        var core = r._web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("data:", StringComparison.Ordinal) || e.Uri == "about:blank") return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.NavigationCompleted += (_, _) => { r._ready = true; r.Render(); };
        core.WebMessageReceived += (_, e) => r.OnAction(e.WebMessageAsJson);

        vm.Conversation.CollectionChanged += r.OnConversationChanged;
        foreach (var t in vm.Conversation) r.Watch(t);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CockpitOverlayViewModel.CurrentTheme) or nameof(CockpitOverlayViewModel.IsChatBusy)) r.Schedule();
        };
        r._web.NavigateToString(Page);
        return true;
    }

    private static void OpenExternal(string uri)
    {
        if (uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { }
    }

    private void OnConversationChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (ChatTurnViewModel t in e.NewItems) Watch(t);
        Schedule();
    }

    private void Watch(ChatTurnViewModel t)
    {
        t.PropertyChanged += (_, _) => Schedule();
        t.Steps.CollectionChanged += (_, _) => Schedule();
    }

    private void Schedule() { if (!_debounce.IsEnabled) _debounce.Start(); }

    /// <summary>Message buttons in the page: {act: "copy" | "copyPrompt" | "edit" | "retry" | "copyCode", i, text?}.</summary>
    private void OnAction(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            string act = m.GetProperty("act").GetString() ?? "";
            int i = m.TryGetProperty("i", out var iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetInt32() : -1;
            var turn = i >= 0 && i < _vm.Conversation.Count ? _vm.Conversation[i] : null;
            switch (act)
            {
                case "copy" when turn is not null: ChatWindow.CopyText(turn.ResponseText); break;
                case "copyPrompt" when turn is not null: ChatWindow.CopyText(turn.UserPrompt); break;
                case "copyCode" when m.TryGetProperty("text", out var t): ChatWindow.CopyText(t.GetString() ?? ""); break;
                case "edit" when turn is not null: _vm.EditTurnCommand.Execute(turn); break;
                case "retry" when _vm.RetryCommand.CanExecute(null): _vm.RetryCommand.Execute(null); break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    private void Render()
    {
        if (!_ready || _web.CoreWebView2 is null) return;
        var turns = _vm.Conversation.Select(t => new
        {
            p = t.UserPrompt, b = t.BadgeText, s = t.Steps.ToArray(), r = t.ResponseText, run = t.IsRunning, th = t.ThinkingSummary, err = t.IsError,
        }).ToArray();
        var theme = new Dictionary<string, string>
        {
            ["bg"] = Css("WindowBackgroundBrush", "#0b1220"), ["panel"] = Css("PanelBrush", "#111a2b"), ["text"] = Css("TextBrush", "#e6edf6"),
            ["muted"] = Css("MutedBrush", "#8aa0b8"), ["accent"] = Css("AccentBrush", "#3fd0ff"), ["line"] = Css("LineBrush", "#22324a"),
        };
        _ = _web.CoreWebView2.ExecuteScriptAsync("render(" + JsonSerializer.Serialize(new { turns, theme, busy = _vm.IsChatBusy }) + ")");
    }

    /// <summary>A theme brush as CSS; gradients use their first stop.</summary>
    private static string Css(string key, string fallback)
    {
        Color? c = Application.Current?.TryFindResource(key) switch
        {
            SolidColorBrush s => s.Color,
            GradientBrush { GradientStops.Count: > 0 } g => g.GradientStops[0].Color,
            _ => null,
        };
        return c is { } v ? string.Create(CultureInfo.InvariantCulture, $"rgba({v.R},{v.G},{v.B},{v.A / 255.0:0.###})") : fallback;
    }

    private const string Page = """
<!doctype html><html><head><meta charset="utf-8">
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/katex.min.css">
<script src="https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/katex.min.js"></script>
<script src="https://cdn.jsdelivr.net/npm/marked@12.0.2/marked.min.js"></script>
<link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/styles/github-dark.min.css">
<script src="https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/highlight.min.js"></script>
<style>
:root{--bg:#0b1220;--panel:#111a2b;--text:#e6edf6;--muted:#8aa0b8;--accent:#3fd0ff;--line:#22324a}
html,body{margin:0;background:var(--bg);color:var(--text);font:15px/1.6 "Segoe UI",sans-serif}
main{max-width:860px;margin:0 auto;padding:24px 24px 8px}
.empty{color:var(--muted);text-align:center;margin-top:80px;font-size:16px}
.turn{margin-bottom:28px}
.user{margin-left:auto;max-width:640px;width:fit-content;background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:10px 14px;white-space:pre-wrap}
.badge{font:11px Consolas,monospace;color:var(--accent);margin:14px 0 2px;letter-spacing:.5px}
details{color:var(--muted);font-size:12px;margin-bottom:4px}
.thinking{color:var(--muted);font-style:italic}
.answer pre{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:10px;overflow:auto}
.answer code{font-family:Consolas,monospace;font-size:13px;background:var(--panel);padding:1px 4px;border-radius:4px}
.answer pre code{background:none;padding:0}
.answer table{border-collapse:collapse}.answer th,.answer td{border:1px solid var(--line);padding:4px 8px}
.answer hr{border:0;border-top:1px solid var(--line)}
.answer a{color:var(--accent)}
.katex-display{overflow-x:auto;overflow-y:hidden;padding:4px 0}
.err{color:#ff6b6b}
.acts{display:flex;gap:2px;margin-top:4px;opacity:0;transition:opacity .15s}
.turn:hover .acts,.turn.last .acts.a{opacity:1}
.acts.u{justify-content:flex-end}
.acts button,.code-head button{background:none;border:0;color:var(--muted);font:12px "Segoe UI",sans-serif;padding:3px 8px;border-radius:6px;cursor:pointer}
.acts button:hover,.code-head button:hover{background:var(--panel);color:var(--text)}
.code{border:1px solid var(--line);border-radius:8px;margin:10px 0;overflow:hidden}
.code-head{display:flex;justify-content:space-between;align-items:center;background:var(--panel);border-bottom:1px solid var(--line);padding:2px 4px 2px 10px;font:11px Consolas,monospace;color:var(--muted)}
.code pre{margin:0;border:0;border-radius:0}
button#down{position:fixed;bottom:16px;left:50%;transform:translateX(-50%);display:none;width:34px;height:34px;border-radius:17px;border:1px solid var(--line);background:var(--panel);color:var(--text);cursor:pointer;font-size:16px}
</style></head><body><main id="chat"></main>
<button id="down" title="Jump to the latest message" onclick="scrollTo({top:document.body.scrollHeight,behavior:'smooth'})">&#8595;</button><script>
function esc(s){return (s||'').replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));}
function md(src){
  src=src||'';
  if(!window.marked) return '<div style="white-space:pre-wrap">'+esc(src)+'</div>';
  const math=[];
  src=src.replace(/\$\$([\s\S]+?)\$\$|\\\[([\s\S]+?)\\\]|\\\(([\s\S]+?)\\\)|\$([^$\n]+?)\$/g,(m,a,b,c,d)=>{
    const tex=a??b??c??d, display=a!==undefined||b!==undefined; let h;
    try{h=window.katex?katex.renderToString(tex,{displayMode:display,throwOnError:false}):esc(m);}catch(e){h=esc(m);}
    math.push(h); return 'MATHX'+(math.length-1)+'XMATH';});
  return marked.parse(src).replace(/MATHX(\d+)XMATH/g,(m,i)=>math[+i]);
}
function send(act,i,text){window.chrome&&chrome.webview&&chrome.webview.postMessage({act:act,i:i,text:text});}
function flash(b,label){const o=b.textContent;b.textContent=label;setTimeout(()=>b.textContent=o,1200);}
const open=new Set(); // expanded step lists survive re-renders
function render(d){
  const r=document.documentElement.style; for(const k in d.theme) r.setProperty('--'+k,d.theme[k]);
  const atBottom=innerHeight+scrollY>=document.body.scrollHeight-80;
  const el=document.getElementById('chat');
  if(!d.turns.length){el.innerHTML='<div class="empty">Start a conversation below.</div>';return;}
  const n=d.turns.length;
  el.innerHTML=d.turns.map((t,i)=>'<div class="turn'+(i==n-1?' last':'')+'"><div class="user">'+esc(t.p)+'</div>'
    +'<div class="acts u"><button data-a="copyPrompt" data-i="'+i+'" title="Copy prompt">Copy</button>'
    +'<button data-a="edit" data-i="'+i+'" title="Edit: put this prompt back in the box">Edit</button></div>'
    +'<div class="badge">'+esc(t.b)+'</div>'
    +(t.s.length?'<details data-i="'+i+'"'+(open.has(i)?' open':'')+'><summary>'+t.s.length+' steps</summary>'+t.s.map(esc).join('<br>')+'</details>':'')
    +(t.run&&!t.r?'<div class="thinking">'+esc(t.th||'Working...')+'</div>':'')
    +'<div class="answer'+(t.err?' err':'')+'">'+md(t.r)+'</div>'
    +(t.run&&t.r?'<div class="thinking">'+esc(t.th||'Working...')+'</div>':'')
    +(t.r&&!t.run?'<div class="acts a"><button data-a="copy" data-i="'+i+'" title="Copy answer (Ctrl+Shift+C for the last one)">Copy</button>'
      +(i==n-1&&!d.busy?'<button data-a="retry" data-i="'+i+'" title="Run this prompt again">Retry</button>':'')+'</div>':'')
    +'</div>').join('');
  el.querySelectorAll('.answer pre>code').forEach(c=>{
    const pre=c.parentElement, lang=((c.className.match(/language-(\S+)/)||[])[1]||'code');
    if(window.hljs){try{hljs.highlightElement(c);}catch(e){}}
    const box=document.createElement('div'); box.className='code';
    box.innerHTML='<div class="code-head"><span>'+esc(lang)+'</span><button>Copy code</button></div>';
    pre.replaceWith(box); box.appendChild(pre);
    box.querySelector('button').onclick=e=>{send('copyCode',-1,c.innerText);flash(e.target,'Copied');};
  });
  el.querySelectorAll('details').forEach(x=>x.ontoggle=()=>x.open?open.add(+x.dataset.i):open.delete(+x.dataset.i));
  el.querySelectorAll('.acts button').forEach(b=>b.onclick=()=>{send(b.dataset.a,+b.dataset.i);if(b.dataset.a.startsWith('copy'))flash(b,'Copied');});
  if(atBottom) scrollTo(0,document.body.scrollHeight);
  updDown();
}
function updDown(){document.getElementById('down').style.display=innerHeight+scrollY<document.body.scrollHeight-200?'block':'none';}
addEventListener('scroll',updDown);
</script></body></html>
""";
}
#endif
