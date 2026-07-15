using System.Security.Cryptography;
using System.Text.Json;

using Rc.InteractiveTestApp;

var statePath = ResolveStatePath(args);
var historicalRunCount = await ReadRunCountAsync(statePath);
var challenge = RandomNumberGenerator.GetInt32(100000, 1000000);

Console.WriteLine($"HISTORICAL_RUN_COUNT: {historicalRunCount}");
Console.Write("Enter historical run count: ");
var historicalInput = Console.ReadLine();
Console.WriteLine($"CHALLENGE_NUMBER: {challenge}");
Console.Write("Enter challenge number: ");
var challengeInput = Console.ReadLine();

var result = InteractiveChallengeSession.Evaluate(historicalRunCount, historicalInput, challenge, challengeInput);
if (result.IsSuccess)
{
    await WriteRunCountAsync(statePath, result.NextRunCount);
}

Console.WriteLine($"RESULT: {result.Status}");
Console.WriteLine($"NEXT_RUN_COUNT: {result.NextRunCount}");
return result.IsSuccess ? 0 : 1;

static string ResolveStatePath(string[] arguments)
{
    if (arguments.Length == 0)
    {
        return Path.Combine(AppContext.BaseDirectory, "RemoteControllerInteractiveTest.state.json");
    }

    if (arguments.Length == 2 && string.Equals(arguments[0], "--state-file", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(arguments[1]))
    {
        return Path.GetFullPath(arguments[1]);
    }

    throw new ArgumentException("Usage: Rc.InteractiveTestApp.exe [--state-file <path>]");
}

static async Task<int> ReadRunCountAsync(string statePath)
{
    if (!File.Exists(statePath))
    {
        return 0;
    }

    await using var stream = File.OpenRead(statePath);
    var state = await JsonSerializer.DeserializeAsync<InteractiveTestState>(stream)
        ?? throw new InvalidDataException("The interactive test state file is invalid.");
    return state.SuccessfulRunCount >= 0
        ? state.SuccessfulRunCount
        : throw new InvalidDataException("The interactive test state file contains a negative run count.");
}

static async Task WriteRunCountAsync(string statePath, int runCount)
{
    Directory.CreateDirectory(Path.GetDirectoryName(statePath) ?? AppContext.BaseDirectory);
    var temporaryPath = statePath + ".tmp";
    await using (var stream = File.Create(temporaryPath))
    {
        await JsonSerializer.SerializeAsync(stream, new InteractiveTestState(runCount));
    }
    File.Move(temporaryPath, statePath, overwrite: true);
}

file sealed record InteractiveTestState(int SuccessfulRunCount);
