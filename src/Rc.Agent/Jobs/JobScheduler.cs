using System.Threading.Channels;
using Rc.Contracts;

namespace Rc.Agent.Jobs;

public sealed class JobScheduler : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly Channel<WorkItem> normalQueue = Channel.CreateUnbounded<WorkItem>();
    private readonly Channel<WorkItem> elevatedQueue = Channel.CreateUnbounded<WorkItem>();
    private readonly Task[] workers;

    public JobScheduler(int normalConcurrency, int elevatedConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(normalConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(elevatedConcurrency);
        workers = Enumerable.Range(0, normalConcurrency).Select(_ => RunAsync(normalQueue.Reader)).Concat(
            Enumerable.Range(0, elevatedConcurrency).Select(_ => RunAsync(elevatedQueue.Reader))).ToArray();
    }

    public Task EnqueueAsync(ExecutionIdentity identity, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!Enum.IsDefined(identity))
        {
            throw new ArgumentOutOfRangeException(nameof(identity));
        }

        var item = new WorkItem(work, cancellationToken);
        var queue = identity == ExecutionIdentity.ElevatedBroker ? elevatedQueue.Writer : normalQueue.Writer;
        if (!queue.TryWrite(item))
        {
            throw new InvalidOperationException("The job scheduler is stopping.");
        }

        return item.Completion.Task;
    }

    private async Task RunAsync(ChannelReader<WorkItem> reader)
    {
        await foreach (var item in reader.ReadAllAsync(stopping.Token).ConfigureAwait(false))
        {
            if (item.CancellationToken.IsCancellationRequested)
            {
                item.Completion.TrySetCanceled(item.CancellationToken);
                continue;
            }

            using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, item.CancellationToken);
            try
            {
                await item.Work(executionCancellation.Token).ConfigureAwait(false);
                item.Completion.TrySetResult();
            }
            catch (OperationCanceledException) when (item.CancellationToken.IsCancellationRequested)
            {
                item.Completion.TrySetCanceled(item.CancellationToken);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                item.Completion.TrySetCanceled(stopping.Token);
            }
            catch (Exception exception)
            {
                item.Completion.TrySetException(exception);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        normalQueue.Writer.TryComplete();
        elevatedQueue.Writer.TryComplete();
        stopping.Cancel();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        stopping.Dispose();
    }

    private sealed class WorkItem(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        public Func<CancellationToken, Task> Work { get; } = work;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
