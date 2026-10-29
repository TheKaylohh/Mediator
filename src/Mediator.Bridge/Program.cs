using Mediator.Bridge;
using Mediator.Protocol;

BridgeOptions options;
try
{
    options = BridgeOptions.Parse(args);
    // Do not leave the token in the environment of anything this process might start.
    Environment.SetEnvironmentVariable(BridgeOptions.TokenVariable, null);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"mediator-mcp-bridge: {ex.Message}");
    Console.Error.WriteLine(BridgeOptions.Usage);
    return 2;
}

// Take raw stdio before anything can write to Console.Out; stray writes then land on stderr.
await using var stdio = StdioJsonLines.OpenProcessStdio();
var bridge = new MediatorMcpBridge(options, stdio, Console.Error);
return await bridge.RunAsync();
