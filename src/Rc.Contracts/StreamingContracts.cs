namespace Rc.Contracts;

public sealed record ByteChunk(string JobId, string Stream, long Offset, byte[] Data, bool IsFinal);

public enum JobState
{
    Queued,
    Running,
    Exited,
    FailedToStart,
    Cancelled,
    InterruptedByReboot,
}
