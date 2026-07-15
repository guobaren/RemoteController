using Rc.InteractiveTestApp;
using Xunit;

namespace Rc.TaskHost.Tests;

public sealed class InteractiveChallengeSessionTests
{
    [Fact]
    public void CorrectHistoryAndChallengeAdvanceThePersistentRunCount()
    {
        var result = InteractiveChallengeSession.Evaluate(7, "7", 456789, "456789");

        Assert.True(result.IsSuccess);
        Assert.Equal(8, result.NextRunCount);
        Assert.Equal("PASS", result.Status);
    }

    [Fact]
    public void IncorrectFirstInputDoesNotAdvanceThePersistentRunCount()
    {
        var result = InteractiveChallengeSession.Evaluate(7, "6", 456789, "456789");

        Assert.False(result.IsSuccess);
        Assert.Equal(7, result.NextRunCount);
        Assert.Equal("FIRST_INPUT_INVALID", result.Status);
    }

    [Fact]
    public void IncorrectSecondInputDoesNotAdvanceThePersistentRunCount()
    {
        var result = InteractiveChallengeSession.Evaluate(7, "7", 456789, "456788");

        Assert.False(result.IsSuccess);
        Assert.Equal(7, result.NextRunCount);
        Assert.Equal("SECOND_INPUT_INVALID", result.Status);
    }
}
