using System.Net.Sockets;
using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Protocol;

namespace Mediator.Tests;

static class TransportTests
{
    sealed class NoTools : IToolProvider
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } = [];
        public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct) => Task.FromResult<JsonNode?>(null);
        public Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct) => Task.FromResult(ToolResult.Fail("none"));
    }

    sealed class DenyAll : IApprovalPrompt
    {
        public Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct) => Task.FromResult(ApprovalResponse.Deny);
    }

    static async Task<(McpBusServer Bus, JobRegistry Jobs, AuditLog Audit)> Start()
    {
        var jobs = new JobRegistry();
        var tools = new ToolRegistry();
        tools.Register(new NoTools());
        string dir = Path.Combine(Path.GetTempPath(), "mediator-tests", Guid.NewGuid().ToString("N")[..8]);
        var audit = await AuditLog.OpenAsync(Path.Combine(dir, "audit.db"));
        var bus = new McpBusServer(new BusOptions { PipeName = "mediator-tcp-" + Guid.NewGuid().ToString("N")[..8] },
            jobs, tools, new PermissionGate(new DenyAll()), new LeaseManager(), audit);
        bus.Start();
        return (bus, jobs, audit);
    }

    static async Task<BusWelcome> HelloOverTcp(BusEndpoint ep, JobGrant grant)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(ep.Host, ep.Port);
        var stream = client.GetStream();
        var writer = new FrameWriter(stream);
        await writer.WriteAsync(new BusHello(BusHello.CurrentProtocolVersion, grant.JobId, grant.Token, Environment.ProcessId, "tcp").ToJson(), CancellationToken.None);
        var frame = await FrameIO.ReadFrameAsync(stream, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return BusWelcome.Parse(frame!);
    }

    public static async Task ListenerOnlyWhileWslJobsExist()
    {
        var (bus, jobs, audit) = await Start();
        await using (bus) await using (audit)
        {
            jobs.Issue("host-job", [], [], TimeSpan.FromMinutes(5));
            Check.True(bus.TcpEndpoint is null, "no TCP listener while only pipe jobs exist");

            var wsl = jobs.Issue("wsl-job", [], [], TimeSpan.FromMinutes(5), BusTransport.LoopbackTcp);
            var ep = bus.TcpEndpoint ?? throw new Exception("listener should start for a WSL job");
            Check.True(ep.Host == "127.0.0.1" && ep.Port > 0, "ephemeral loopback port");
            Check.True((await HelloOverTcp(ep, wsl)).Ok, "WSL job authenticates over TCP");

            jobs.Revoke("wsl-job");
            Check.True(bus.TcpEndpoint is null, "listener stops when the last WSL job ends");
            bool refused = false;
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(ep.Host, ep.Port);
            }
            catch (SocketException) { refused = true; }
            Check.True(refused, "port no longer accepts connections");
        }
    }

    public static async Task PipeTokenOverTcpRejectedAndAudited()
    {
        var (bus, jobs, audit) = await Start();
        await using (bus) await using (audit)
        {
            var pipeJob = jobs.Issue("pipe-only", [], [], TimeSpan.FromMinutes(5));
            jobs.Issue("wsl-job", [], [], TimeSpan.FromMinutes(5), BusTransport.LoopbackTcp);
            var welcome = await HelloOverTcp(bus.TcpEndpoint!, pipeJob);
            Check.True(!welcome.Ok && welcome.Error!.Contains("Transport mismatch"), "valid pipe token refused over TCP: " + welcome.Error);
            Check.Equal(1L, await audit.CountAsync("pipe-only"), "rejection written to the audit log");
        }
    }

    public static async Task IdleTcpSocketDropped()
    {
        var (bus, jobs, audit) = await Start();
        await using (bus) await using (audit)
        {
            jobs.Issue("wsl-job", [], [], TimeSpan.FromMinutes(5), BusTransport.LoopbackTcp);
            var ep = bus.TcpEndpoint!;
            using var idle = new TcpClient();
            await idle.ConnectAsync(ep.Host, ep.Port);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int n = await idle.GetStream().ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(6)); // returns 0 when the server closes
            Check.Equal(0, n, "server closed the idle socket");
            Check.True(clock.Elapsed > TimeSpan.FromSeconds(1.5) && clock.Elapsed < TimeSpan.FromSeconds(4), $"closed at the 2 s deadline (took {clock.Elapsed.TotalSeconds:0.0} s)");
        }
    }
}
