using ServerApp.Core;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ServerApp.Cli <server-role-manifest.json>");
    return 2;
}

try
{
    var json = await File.ReadAllTextAsync(args[0]);
    var manifest = ServerRoleManifest.Parse(json);
    var coordinator = new ProvisioningCoordinator([new DiagnosticRoleAdapter()]);
    var result = await coordinator.ProvisionAsync(manifest);

    Console.WriteLine($"Instance: {result.InstanceId}");
    Console.WriteLine($"Role:     {result.RoleId}");
    Console.WriteLine($"State:    {result.State}");
    Console.WriteLine($"Detail:   {result.Detail}");

    foreach (var error in result.ValidationErrors)
        Console.Error.WriteLine($"{error.Code}: {error.Message}");

    return result.IsReady ? 0 : 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
    return 1;
}

internal sealed class DiagnosticRoleAdapter : IServerRoleAdapter
{
    public string RoleId => "diagnostic.ready";

    public ValueTask<RoleStartResult> StartAsync(
        ServerRoleManifest manifest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new RoleStartResult(
            true,
            $"Diagnostic role accepted instance '{manifest.InstanceId}' and reports ready. " +
            $"Declared capacity: {manifest.Resources.CpuCores} CPU core(s), {manifest.Resources.MemoryMiB} MiB."));
    }
}
