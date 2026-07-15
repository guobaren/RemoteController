using Rc.Agent.Security;
using Rc.Agent.Tests.Persistence;
using Xunit;

namespace Rc.Agent.Tests.Security;

public sealed class LocalTlsHandshakeDiagnosticsFileTests
{
    [Fact]
    public void MostRecentTlsFailureCanBeReadAndCleared()
    {
        using var directory = new TemporaryDirectory();
        var exception = new IOException("The TLS server could not acquire a credential.");

        LocalTlsHandshakeDiagnosticsFile.Write(directory.Path, "authenticating the TLS server", exception);

        Assert.True(LocalTlsHandshakeDiagnosticsFile.TryRead(directory.Path, out var diagnostic));
        Assert.NotNull(diagnostic);
        Assert.Equal("authenticating the TLS server", diagnostic!.Stage);
        Assert.Equal(typeof(IOException).FullName, diagnostic.ExceptionType);
        Assert.Equal(exception.HResult, diagnostic.HResult);
        Assert.Equal(exception.Message, diagnostic.Message);

        LocalTlsHandshakeDiagnosticsFile.Clear(directory.Path);

        Assert.False(LocalTlsHandshakeDiagnosticsFile.TryRead(directory.Path, out _));
    }
}
