using System.Diagnostics;
using Rc.Agent.Tests.Persistence;
using Xunit;

namespace Rc.Agent.Tests;

public sealed class InstallationScriptTests
{
    [Fact]
    public async Task InstallAndUninstallWhatIfDoNotMutateTheMachineOrTemporaryPaths()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "publish");
        var install = Path.Combine(directory.Path, "install");
        var data = Path.Combine(directory.Path, "data");
        Directory.CreateDirectory(source);
        foreach (var file in new[] { "Rc.Agent.exe", "Rc.PrivilegedBroker.exe", "Rc.TaskHost.exe", "Rc.UiAgent.exe", "Rc.UiTestApp.exe", "Rc.InteractiveTestApp.exe", "Rc.Cli.exe" })
        {
            await File.WriteAllBytesAsync(Path.Combine(source, file), []);
        }

        var root = FindRepositoryRoot();
        await RunPowerShellAsync(Path.Combine(root, "scripts", "Install-RemoteController.ps1"),
            "-SourcePath", source, "-InstallPath", install, "-DataRoot", data, "-NoFirewallRule", "-WhatIf");
        await RunPowerShellAsync(Path.Combine(root, "scripts", "Uninstall-RemoteController.ps1"),
            "-InstallPath", install, "-DataRoot", data, "-KeepData", "-WhatIf");

        Assert.False(Directory.Exists(install));
        Assert.False(Directory.Exists(data));
    }

    [Fact]
    public async Task UpdateWhatIfDoesNotMutateTheMachineOrTemporaryPaths()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "publish");
        var install = Path.Combine(directory.Path, "install");
        var data = Path.Combine(directory.Path, "data");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "Install-RemoteController.ps1"), "param()");

        await RunPowerShellAsync(Path.Combine(FindRepositoryRoot(), "scripts", "Update-RemoteController.ps1"),
            "-SourcePath", source, "-InstallPath", install, "-DataRoot", data, "-NoFirewallRule", "-WhatIf");

        Assert.False(Directory.Exists(install));
        Assert.False(Directory.Exists(data));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rc.RemoteController.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static async Task RunPowerShellAsync(string script, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start PowerShell.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, $"PowerShell exited with {process.ExitCode}.\nSTDOUT:\n{output}\nSTDERR:\n{error}");
    }
}
