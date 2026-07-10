using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class ChunkContractTests
{
    [Fact]
    public void FileAndTransferChunksDoNotUseTheJobLogChunkShape()
    {
        var assembly = Assembly.Load("Rc.Contracts");
        var fileChunkType = assembly.GetType("Rc.Contracts.FileChunk");
        var fileRangeChunkType = assembly.GetType("Rc.Contracts.FileRangeChunk");
        var transferChunkType = assembly.GetType("Rc.Contracts.TransferChunk");
        var fileReadResponseType = assembly.GetType("Rc.Contracts.FileReadResponse");

        Assert.NotNull(fileChunkType);
        Assert.NotNull(fileRangeChunkType);
        Assert.NotNull(transferChunkType);
        Assert.NotNull(fileReadResponseType);
        Assert.Equal(fileRangeChunkType, fileReadResponseType!.GetProperty("Chunk")?.PropertyType);

        var fileChunk = Activator.CreateInstance(fileChunkType!, ["transfer-7", "reports/data.bin", 4L, new byte[] { 4, 5 }, true]);
        var fileJson = JsonSerializer.Serialize(fileChunk, fileChunkType!, ContractJson.Options);
        Assert.Equal("{\"transferSessionId\":\"transfer-7\",\"relativePath\":\"reports/data.bin\",\"offset\":4,\"data\":\"BAU=\",\"isFinal\":true}", fileJson);

        var rangeChunk = Activator.CreateInstance(fileRangeChunkType!, ["reports/data.bin", 4L, new byte[] { 4, 5 }, true]);
        var rangeJson = JsonSerializer.Serialize(rangeChunk, fileRangeChunkType!, ContractJson.Options);
        Assert.Equal("{\"path\":\"reports/data.bin\",\"offset\":4,\"data\":\"BAU=\",\"isFinal\":true}", rangeJson);
        Assert.DoesNotContain("transferSessionId", rangeJson, StringComparison.Ordinal);
        Assert.DoesNotContain("jobId", fileJson, StringComparison.Ordinal);
        Assert.DoesNotContain("stream", fileJson, StringComparison.Ordinal);
    }
}