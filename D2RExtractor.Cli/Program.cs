using D2RExtractor.Cli;
using D2RExtractor.Services;

var cli = CommandLine.Parse(args);
if (cli.Error != null)
{
    Console.Error.WriteLine($"d2rextractor: {cli.Error}");
    Console.Error.WriteLine("Try 'd2rextractor help'.");
    return 2;
}

LoggingService.Initialize();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Cooperative: let the operation unwind so the manifest is left consistent.
    e.Cancel = true;
    Console.Error.WriteLine("\nInterrupted — finishing the current file, then stopping.");
    cts.Cancel();
};

try
{
    return cli.Command switch
    {
        "add"       => Commands.Add(cli),
        "list"      => Commands.List(),
        "forget"    => Commands.Forget(cli),
        "status"    => Commands.Status(cli),
        "extract"   => Commands.Extract(cli, cts.Token),
        "update"    => Commands.Update(cli, cts.Token),
        "undo"      => Commands.Undo(cli, cts.Token),
        "--version" => Commands.Version(),
        _           => Commands.Help(),
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"d2rextractor: {ex.Message}");
    LoggingService.Write($"CLI error: {ex}");
    return 1;
}
