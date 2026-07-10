using System.Reflection;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class PublicContractShapeTests
{
    [Theory]
    [InlineData("Rc.Contracts.JobSnapshot")]
    [InlineData("Rc.Contracts.FileManifest")]
    [InlineData("Rc.Contracts.FileManifestEntry")]
    [InlineData("Rc.Contracts.DisplaySnapshot")]
    [InlineData("Rc.Contracts.WindowSnapshot")]
    [InlineData("Rc.Contracts.UiSessionSnapshot")]
    [InlineData("Rc.Contracts.PairRequest")]
    [InlineData("Rc.Contracts.PairResponse")]
    [InlineData("Rc.Contracts.ExecResponse")]
    [InlineData("Rc.Contracts.JobRequest")]
    [InlineData("Rc.Contracts.JobResponse")]
    [InlineData("Rc.Contracts.FileManifestRequest")]
    [InlineData("Rc.Contracts.FileManifestResponse")]
    [InlineData("Rc.Contracts.FileReadRequest")]
    [InlineData("Rc.Contracts.FileReadResponse")]
    [InlineData("Rc.Contracts.FileWriteRequest")]
    [InlineData("Rc.Contracts.FileWriteResponse")]
    [InlineData("Rc.Contracts.UiSnapshotRequest")]
    [InlineData("Rc.Contracts.UiSnapshotResponse")]
    public void RequiredContractDtoIsPubliclyAvailable(string fullTypeName)
    {
        var contractType = Assembly.Load("Rc.Contracts").GetType(fullTypeName);

        Assert.NotNull(contractType);
        Assert.True(contractType!.IsPublic);
    }
}
