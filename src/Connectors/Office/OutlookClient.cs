using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Mediator.Connectors.Office;

/// <summary>Outlook could not do what was asked (not installed, no profile, item gone, ...). The message is for the user.</summary>
public sealed class OutlookException(string message) : Exception(message);

/// <summary>What the Outlook tools need; the real one drives desktop Outlook, tests use a fake.</summary>
public interface IOutlookClient
{
    /// <summary>Null when Outlook can be used; otherwise why not (shown in Connectors and returned to the model).</summary>
    string? Unavailable();
    JsonArray SearchMail(string folder, string? query, string? from, int days, bool unreadOnly, int max);
    JsonObject ReadMail(string id, int maxChars);
    /// <summary>Saves a draft in Drafts and returns its id. Never sends.</summary>
    string CreateDraft(string to, string? cc, string subject, string body, bool html, bool show);
    JsonArray Calendar(DateTime from, DateTime to, int max);
}

/// <summary>
/// V15: classic desktop Outlook (Office 2016+, OUTLOOK.EXE) through its COM automation, on a short-lived STA thread per
/// call. Mail is searched newest first (at most 1,000 items scanned), read by id, and drafted into Drafts; Mediator never
/// sends mail. The new Outlook for Windows (olk.exe) and Outlook on the web have no automation: they are reported as such.
/// </summary>
public sealed class OutlookClient : IOutlookClient
{
    private const int OlFolderSent = 5, OlFolderInbox = 6, OlFolderCalendar = 9, OlFolderDrafts = 16, OlMailItem = 0;
    private const int MaxScanned = 1000;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    public string? Unavailable()
    {
        if (!OperatingSystem.IsWindows()) return "Outlook automation needs Windows with desktop Outlook.";
        if (Type.GetTypeFromProgID("Outlook.Application") is null)
            return "Classic desktop Outlook is not installed. The new Outlook and Outlook on the web cannot be automated; install classic Outlook (Microsoft 365 apps) to use mail and calendar.";
        if (!HasProfile())
            return "Classic Outlook has no mail profile yet. Open classic Outlook once and add your account (if you use the new Outlook, switch the toggle off in its title bar).";
        return null;
    }

    /// <summary>A classic Outlook profile exists (HKCU\Software\Microsoft\Office\16.0\Outlook\Profiles\*).</summary>
    private static bool HasProfile()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Outlook\Profiles");
            return key is not null && key.SubKeyCount > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    private static int FolderId(string folder) => folder.ToLowerInvariant() switch
    {
        "inbox" or "" => OlFolderInbox,
        "sent" or "sent items" => OlFolderSent,
        "drafts" => OlFolderDrafts,
        _ => throw new OutlookException($"Unknown folder '{folder}' (inbox, sent, drafts)."),
    };

    /// <summary>Runs <paramref name="work"/> with the Outlook session on a dedicated STA thread (COM's rule for Office).</summary>
    private T OnSta<T>(Func<dynamic, dynamic, T> work)
    {
        if (Unavailable() is { } why) throw new OutlookException(why);
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            dynamic? app = null;
            try
            {
                if (!OperatingSystem.IsWindows()) throw new OutlookException("Outlook needs Windows.");
                app = Activator.CreateInstance(Type.GetTypeFromProgID("Outlook.Application")!)!;
                dynamic ns = app.GetNamespace("MAPI");
                result = work(app, ns);
            }
            catch (Exception ex) { error = ex; }
            finally { Release(app); }
        }) { IsBackground = true, Name = "Mediator Outlook" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(CallTimeout)) throw new OutlookException("Outlook did not answer within a minute. Is a dialog open in Outlook (sign-in, profile, security prompt)?");
        return error switch
        {
            null => result!,
            OutlookException o => throw o,
            COMException c => throw new OutlookException("Outlook refused: " + c.Message),
            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException b => throw new OutlookException("Outlook returned something unexpected: " + b.Message),
            _ => throw new OutlookException(error.Message),
        };
    }

    private static void Release(object? com)
    {
        if (OperatingSystem.IsWindows() && com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    private static string Clip(string? s, int max)
    {
        s = (s ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }

    private static string When(object? value) => value is DateTime d ? d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";

    private static bool Contains(string? haystack, string needle) => (haystack ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase);

    public JsonArray SearchMail(string folder, string? query, string? from, int days, bool unreadOnly, int max) => OnSta((app, ns) =>
    {
        int folderId = FolderId(folder);
        dynamic items = ns.GetDefaultFolder(folderId).Items;
        string dateField = folderId == OlFolderInbox ? "[ReceivedTime]" : "[LastModificationTime]";
        items.Sort(dateField, true);
        var since = DateTime.Now.AddDays(-Math.Clamp(days, 1, 3650));
        var found = new JsonArray();
        int scanned = 0;
        dynamic? item = items.GetFirst();
        while (item is not null && scanned++ < MaxScanned && found.Count < max)
        {
            try
            {
                if ((int)item.Class == 43) // olMail
                {
                    DateTime at = folderId == OlFolderInbox ? (DateTime)item.ReceivedTime : (DateTime)item.LastModificationTime;
                    if (at < since) break;
                    string subject = (string)(item.Subject ?? ""), sender = (string)(item.SenderName ?? ""), address = SenderAddress(item);
                    string preview = Clip((string)(item.Body ?? ""), 300);
                    bool ok = (!unreadOnly || (bool)item.UnRead)
                        && (string.IsNullOrWhiteSpace(from) || Contains(sender, from!) || Contains(address, from!))
                        && (string.IsNullOrWhiteSpace(query) || Contains(subject, query!) || Contains(preview, query!) || Contains(sender, query!));
                    if (ok)
                        found.Add(new JsonObject
                        {
                            ["id"] = (string)item.EntryID, ["date"] = When(at), ["from"] = sender, ["fromAddress"] = address, ["to"] = (string)(item.To ?? ""),
                            ["subject"] = subject, ["unread"] = (bool)item.UnRead, ["attachments"] = (int)item.Attachments.Count, ["preview"] = preview,
                        });
                }
            }
            catch (COMException) { /* a protected or broken item: skip it */ }
            finally { Release(item); }
            item = items.GetNext();
        }
        return found;
    });

    private static string SenderAddress(dynamic mail)
    {
        try
        {
            if ((string)(mail.SenderEmailType ?? "") == "EX")
            {
                dynamic? user = mail.Sender?.GetExchangeUser();
                if (user is not null) return (string)(user.PrimarySmtpAddress ?? "");
            }
            return (string)(mail.SenderEmailAddress ?? "");
        }
        catch (COMException) { return ""; }
    }

    public JsonObject ReadMail(string id, int maxChars) => OnSta((app, ns) =>
    {
        dynamic item;
        try { item = ns.GetItemFromID(id); }
        catch (COMException) { throw new OutlookException("That message was not found (moved or deleted). Search again for its new id."); }
        var names = new JsonArray();
        for (int i = 1; i <= (int)item.Attachments.Count; i++) names.Add((string)item.Attachments.Item(i).FileName);
        return new JsonObject
        {
            ["id"] = id, ["subject"] = (string)(item.Subject ?? ""), ["from"] = (string)(item.SenderName ?? ""), ["fromAddress"] = SenderAddress(item),
            ["to"] = (string)(item.To ?? ""), ["cc"] = (string)(item.CC ?? ""), ["date"] = When(item.ReceivedTime), ["attachments"] = names,
            ["body"] = Clip((string)(item.Body ?? ""), Math.Clamp(maxChars, 500, 50_000)),
        };
    });

    public string CreateDraft(string to, string? cc, string subject, string body, bool html, bool show) => OnSta((app, ns) =>
    {
        dynamic mail = app.CreateItem(OlMailItem);
        mail.To = to;
        if (!string.IsNullOrWhiteSpace(cc)) mail.CC = cc;
        mail.Subject = subject;
        if (html) mail.HTMLBody = body; else mail.Body = body;
        mail.Save(); // into Drafts; never Send()
        if (show) mail.Display(false);
        return (string)mail.EntryID;
    });

    public JsonArray Calendar(DateTime from, DateTime to, int max) => OnSta((app, ns) =>
    {
        dynamic items = ns.GetDefaultFolder(OlFolderCalendar).Items;
        items.IncludeRecurrences = true;
        items.Sort("[Start]");
        // Restrict takes dates in the user's own short format ("g"), as Outlook documents.
        string filter = $"[Start] < '{to.ToString("g", CultureInfo.CurrentCulture)}' AND [End] > '{from.ToString("g", CultureInfo.CurrentCulture)}'";
        dynamic restricted = items.Restrict(filter);
        var list = new JsonArray();
        foreach (dynamic appt in restricted)
        {
            if (list.Count >= max) break;
            if ((int)appt.Class != 26) continue; // olAppointment
            list.Add(new JsonObject
            {
                ["start"] = When(appt.Start), ["end"] = When(appt.End), ["subject"] = (string)(appt.Subject ?? ""), ["location"] = (string)(appt.Location ?? ""),
                ["organizer"] = (string)(appt.Organizer ?? ""), ["allDay"] = (bool)appt.AllDayEvent, ["recurring"] = (bool)appt.IsRecurring,
            });
        }
        return list;
    });

    /// <summary>A short, readable list of mails for the model.</summary>
    public static string Describe(JsonArray mails)
    {
        if (mails.Count == 0) return "No matching messages.";
        var sb = new StringBuilder();
        foreach (var m in mails)
            sb.Append($"- {m!["date"]} | {m["from"]} | {m["subject"]}{((bool?)m["unread"] == true ? " (unread)" : "")}{((int?)m["attachments"] > 0 ? $" [{m["attachments"]} attachment(s)]" : "")}\n  id: {m["id"]}\n  {m["preview"]}\n");
        return sb.ToString();
    }
}
