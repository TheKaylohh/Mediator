namespace Cockpit.Core.Diagnostics;

/// <summary>Minimal logging seam; the WPF host plugs in its own sink. Never log to stdout.</summary>
public interface ICockpitLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

public sealed class StderrLog : ICockpitLog
{
    private readonly string _prefix;
    public StderrLog(string prefix) => _prefix = prefix;
    public void Info(string message) => Console.Error.WriteLine($"{Now()} [{_prefix}] {message}");
    public void Warn(string message) => Console.Error.WriteLine($"{Now()} [{_prefix}] WARN {message}");
    public void Error(string message, Exception? exception = null) =>
        Console.Error.WriteLine($"{Now()} [{_prefix}] ERROR {message}{(exception is null ? "" : ": " + exception)}");
    private static string Now() => DateTime.UtcNow.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class NullLog : ICockpitLog
{
    public static readonly NullLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
