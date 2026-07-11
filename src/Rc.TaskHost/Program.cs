using System.Text.Json;
using Rc.Contracts;
using Rc.TaskHost;

if (args.Length != 2 || !string.Equals(args[0], "--request", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: rc-taskhost --request <task-launch-request.json>");
    return 64;
}

try
{
    var requestJson = await File.ReadAllTextAsync(args[1]);
    var request = JsonSerializer.Deserialize<TaskLaunchRequest>(requestJson, ContractJson.Options)
        ?? throw new InvalidDataException("Task launch request is empty.");
    await using var runner = new TaskHostRunner(request);
    var status = await runner.RunAsync();
    await PersistTerminalStatusAsync(request, status);
    Console.Out.WriteLine(JsonSerializer.Serialize(status, ContractJson.Options));
    return status.Job.ExitCode ?? 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task PersistTerminalStatusAsync(TaskLaunchRequest request, TaskRuntimeStatus status)
{
    var directory = Path.Combine(request.DataRoot, "task-status");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, request.JobId + ".json");
    var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
    await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(status, ContractJson.Options));
    File.Move(temporaryPath, path, overwrite: true);
}