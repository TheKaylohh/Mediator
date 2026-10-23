using Cockpit.CliHost;

// cockpit <command> [options]. Exit codes: 0 ok, 1 the job or a check failed, 2 usage error, 130 interrupted.
try
{
    var cli = CliArgs.Parse(args);
    return cli.Command switch
    {
        "doctor" => await Commands.DoctorAsync(cli),
        "assemble" => await Commands.AssembleAsync(cli),
        "cad" or "resolve" or "blender" or "tutor" => await Commands.TaskAsync(cli),
        "matrix" => Commands.Matrix(cli),
        "organize" => await Commands.OrganizeAsync(cli),
        "rollback" => await Commands.RollbackAsync(cli),
        "operations" => await Commands.OperationsAsync(cli),
        "help" or "--help" or "-h" or "" => Commands.Help(),
        _ => Commands.Usage($"Unknown command '{cli.Command}'."),
    };
}
catch (UsageException ex)
{
    return Commands.Usage(ex.Message);
}
