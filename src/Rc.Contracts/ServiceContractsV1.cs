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
    ValueTask<JobResponse> GetAsync(JobRequest request, CancellationToken cancellationToken = default);
}

public interface IFileServiceV1
{
    ValueTask<FileManifestResponse> GetManifestAsync(FileManifestRequest request, CancellationToken cancellationToken = default);

    ValueTask<FileReadResponse> ReadAsync(FileReadRequest request, CancellationToken cancellationToken = default);

    ValueTask<FileWriteResponse> WriteAsync(FileWriteRequest request, CancellationToken cancellationToken = default);
}

public interface IUiServiceV1
{
    ValueTask<UiSnapshotResponse> GetSnapshotAsync(UiSnapshotRequest request, CancellationToken cancellationToken = default);
}
