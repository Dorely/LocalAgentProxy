using LocalAgentProxy;

try
{
    if (args.FirstOrDefault() == "bridge") await Bridge.RunAsync();
    else return await Commands.RunAsync(args);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex is InvalidOperationException ? ex.Message : "LocalAgentProxy failed (" + ex.GetType().Name + "). No request data was logged.");
    return 1;
}
