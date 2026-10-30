using Mediator.CliHost;
using Mediator.Core.Connectors;

// mediator <command> [options]. Exit codes: 0 ok, 1 the job or a check failed, 2 usage error, 130 interrupted.
try
{
    var cli = CliArgs.Parse(args);
    // V18: the connectors installed next to mediator-cli (or, in a checkout, the connector projects' build output).
    ConnectorCatalog.Set(ConnectorLoader.LoadAll(log: new Mediator.Core.Diagnostics.StderrLog("mediator")).SelectMany(r => r.Connectors));
    if (ConnectorCatalog.Command(cli.Command) is { } command) return Commands.ConnectorCommand(command, cli);
    return cli.Command switch
    {
        "doctor" => await Commands.DoctorAsync(cli),
        "assemble" => await Commands.AssembleAsync(cli),
        "cad" or "resolve" or "blender" or "tutor" => await Commands.TaskAsync(cli),
        "connectors" => Commands.Connectors(cli),
        "organize" => await Commands.OrganizeAsync(cli),
        "rollback" => await Commands.RollbackAsync(cli),
        "operations" => await Commands.OperationsAsync(cli),
        "support-bundle" => await Commands.SupportBundleAsync(cli),
        "help" or "--help" or "-h" or "" => Commands.Help(),
        _ => Commands.Usage($"Unknown command '{cli.Command}'."),
    };
}
catch (UsageException ex)
{
    return Commands.Usage(ex.Message);
}
