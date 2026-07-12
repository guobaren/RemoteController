using Rc.Contracts;

namespace Rc.Agent.Ui;

public sealed class UiSessionRegistry
{
    private readonly object gate = new();
    private UiAgentRegistration? registration;
    private DateTimeOffset registeredAtUtc;

    public void Register(UiAgentRegistration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Session.IsActive)
        {
            throw new InvalidOperationException("UiAgent must register an active user session.");
        }

        lock (gate)
        {
            registration = value;
            registeredAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public UiAgentRegistration? GetActive(TimeSpan maximumAge)
    {
        lock (gate)
        {
            return registration is not null && registeredAtUtc >= DateTimeOffset.UtcNow - maximumAge
                ? registration
                : null;
        }
    }
}
