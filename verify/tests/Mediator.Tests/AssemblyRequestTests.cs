using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.Runtime;
using Mediator.Connectors.SolidWorks;
using Mediator.Connectors.SolidWorks.Contracts;

namespace Mediator.Tests;

/// <summary>V10: the assembly is saved under the name the user asks for; mates may use each part's default planes.</summary>
static class AssemblyRequestTests
{
    public static void RequestedNameAndPlaneIds()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mediator-tests", "asm-name");
        Check.Equal(Path.Combine(folder, "Train_Master_Assembly.SLDASM"),
            SolidWorksConnector.RequestedAssemblyPath("Create a new assembly file named Train_Master_Assembly.SLDASM. Insert the parts", folder), "a named file goes in the job's folder");
        Check.Equal(@"D:\Out\Car.sldasm", SolidWorksConnector.RequestedAssemblyPath(@"save it as D:\Out\Car.sldasm please", folder), "a full path is kept");
        Check.True(SolidWorksConnector.RequestedAssemblyPath("assemble the parts", folder) is null, "no name: the default is used");
        Check.True(SolidWorksConnector.Assemble(folder, LaunchMode.None, SolidWorksConnector.RequestedAssemblyPath("named Gearbox.SLDASM", folder)).Prompt.Contains("Gearbox.SLDASM", StringComparison.Ordinal)
            && !SolidWorksConnector.Assemble(folder, LaunchMode.None, SolidWorksConnector.RequestedAssemblyPath("named Gearbox.SLDASM", folder)).Prompt.Contains("Mediator-Assembly-", StringComparison.Ordinal),
            "the Assemble prompt asks for the user's name, not the default");
        foreach (var (text, index) in new[] { ("front", 0), ("Top Plane", 1), ("plane:right", 2), ("RIGHT", 2), ("p1", -1), ("c2", -1) })
            Check.Equal(index, StandardPlanes.IndexOf(text), "plane id " + text);
        Check.True(StandardPlanes.IsStandard(StandardPlanes.Face(1)) && StandardPlanes.Face(1).DirectionUnit[1] == 1, "Top is the XZ plane, normal +Y");
    }

    /// <summary>A plan mating default planes passes validation and reaches the SolidWorks backend as those planes.</summary>
    public static async Task DefaultPlaneMatesReachTheBackend(string bridgeDll)
    {
        string parts = Path.Combine(Path.GetTempPath(), "mediator-tests", "asm-planes-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(parts);
        string chassis = Path.Combine(parts, "Chassis.SLDPRT"), boiler = Path.Combine(parts, "Boiler.SLDPRT");
        File.WriteAllText(chassis, "part");
        File.WriteAllText(boiler, "part");
        var swx = new SimulatedSwxBackend();
        await using var runtime = await RuntimeTests.Runtime(bridgeDll, new AutoApprovalPrompt(), swx);
        string output = Path.Combine(parts, "Train_Master_Assembly.SLDASM");
        var job = await runtime.StartJobAsync(SolidWorksConnector.Assemble(parts, LaunchMode.None, output) with { JobId = "asm-planes" });
        await using var client = await runtime.ConnectClientAsync(job);
        Check.True(!(await client.CallToolAsync("swx_part_cards", new JsonObject { ["folder"] = parts }, TimeSpan.FromSeconds(30))).IsError, "cards");
        JsonObject Mate(string key, string a, string b, string type = "Coincident") => new()
            { ["key"] = key, ["type"] = type, ["componentA"] = "boiler", ["faceA"] = a, ["componentB"] = "chassis", ["faceB"] = b, ["confidence"] = 0.9 };
        var plan = new JsonObject
        {
            ["outputPath"] = output,
            ["components"] = new JsonArray(
                new JsonObject { ["key"] = "chassis", ["partPath"] = chassis, ["fixed"] = true },
                new JsonObject { ["key"] = "boiler", ["partPath"] = boiler }),
            ["mates"] = new JsonArray(Mate("centre-x", "right", "Right Plane"), Mate("centre-z", "front", "front"), Mate("seat", "p1", "top")),
        };
        var build = await client.CallToolAsync("swx_build_assembly", plan, TimeSpan.FromSeconds(30));
        Check.True(!build.IsError, "a plan with default planes is accepted: " + build.Text);
        var sent = swx.Builds.Single();
        Check.True(sent.OutputPath.EndsWith("Train_Master_Assembly.SLDASM", StringComparison.Ordinal), "saved under the requested name");
        Check.True(sent.Mates[0].FaceA.Id == "right" && StandardPlanes.IsStandard(sent.Mates[0].FaceB) && sent.Mates[2].FaceB.Id == "top" && !StandardPlanes.IsStandard(sent.Mates[2].FaceA),
            "default planes reach the backend as planes; card faces stay card faces");
        var bad = await client.CallToolAsync("swx_build_assembly", plan.DeepClone().AsObject() is var p2 && (p2["outputPath"] = Path.Combine(parts, "x.SLDASM")) is not null
            && (p2["mates"] = new JsonArray(Mate("m", "right", "c1", "Concentric"))) is not null ? p2 : p2, TimeSpan.FromSeconds(30));
        Check.True(bad.IsError && bad.Text.Contains("Concentric", StringComparison.Ordinal), "a plane is never accepted for Concentric");
        await runtime.EndJobAsync(job.JobId);
    }
}
