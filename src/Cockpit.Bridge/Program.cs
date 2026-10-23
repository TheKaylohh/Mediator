using Cockpit.Bridge;
using Cockpit.Protocol;

BridgeOptions options;
try
{
    options = BridgeOptions.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"cockpit-mcp-bridge: {ex.Message}");
    Console.Error.WriteLine(BridgeOptions.Usage);
    return 2;
}

// Take raw stdio before anything can write to Console.Out; stray writes then land on stderr.
await using var stdio = StdioJsonLines.OpenProcessStdio();
var bridge = new CockpitMcpBridge(options, stdio, Console.Error);
return await bridge.RunAsync();
