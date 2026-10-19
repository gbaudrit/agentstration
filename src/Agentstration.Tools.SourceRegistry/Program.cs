using Agentstration.Tools.SourceRegistry;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await SourceRegistryCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
