using Rc.Agent.Configuration;
using Rc.Agent.Persistence;
using Rc.Agent.Tests.Persistence;
using Rc.Agent.Updates;
using Rc.Contracts;
using Xunit;

namespace Rc.Agent.Tests.Updates;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task CompleteStagesValidatedPackageAndStartsElevatedApplication()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var applier = new RecordingApplier();
        var service = new UpdateService(store, new AgentOptions
        {
            MaximumUpdateChunkBytes = 4,
            MaximumUpdatePackageBytes = 1024,
        }, applier);
        var files = RequiredFiles().Select((path, index) => new UpdatePackageFile(path, 1, Hash([(byte)index]))).ToArray();
        var request = new UpdateStartRequest(Guid.NewGuid(), new UpdatePackageManifest("RemoteController", "1.2.3", files));

        var started = await service.StartAsync(request);
        Assert.Equal(UpdateState.Receiving, started.State);
        foreach (var (file, index) in files.Select((file, index) => (file, index)))
        {
            await service.WriteChunkAsync(new UpdateWriteChunkRequest(request.UpdateId, file.RelativePath, 0, [(byte)index], file.Sha256));
        }

        var completed = await service.CompleteAsync(new UpdateCompleteRequest(request.UpdateId));

        Assert.Equal(UpdateState.Applying, completed.State);
        Assert.Equal("job-update", completed.InstallationJobId);
        Assert.NotNull(applier.PackagePath);
        Assert.True(File.Exists(Path.Combine(applier.PackagePath!, "Rc.Agent.exe")));

        await store.SaveJobSnapshotAsync(new JobSnapshot(
            "job-update", JobState.Exited, 0, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow, null, ExecutionIdentity.ElevatedBroker));

        var finished = await service.GetStatusAsync(new UpdateStatusRequest(request.UpdateId));
        Assert.Equal(UpdateState.Succeeded, finished.State);
    }

    [Fact]
    public async Task ChunkWithWrongHashIsRejectedWithoutWritingData()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new AgentStateStore(directory.Path);
        await store.InitializeAsync();
        var service = new UpdateService(store, new AgentOptions { MaximumUpdateChunkBytes = 4, MaximumUpdatePackageBytes = 1024 }, new RecordingApplier());
        var files = RequiredFiles().Select(path => new UpdatePackageFile(path, 1, Hash([0]))).ToArray();
        var request = new UpdateStartRequest(Guid.NewGuid(), new UpdatePackageManifest("RemoteController", "1.2.3", files));
        await service.StartAsync(request);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.WriteChunkAsync(
            new UpdateWriteChunkRequest(request.UpdateId, "Rc.Agent.exe", 0, [0], new string('A', 64))).AsTask());

        var status = await service.GetStatusAsync(new UpdateStatusRequest(request.UpdateId));
        Assert.Equal(0, status.ReceivedBytes);
    }

    private static string[] RequiredFiles() =>
    [
        "Install-RemoteController.ps1",
        "Update-RemoteController.ps1",
        "Rc.Agent.exe",
        "Rc.PrivilegedBroker.exe",
        "Rc.TaskHost.exe",
        "Rc.UiAgent.exe",
        "Rc.UiTestApp.exe",
        "Rc.InteractiveTestApp.exe",
        "Rc.Cli.exe",
    ];

    private static string Hash(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

    private sealed class RecordingApplier : IAgentUpdateApplier
    {
        public string? PackagePath { get; private set; }

        public Task<string> ApplyAsync(string packagePath, CancellationToken cancellationToken = default)
        {
            PackagePath = packagePath;
            return Task.FromResult("job-update");
        }
    }
}
