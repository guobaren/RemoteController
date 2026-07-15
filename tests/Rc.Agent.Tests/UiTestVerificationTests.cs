using Rc.UiTestApp;
using Xunit;

namespace Rc.Agent.Tests;

public sealed class UiTestVerificationTests
{
    [Fact]
    public void InvokeRecordsAnObservableVerificationResult()
    {
        var verification = new UiTestVerification();

        verification.RecordInvoke();

        Assert.Equal("invoke:1", verification.Current);
    }

    [Fact]
    public void MouseAndNestedTreeActionsRecordDistinctObservableResults()
    {
        var verification = new UiTestVerification();

        verification.RecordTreeState("nested", expanded: true);
        Assert.Equal("tree:nested:expanded", verification.Current);

        verification.RecordMouseDown("left");
        Assert.Equal("mouse:down:left", verification.Current);

        verification.RecordMouseDrag(28, 43);
        Assert.Equal("mouse:drag:28,43", verification.Current);

        verification.RecordMouseUp("left");
        Assert.Equal("mouse:up:left", verification.Current);

        verification.RecordMouseWheel("up", 4);
        Assert.Equal("mouse:wheel:up:4", verification.Current);

        verification.RecordKeyboard("key:A");
        Assert.Equal("keyboard:key:A", verification.Current);
    }

    [Fact]
    public void ResetClearsTheVisibleStateAndInvokeCount()
    {
        var verification = new UiTestVerification();
        verification.RecordInvoke();

        verification.Reset();
        verification.RecordInvoke();

        Assert.Equal("invoke:1", verification.Current);
    }
}
