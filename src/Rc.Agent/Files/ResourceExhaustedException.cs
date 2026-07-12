namespace Rc.Agent.Files;

public sealed class ResourceExhaustedException : Exception
{
    public ResourceExhaustedException(string message)
        : base(message)
    {
    }

    public ResourceExhaustedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
