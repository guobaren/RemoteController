using System.Text.Json;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class UiAgentCommandContractTests
{
    [Fact]
    public void UiAgentCommandRoundTripsItsOperationAndPolymorphicTarget()
    {
        var command = new UiAgentCommandRequest(
            1,
            UiOperationKinds.Screenshot,
            JsonSerializer.SerializeToElement(new UiScreenshotRequest(new DisplayTarget(2)), ContractJson.Options));

        var json = JsonSerializer.Serialize(command, ContractJson.Options);
        var restored = JsonSerializer.Deserialize<UiAgentCommandRequest>(json, ContractJson.Options);

        Assert.Equal("{\"protocolVersion\":1,\"operation\":\"screenshot\",\"request\":{\"target\":{\"kind\":\"display\",\"displayIndex\":2}}}", json);
        Assert.NotNull(restored);
        Assert.Equal(UiOperationKinds.Screenshot, restored!.Operation);
        Assert.IsType<DisplayTarget>(restored.Request.Deserialize<UiScreenshotRequest>(ContractJson.Options)!.Target);
    }

    [Theory]
    [InlineData(UiOperationKinds.Snapshot)]
    [InlineData(UiOperationKinds.ClipboardWrite)]
    [InlineData(UiOperationKinds.BrowserLaunch)]
    public void UiOperationKindsRecognizePublishedOperations(string operation)
    {
        Assert.True(UiOperationKinds.IsSupported(operation));
        Assert.False(UiOperationKinds.IsSupported("unknown"));
    }

    [Fact]
    public void AutomationRequestsDefensivelyCopyRuntimeIdsAndRequireValueForSetValue()
    {
        var runtimeId = new[] { 42, 7, 99 };
        var request = new UiAutomationActionRequest(new WindowTarget(123), runtimeId, UiAutomationAction.Focus);
        runtimeId[0] = -1;

        Assert.Equal(42, request.RuntimeId[0]);
        Assert.Throws<ArgumentException>(() => new UiAutomationActionRequest(new WindowTarget(123), [42], UiAutomationAction.SetValue));
    }

    [Fact]
    public void AutomationTreeRequestEnforcesBoundedTraversal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiAutomationTreeRequest(new WindowTarget(1), maximumDepth: 33));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiAutomationTreeRequest(new WindowTarget(1), maximumElements: 10_001));
        Assert.True(UiOperationKinds.IsSupported(UiOperationKinds.AutomationTree));
        Assert.True(UiOperationKinds.IsSupported(UiOperationKinds.AutomationAction));
    }

    [Fact]
    public void BrowserRequestsRequireBoundedHttpUrlsAndPreserveTargets()
    {
        var request = new UiBrowserNavigateRequest(new WindowTarget(123), "https://example.test/path?q=1");

        Assert.Equal(123, request.Target.WindowHandle);
        Assert.Equal("https://example.test/path?q=1", request.Url);
        Assert.Throws<ArgumentException>(() => new UiBrowserLaunchRequest(BrowserKind.Edge, "file:///C:/secret.txt"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiBrowserDomRequest(new WindowTarget(1), maximumDepth: 33));
    }
}
