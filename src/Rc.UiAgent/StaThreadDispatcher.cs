namespace Rc.UiAgent;

internal static class StaThreadDispatcher
{
    public static T Run<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        T? result = default;
        Exception? failure = null;
        using var complete = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { result = operation(); }
            catch (Exception exception) { failure = exception; }
            finally { complete.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        complete.Wait();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The UI automation operation failed.", failure);
        }
        return result!;
    }
}
