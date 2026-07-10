using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class DefensiveCopyTests
{
    [Fact]
    public void ExecRequestRetainsItsOwnArgvAfterSourceArrayChanges()
    {
        var argv = new[] { "tool.exe", "--safe" };
        var request = ExecRequest.ForDirectArgv(argv);
        argv[0] = "changed.exe";

        var json = JsonSerializer.Serialize(request, ContractJson.Options);

        Assert.Contains("tool.exe", json, StringComparison.Ordinal);
        Assert.DoesNotContain("changed.exe", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PairingDtosRetainCertificateBytesAfterSourceArraysChange()
    {
        var requestBytes = new byte[] { 1, 2 };
        var certificateBytes = new byte[] { 3, 4 };
        var authorityBytes = new byte[] { 5, 6 };
        var request = new PairRequest("controller", requestBytes);
        var response = new PairResponse("agent", certificateBytes, authorityBytes);
        requestBytes[0] = 9;
        certificateBytes[0] = 9;
        authorityBytes[0] = 9;

        var requestJson = JsonSerializer.Serialize(request, ContractJson.Options);
        var responseJson = JsonSerializer.Serialize(response, ContractJson.Options);

        Assert.Contains("AQI=", requestJson, StringComparison.Ordinal);
        Assert.Contains("AwQ=", responseJson, StringComparison.Ordinal);
        Assert.Contains("BQY=", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("CQI=", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("CQQ=", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("CQY=", responseJson, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotCollectionsRetainTheirContentsAfterSourceListsChange()
    {
        var entries = new List<FileManifestEntry>
        {
            new("first.txt", 1, DateTimeOffset.UnixEpoch, null),
        };
        var displays = new List<DisplaySnapshot>
        {
            new(0, "DISPLAY1", 0, 0, 100, 100, true),
        };
        var windows = new List<WindowSnapshot>();
        var manifest = new FileManifest("root", entries);
        var session = new UiSessionSnapshot(1, "user", true, displays, windows);
        entries.Add(new("injected.txt", 2, DateTimeOffset.UnixEpoch, null));
        displays.Add(new(1, "INJECTED", 0, 0, 100, 100, false));
        windows.Add(new(7, "Injected", "bad.exe", 2, 0, 0, 1, 1, true));

        var manifestJson = JsonSerializer.Serialize(manifest, ContractJson.Options);
        var sessionJson = JsonSerializer.Serialize(session, ContractJson.Options);

        Assert.DoesNotContain("injected.txt", manifestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("INJECTED", sessionJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Injected", sessionJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ChunksRetainDataAfterSourceArraysChange()
    {
        var assembly = Assembly.Load("Rc.Contracts");
        var fileChunkType = assembly.GetType("Rc.Contracts.FileChunk");
        var transferChunkType = assembly.GetType("Rc.Contracts.TransferChunk");
        var logChunkType = assembly.GetType("Rc.Contracts.ByteChunk");
        var streamType = assembly.GetType("Rc.Contracts.JobOutputKind");

        Assert.NotNull(fileChunkType);
        Assert.NotNull(transferChunkType);
        Assert.NotNull(logChunkType);
        Assert.NotNull(streamType);

        var fileData = new byte[] { 1, 2 };
        var transferData = new byte[] { 3, 4 };
        var logData = new byte[] { 5, 6 };
        var stdout = Enum.Parse(streamType!, "Stdout");
        var fileChunk = Activator.CreateInstance(fileChunkType!, ["transfer", "file.txt", 0L, fileData, true]);
        var transferChunk = Activator.CreateInstance(transferChunkType!, ["transfer", 0L, transferData, true]);
        var logChunk = Activator.CreateInstance(logChunkType!, ["job", stdout, 0L, logData, true]);
        fileData[0] = 9;
        transferData[0] = 9;
        logData[0] = 9;

        Assert.Contains("AQI=", JsonSerializer.Serialize(fileChunk, fileChunkType!, ContractJson.Options), StringComparison.Ordinal);
        Assert.Contains("AwQ=", JsonSerializer.Serialize(transferChunk, transferChunkType!, ContractJson.Options), StringComparison.Ordinal);
        Assert.Contains("BQY=", JsonSerializer.Serialize(logChunk, logChunkType!, ContractJson.Options), StringComparison.Ordinal);
    }
}
