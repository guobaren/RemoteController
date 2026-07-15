using System.Text.Json;
using Rc.UiAgent;
using Xunit;

namespace Rc.Agent.Tests.Ui;

public sealed class ChromiumDevToolsDocumentTests
{
    [Fact]
    public void CreatesBoundedSemanticSnapshotFromChromiumDom()
    {
        using var document = JsonDocument.Parse("""
            {
              "nodeId": 1, "nodeName": "#document", "localName": "", "nodeValue": "", "attributes": [],
              "children": [{
                "nodeId": 2, "nodeName": "BODY", "localName": "body", "nodeValue": "", "attributes": ["id", "main", "class", "article"],
                "children": [{ "nodeId": 3, "nodeName": "#text", "localName": "", "nodeValue": "Forecast for Hangzhou", "attributes": [] }]
              }]
            }
            """);

        var snapshot = ChromiumDevToolsDocument.CreateSnapshot(document.RootElement, 42, maximumDepth: 2, maximumElements: 10);

        var body = Assert.Single(snapshot.Children);
        Assert.Equal("DOM.body", body.ControlType);
        Assert.Equal("main", body.AutomationId);
        Assert.Equal("article", body.ClassName);
        Assert.Equal([42, 3], Assert.Single(body.Children).RuntimeId);
        Assert.Equal("Forecast for Hangzhou", body.Children[0].Name);
    }

    [Fact]
    public void StopsAtConfiguredDepthAndElementLimit()
    {
        using var document = JsonDocument.Parse("""
            { "nodeId": 1, "nodeName": "#document", "localName": "", "nodeValue": "", "attributes": [],
              "children": [{ "nodeId": 2, "nodeName": "DIV", "localName": "div", "nodeValue": "", "attributes": [],
              "children": [{ "nodeId": 3, "nodeName": "#text", "localName": "", "nodeValue": "hidden", "attributes": [] }] }] }
            """);

        var depthLimited = ChromiumDevToolsDocument.CreateSnapshot(document.RootElement, 1, maximumDepth: 1, maximumElements: 10);
        var countLimited = ChromiumDevToolsDocument.CreateSnapshot(document.RootElement, 1, maximumDepth: 2, maximumElements: 2);

        Assert.Empty(Assert.Single(depthLimited.Children).Children);
        Assert.Empty(Assert.Single(countLimited.Children).Children);
    }
}
