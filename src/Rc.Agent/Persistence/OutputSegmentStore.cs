using Microsoft.Data.Sqlite;
using Rc.Contracts;

namespace Rc.Agent.Persistence;

public sealed record OutputSegmentInfo(
    string SegmentId,
    string JobId,
    JobOutputKind Stream,
    string RelativePath,
    long StartOffset,
    long ByteLength,
    DateTimeOffset CreatedAtUtc);

public sealed record LogQuotaResult(long RemovedBytes, long RetainedBytes, IReadOnlyList<string> EvictedJobIds);

public sealed partial class AgentStateStore
{
    public async Task<OutputSegmentInfo> AppendOutputSegmentAsync(
        string jobId,
        JobOutputKind stream,
        long startOffset,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        await outputMutationGate.WaitAsync(cancellationToken);
        try
        {
            var segmentId = Guid.NewGuid().ToString("N");
            var relativePath = Path.Combine("segments", $"{segmentId}.seg");
            var fullPath = GetSegmentPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, data.ToArray(), cancellationToken);

            var createdAtUtc = DateTimeOffset.UtcNow;
            try
            {
                await using var connection = await OpenConnectionAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO output_segments (
                        segment_id, job_id, stream, relative_path, start_offset, byte_length, created_at_utc)
                    VALUES ($segmentId, $jobId, $stream, $relativePath, $startOffset, $byteLength, $createdAtUtc);
                    """;
                command.Parameters.AddWithValue("$segmentId", segmentId);
                command.Parameters.AddWithValue("$jobId", jobId);
                command.Parameters.AddWithValue("$stream", stream.ToString());
                command.Parameters.AddWithValue("$relativePath", relativePath);
                command.Parameters.AddWithValue("$startOffset", startOffset);
                command.Parameters.AddWithValue("$byteLength", data.Length);
                command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch
            {
                File.Delete(fullPath);
                throw;
            }

            return new OutputSegmentInfo(segmentId, jobId, stream, relativePath, startOffset, data.Length, createdAtUtc);
        }
        finally
        {
            outputMutationGate.Release();
        }
    }

    public async Task<LogQuotaResult> EnforceLogQuotaAsync(long quotaBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quotaBytes);
        await outputMutationGate.WaitAsync(cancellationToken);
        try
        {
            var retainedBytes = await GetStoredLogBytesAsync(cancellationToken);
            if (retainedBytes <= quotaBytes)
            {
                return new LogQuotaResult(0, retainedBytes, Array.Empty<string>());
            }

            var removedBytes = 0L;
            var evictedJobIds = new List<string>();
            foreach (var jobId in await GetCompletedJobIdsWithOutputAsync(cancellationToken))
            {
                if (retainedBytes <= quotaBytes)
                {
                    break;
                }

                var removedForJob = 0L;
                foreach (var segment in await GetOutputSegmentsAsync(jobId, cancellationToken))
                {
                    await DeleteOutputSegmentAsync(segment, cancellationToken);
                    removedForJob += segment.ByteLength;
                    removedBytes += segment.ByteLength;
                    retainedBytes -= segment.ByteLength;
                }

                if (removedForJob > 0)
                {
                    evictedJobIds.Add(jobId);
                }
            }

            if (retainedBytes > quotaBytes)
            {
                foreach (var segment in await GetRunningOutputSegmentsOldestFirstAsync(cancellationToken))
                {
                    if (retainedBytes <= quotaBytes)
                    {
                        break;
                    }

                    await DeleteOutputSegmentAsync(segment, cancellationToken);
                    removedBytes += segment.ByteLength;
                    retainedBytes -= segment.ByteLength;
                }
            }

            return new LogQuotaResult(removedBytes, retainedBytes, evictedJobIds);
        }
        finally
        {
            outputMutationGate.Release();
        }
    }

    private async Task<long> GetStoredLogBytesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(byte_length), 0) FROM output_segments;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<OutputSegmentInfo>> GetOutputSegmentsAsync(string jobId, CancellationToken cancellationToken)
    {
        var segments = new List<OutputSegmentInfo>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT segment_id, stream, relative_path, start_offset, byte_length, created_at_utc
            FROM output_segments
            WHERE job_id = $jobId
            ORDER BY created_at_utc, segment_id;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            segments.Add(new OutputSegmentInfo(
                reader.GetString(0),
                jobId,
                Enum.Parse<JobOutputKind>(reader.GetString(1), ignoreCase: false),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return segments;
    }

    private async Task<IReadOnlyList<string>> GetCompletedJobIdsWithOutputAsync(CancellationToken cancellationToken)
    {
        var jobIds = new List<string>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.job_id
            FROM job_snapshots AS j
            INNER JOIN output_segments AS s ON s.job_id = j.job_id
            WHERE j.state IN ('Exited', 'FailedToStart', 'Cancelled', 'InterruptedByReboot')
            GROUP BY j.job_id
            ORDER BY COALESCE(j.finished_at_utc, j.created_at_utc), j.job_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobIds.Add(reader.GetString(0));
        }

        return jobIds;
    }

    private async Task<IReadOnlyList<OutputSegmentInfo>> GetRunningOutputSegmentsOldestFirstAsync(CancellationToken cancellationToken)
    {
        var segments = new List<OutputSegmentInfo>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.segment_id, s.job_id, s.stream, s.relative_path, s.start_offset, s.byte_length, s.created_at_utc
            FROM output_segments AS s
            INNER JOIN job_snapshots AS j ON j.job_id = s.job_id
            WHERE j.state IN ('Queued', 'Running')
            ORDER BY s.created_at_utc, s.segment_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            segments.Add(new OutputSegmentInfo(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<JobOutputKind>(reader.GetString(2), ignoreCase: false),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return segments;
    }

    private async Task DeleteOutputSegmentAsync(OutputSegmentInfo segment, CancellationToken cancellationToken)
    {
        File.Delete(GetSegmentPath(segment.RelativePath));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM output_segments WHERE segment_id = $segmentId;";
        command.Parameters.AddWithValue("$segmentId", segment.SegmentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string GetSegmentPath(string relativePath)
    {
        var segmentsRoot = Path.GetFullPath(Path.Combine(DataRoot, "segments"));
        var fullPath = Path.GetFullPath(Path.Combine(DataRoot, relativePath));
        var prefix = segmentsRoot.EndsWith(Path.DirectorySeparatorChar)
            ? segmentsRoot
            : segmentsRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Output segment paths must remain within the segments root.");
        }

        return fullPath;
    }
}