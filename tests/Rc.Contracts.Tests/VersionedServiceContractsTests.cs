using System.Reflection;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class VersionedServiceContractsTests
{
    [Theory]
    [InlineData("Rc.Contracts.IPairServiceV1", "PairAsync", "Rc.Contracts.PairRequest", "Rc.Contracts.PairResponse")]
    [InlineData("Rc.Contracts.IExecServiceV1", "StartAsync", "Rc.Contracts.ExecRequest", "Rc.Contracts.ExecResponse")]
    [InlineData("Rc.Contracts.IJobServiceV1", "ListAsync", "Rc.Contracts.JobListRequest", "Rc.Contracts.JobListResponse")]
    [InlineData("Rc.Contracts.IJobServiceV1", "GetAsync", "Rc.Contracts.JobRequest", "Rc.Contracts.JobResponse")]
    [InlineData("Rc.Contracts.IJobServiceV1", "ReadLogsAsync", "Rc.Contracts.JobLogReadRequest", "Rc.Contracts.JobLogReadResponse")]
    [InlineData("Rc.Contracts.IJobServiceV1", "FollowLogsAsync", "Rc.Contracts.JobLogFollowRequest", "Rc.Contracts.JobLogReadResponse")]
    [InlineData("Rc.Contracts.IJobServiceV1", "WriteStdinAsync", "Rc.Contracts.JobInputRequest", "Rc.Contracts.JobResponse")]
    [InlineData("Rc.Contracts.IJobServiceV1", "CloseStdinAsync", "Rc.Contracts.JobCloseInputRequest", "Rc.Contracts.JobResponse")]
    [InlineData("Rc.Contracts.IJobServiceV1", "WaitAsync", "Rc.Contracts.JobWaitRequest", "Rc.Contracts.JobResponse")]
    [InlineData("Rc.Contracts.IJobServiceV1", "CancelAsync", "Rc.Contracts.JobCancelRequest", "Rc.Contracts.JobResponse")]
    [InlineData("Rc.Contracts.IFileServiceV1", "GetManifestAsync", "Rc.Contracts.FileManifestRequest", "Rc.Contracts.FileManifestResponse")]
    [InlineData("Rc.Contracts.IFileServiceV1", "ListAsync", "Rc.Contracts.FileListRequest", "Rc.Contracts.FileListResponse")]
    [InlineData("Rc.Contracts.IFileServiceV1", "StatAsync", "Rc.Contracts.FileStatRequest", "Rc.Contracts.FileStatResponse")]
    [InlineData("Rc.Contracts.IFileServiceV1", "ReadAsync", "Rc.Contracts.FileReadRequest", "Rc.Contracts.FileReadResponse")]
    [InlineData("Rc.Contracts.IFileServiceV1", "WriteAsync", "Rc.Contracts.FileWriteRequest", "Rc.Contracts.FileWriteResponse")]
    [InlineData("Rc.Contracts.IFileServiceV1", "StartTransferAsync", "Rc.Contracts.TransferStartRequest", "Rc.Contracts.TransferStartResponse")]
    [InlineData("Rc.Contracts.IFileServiceV1", "WriteTransferChunkAsync", "Rc.Contracts.TransferWriteChunkRequest", "Rc.Contracts.TransferWriteChunkResponse")]
    [InlineData("Rc.Contracts.IFileServiceV1", "ReadTransferChunkAsync", "Rc.Contracts.TransferReadChunkRequest", "Rc.Contracts.TransferReadChunkResponse")]
    [InlineData("Rc.Contracts.IFileServiceV1", "CompleteTransferAsync", "Rc.Contracts.TransferCompleteRequest", "Rc.Contracts.TransferCompleteResponse")]
    [InlineData("Rc.Contracts.IFileServiceV1", "GetTransferStatusAsync", "Rc.Contracts.TransferStatusRequest", "Rc.Contracts.TransferStatusResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "GetStatusAsync", "Rc.Contracts.UiStatusRequest", "Rc.Contracts.UiStatusResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "GetDisplaysAsync", "Rc.Contracts.UiDisplaysRequest", "Rc.Contracts.UiDisplaysResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "GetWindowsAsync", "Rc.Contracts.UiWindowsRequest", "Rc.Contracts.UiWindowsResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "GetSnapshotAsync", "Rc.Contracts.UiSnapshotRequest", "Rc.Contracts.UiSnapshotResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "CaptureScreenshotAsync", "Rc.Contracts.UiScreenshotRequest", "Rc.Contracts.UiScreenshotResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "ActOnWindowAsync", "Rc.Contracts.UiWindowActionRequest", "Rc.Contracts.UiWindowActionResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "MoveWindowAsync", "Rc.Contracts.UiMoveWindowRequest", "Rc.Contracts.UiMoveWindowResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "MoveMouseAsync", "Rc.Contracts.UiMouseMoveRequest", "Rc.Contracts.UiSnapshotResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "SetMouseButtonAsync", "Rc.Contracts.UiMouseButtonRequest", "Rc.Contracts.UiSnapshotResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "ScrollMouseAsync", "Rc.Contracts.UiMouseWheelRequest", "Rc.Contracts.UiSnapshotResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "SetKeysAsync", "Rc.Contracts.UiKeyRequest", "Rc.Contracts.UiSnapshotResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "TypeTextAsync", "Rc.Contracts.UiTextRequest", "Rc.Contracts.UiSnapshotResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "ReadClipboardAsync", "Rc.Contracts.UiClipboardReadRequest", "Rc.Contracts.UiClipboardReadResponse")]
    [InlineData("Rc.Contracts.IUiServiceV1", "WriteClipboardAsync", "Rc.Contracts.UiClipboardWriteRequest", "Rc.Contracts.UiClipboardWriteResponse")]
    public void VersionedServiceUsesAnExplicitRequestAndResponse(string serviceName, string methodName, string requestName, string responseName)
    {
        var assembly = Assembly.Load("Rc.Contracts");
        var service = assembly.GetType(serviceName);
        var request = assembly.GetType(requestName);
        var response = assembly.GetType(responseName);

        Assert.NotNull(service);
        Assert.NotNull(request);
        Assert.NotNull(response);

        var method = service!.GetMethod(methodName);
        Assert.NotNull(method);
        Assert.Equal(request, method!.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(ValueTask<>).MakeGenericType(response!), method.ReturnType);
    }
}