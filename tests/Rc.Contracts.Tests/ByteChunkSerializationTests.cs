using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class ByteChunkSerializationTests
{
    [Fact]
    public void ByteChunkSerializesBytesAsBase64()
    {
        var contracts = Assembly.Load("Rc.Contracts");
        var chunkType = contracts.GetType("Rc.Contracts.ByteChunk");
        var streamType = contracts.GetType("Rc.Contracts.JobOutputKind");

        Assert.NotNull(chunkType);
        Assert.NotNull(streamType);

        var stdout = Enum.Parse(streamType!, "Stdout");
        var chunk = Activator.CreateInstance(chunkType!, ["job-42", stdout, 8L, new byte[] { 1, 2, 3 }, false]);
        var json = JsonSerializer.Serialize(chunk, chunkType!, ContractJson.Options);

        Assert.Equal("{\"jobId\":\"job-42\",\"stream\":\"stdout\",\"offset\":8,\"data\":\"AQID\",\"isFinal\":false}", json);
    }
}
