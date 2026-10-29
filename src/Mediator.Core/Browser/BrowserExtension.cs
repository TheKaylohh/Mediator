using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Mediator.Core.Browser;

/// <summary>
/// V13: the "Mediator for Chrome" extension that ships with the app (chrome-extension\ next to Mediator.exe).
///
/// Installing it is one Chrome step the user does once ("Load unpacked"; Chrome allows no other install of an extension
/// that is not in the Web Store without admin policy). To make that step the same for everyone and survive app updates:
/// the bundled files are copied to %LOCALAPPDATA%\Mediator\chrome-extension (a fixed path Chrome keeps loading from),
/// refreshed on every start, and Mediator writes pairing.json there (bridge port + pairing secret). The manifest carries
/// a public key, so the extension id is the same on every PC and the bridge can admit exactly that extension.
/// </summary>
public static class BrowserExtension
{
    /// <summary>Fixed by the "key" in chrome-extension/manifest.json (sha256 of the public key, first 32 hex digits mapped to a-p).</summary>
    public const string Id = "dlgomnkpogggoofnllecblchidkecpfp";
    public const string Origin = "chrome-extension://" + Id;
    public const string FolderName = "chrome-extension";
    public const string PairingFile = "pairing.json";
    private const string TokenFile = "chrome-extension.token";
    private const string ConnectedMarker = "chrome-extension.connected";

    /// <summary>Where Chrome loads the extension from: %LOCALAPPDATA%\Mediator\chrome-extension.</summary>
    public static string InstallDirectory(string dataDirectory) => Path.Combine(dataDirectory, FolderName);

    /// <summary>
    /// The extension shipped with the app: chrome-extension\ next to the executable, or (development builds running from
    /// bin\Debug) the repository's chrome-extension folder. Null when neither has a manifest.json.
    /// </summary>
    public static string? BundledDirectory(string? baseDirectory = null)
    {
        var dir = new DirectoryInfo(baseDirectory ?? AppContext.BaseDirectory);
        for (int i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, FolderName);
            if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
        }
        return null;
    }

    /// <summary>The pairing secret: 32 random bytes as hex, created once per user and kept in the data folder.</summary>
    public static string LoadOrCreateToken(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        string file = Path.Combine(dataDirectory, TokenFile);
        try
        {
            string existing = File.ReadAllText(file).Trim();
            if (existing.Length == 64 && existing.All(Uri.IsHexDigit)) return existing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(file, token);
        return token;
    }

    /// <summary>
    /// Copies the bundled extension into <paramref name="target"/> (files that differ only; pairing.json is never
    /// copied) and removes files that are no longer shipped. Returns true when anything changed.
    /// </summary>
    public static bool Install(string source, string target)
    {
        Directory.CreateDirectory(target);
        bool changed = false;
        var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, file);
            if (rel.Equals(PairingFile, StringComparison.OrdinalIgnoreCase)) continue;
            shipped.Add(rel);
            string dest = Path.Combine(target, rel);
            if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(file).Length && File.ReadAllBytes(dest).AsSpan().SequenceEqual(File.ReadAllBytes(file))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            changed = true;
        }
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(target, file);
            if (rel.Equals(PairingFile, StringComparison.OrdinalIgnoreCase) || shipped.Contains(rel)) continue;
            File.Delete(file);
            changed = true;
        }
        return changed;
    }

    /// <summary>Writes pairing.json (port and secret) for the extension to read at each connection attempt.</summary>
    public static void WritePairing(string extensionDirectory, int port, string token)
    {
        Directory.CreateDirectory(extensionDirectory);
        var json = new JsonObject { ["app"] = "Mediator", ["port"] = port, ["token"] = token, ["written"] = DateTimeOffset.UtcNow.ToString("O") };
        string file = Path.Combine(extensionDirectory, PairingFile);
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, json.ToJsonString());
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>True once an extension has paired on this PC (so a Browser job can wait for it while Chrome starts).</summary>
    public static bool EverConnected(string dataDirectory) => File.Exists(Path.Combine(dataDirectory, ConnectedMarker));

    public static void MarkConnected(string dataDirectory)
    {
        try { File.WriteAllText(Path.Combine(dataDirectory, ConnectedMarker), DateTimeOffset.UtcNow.ToString("O")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The steps the user does once in Chrome, as shown in Connectors.</summary>
    public static string SetupSteps(string installDirectory) =>
        "In Chrome, type chrome://extensions in the address bar (Chrome does not let apps open that page): 1. turn on Developer mode (top right); "
        + "2. click Load unpacked; 3. paste the folder path (already copied) and press Select Folder: " + installDirectory + ". The Mediator icon then shows a green ON.";

    /// <summary>True when Google Chrome is installed.</summary>
    public static bool ChromeInstalled(Func<string, string?>? env = null) => ChromeSession.Locate(env ?? Environment.GetEnvironmentVariable) is not null;

    /// <summary>Chrome's user data folder (%LOCALAPPDATA%\Google\Chrome\User Data).</summary>
    public static string ChromeUserDataDirectory(Func<string, string?>? env = null) =>
        Path.Combine((env ?? Environment.GetEnvironmentVariable)("LOCALAPPDATA") ?? "", "Google", "Chrome", "User Data");

    /// <summary>
    /// The folders Chrome loads Mediator for Chrome from, one per profile that has it (read from each profile's
    /// Secure Preferences / Preferences; read only, best effort). Empty when no profile has it.
    /// </summary>
    public static IReadOnlyList<string> LoadedFrom(string chromeUserData)
    {
        var found = new List<string>();
        if (!Directory.Exists(chromeUserData)) return found;
        foreach (var profile in Directory.EnumerateDirectories(chromeUserData))
        {
            foreach (var name in new[] { "Secure Preferences", "Preferences" })
            {
                string file = Path.Combine(profile, name);
                if (!File.Exists(file)) continue;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (JsonNode.Parse(stream)?["extensions"]?["settings"]?[Id]?["path"] is JsonValue v && v.TryGetValue(out string? path)
                        && Path.IsPathFullyQualified(path) && !found.Contains(path, StringComparer.OrdinalIgnoreCase))
                        found.Add(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
            }
        }
        return found;
    }

    /// <summary>The first folder Chrome loads the extension from that is not <paramref name="installDirectory"/> (such a copy never pairs), or null.</summary>
    public static string? WrongFolder(string installDirectory, IEnumerable<string> loadedFrom)
    {
        string want = Normalize(installDirectory);
        return loadedFrom.FirstOrDefault(p => !string.Equals(Normalize(p), want, StringComparison.OrdinalIgnoreCase));
        static string Normalize(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>What Connectors says when Chrome loaded another copy of the extension (the repository's, or an old one).</summary>
    public static string WrongFolderSteps(string loadedFrom, string installDirectory) =>
        "Chrome loaded Mediator for Chrome from " + loadedFrom + ", which never pairs. In Chrome, type chrome://extensions in the address bar, remove Mediator for Chrome, "
        + "then click Load unpacked and select " + installDirectory + " (Set up copies this path).";
}

/// <summary>
/// V13: what the overlay owns for the extension: the bridge, the pairing secret and the installed copy. Created once at
/// start-up; <see cref="Prepare"/> (re)installs the files and writes pairing.json with the bridge's actual port.
/// </summary>
public sealed class BrowserExtensionHost : IAsyncDisposable
{
    private readonly string _token;

    private BrowserExtensionHost(ChromeBridgeServer bridge, string token, string dataDirectory, string? bundledDirectory)
    {
        Bridge = bridge;
        _token = token;
        DataDirectory = dataDirectory;
        InstallDirectory = BrowserExtension.InstallDirectory(dataDirectory);
        BundledDirectory = bundledDirectory;
    }

    public ChromeBridgeServer Bridge { get; }
    public string DataDirectory { get; }
    public string InstallDirectory { get; }
    /// <summary>The shipped extension files; null when this installation has none.</summary>
    public string? BundledDirectory { get; }
    public bool EverConnected => BrowserExtension.EverConnected(DataDirectory);

    /// <summary>Starts the bridge and prepares the extension folder. Null (and logged) when the bridge cannot start.</summary>
    public static BrowserExtensionHost? TryStart(string dataDirectory, Diagnostics.IMediatorLog? log = null, string? bundledDirectory = null, int firstPort = ChromeBridgeServer.DefaultPort)
    {
        try
        {
            string token = BrowserExtension.LoadOrCreateToken(dataDirectory);
            var bridge = new ChromeBridgeServer(token, BrowserExtension.Origin, log, firstPort);
            bridge.Start();
            var host = new BrowserExtensionHost(bridge, token, dataDirectory, bundledDirectory ?? BrowserExtension.BundledDirectory());
            if (host.Prepare() is { } problem) log?.Warn("Chrome extension: " + problem);
            return host;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log?.Warn("The Chrome extension bridge did not start: " + ex.Message);
            return null;
        }
    }

    /// <summary>Copies the shipped files into <see cref="InstallDirectory"/> and writes pairing.json. Returns a problem, or null.</summary>
    public string? Prepare()
    {
        try
        {
            if (BundledDirectory is not null) BrowserExtension.Install(BundledDirectory, InstallDirectory);
            else if (!File.Exists(Path.Combine(InstallDirectory, "manifest.json")))
                return "the extension files are missing from this installation (no chrome-extension folder next to Mediator.exe).";
            BrowserExtension.WritePairing(InstallDirectory, Bridge.Port, _token);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "the extension folder could not be written: " + ex.Message;
        }
    }

    public ValueTask DisposeAsync() => Bridge.DisposeAsync();
}
