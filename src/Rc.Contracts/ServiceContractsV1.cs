namespace Rc.Contracts;

public interface IPairServiceV1
{
    ValueTask<PairResponse> PairAsync(PairRequest request, CancellationToken cancellationToken = default);
}

public interface IExecServiceV1
{
    ValueTask<ExecResponse> StartAsync(ExecRequest request, CancellationToken cancellationToken = default);
}

public interface IJobServiceV1
{
    ValueTask<JobListResponse> ListAsync(JobListRequest request, CancellationToken cancellationToken = default);

    ValueTask<JobResponse> GetAsync(JobRequest request, CancellationToken cancellationToken = default);

    ValueTask<JobLogReadResponse> ReadLogsAsync(JobLogReadRequest request, CancellationToken cancellationToken = default);

    ValueTask<JobLogReadResponse> FollowLogsAsync(JobLogFollowRequest request, CancellationToken cancellationToken = default);

    ValueTask<JobResponse> WriteStdinAsync(JobInputRequest request, CancellationToken cancellationToken = default);

    ValueTask<JobResponse> CloseStdinAsync(JobCloseInputRequest request, CancellationToken cancellationToken = default);

    ValueTask<JobResponse> WaitAsync(JobWaitRequest request, CancellationToken cancellationToken = default);

    ValueTask<JobResponse> CancelAsync(JobCancelRequest request, CancellationToken cancellationToken = default);
}

public interface IFileServiceV1
{
    ValueTask<FileManifestResponse> GetManifestAsync(FileManifestRequest request, CancellationToken cancellationToken = default);

    ValueTask<FileListResponse> ListAsync(FileListRequest request, CancellationToken cancellationToken = default);

    ValueTask<FileStatResponse> StatAsync(FileStatRequest request, CancellationToken cancellationToken = default);

    ValueTask<FileReadResponse> ReadAsync(FileReadRequest request, CancellationToken cancellationToken = default);

    ValueTask<FileWriteResponse> WriteAsync(FileWriteRequest request, CancellationToken cancellationToken = default);

    ValueTask<TransferStartResponse> StartTransferAsync(TransferStartRequest request, CancellationToken cancellationToken = default);

    ValueTask<TransferWriteChunkResponse> WriteTransferChunkAsync(TransferWriteChunkRequest request, CancellationToken cancellationToken = default);

    ValueTask<TransferReadChunkResponse> ReadTransferChunkAsync(TransferReadChunkRequest request, CancellationToken cancellationToken = default);

    ValueTask<TransferCompleteResponse> CompleteTransferAsync(TransferCompleteRequest request, CancellationToken cancellationToken = default);

    ValueTask<TransferStatusResponse> GetTransferStatusAsync(TransferStatusRequest request, CancellationToken cancellationToken = default);
}

public interface IUiServiceV1
{
    ValueTask<UiStatusResponse> GetStatusAsync(UiStatusRequest request, CancellationToken cancellationToken = default);

    ValueTask<UiDisplaysResponse> GetDisplaysAsync(UiDisplaysRequest request, CancellationToken cancellationToken = default);

    ValueTask<UiWindowsResponse> GetWindowsAsync(UiWindowsRequest request, CancellationToken cancellationToken = default);

    ValueTask<UiSnapshotResponse> GetSnapshotAsync(UiSnapshotRequest request, CancellationToken cancellationToken = default);

    ValueTask<UiScreenshotResponse> CaptureScreenshotAsync(UiScreenshotRequest request, CancellationToken cancellationToken = default);

    ValueTask<UiWindowActionResponse> ActOnWindowAsync(UiWindowActionRequest request, CancellationToken cancellationToken = default);

    ValueTask<UiMoveWindowResponse> MoveWindowAsync(UiMoveWindowRequest request, CancellationToken cancellationToken = default);

    ValueTask<UiSnapshotResponse> MoveMouseAsync(UiMouseMoveRequest request, CancellationToken cancellationToken = default);

    ValueTask<UiSnapshotResponse> SetMouseButtonAsync(UiMouseButtonRequest request, CancellationToken cancellationToken = default);

    ValueTask<UiSnapshotResponse> ScrollMouseAsync(UiMouseWheelRequest request, CancellationToken cancellationToken = default);

    ValueTask<UiSnapshotResponse> SetKeysAsync(UiKeyRequest request, CancellationToken cancellationToken = default);

    ValueTask<UiSnapshotResponse> TypeTextAsync(UiTextRequest request, CancellationToken cancellationToken = default);

    ValueTask<UiClipboardReadResponse> ReadClipboardAsync(UiClipboardReadRequest request, CancellationToken cancellationToken = default);

    ValueTask<UiClipboardWriteResponse> WriteClipboardAsync(UiClipboardWriteRequest request, CancellationToken cancellationToken = default);
}