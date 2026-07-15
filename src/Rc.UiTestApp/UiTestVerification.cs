namespace Rc.UiTestApp;

/// <summary>Produces user-visible, automation-readable evidence for each acceptance action.</summary>
public sealed class UiTestVerification
{
    private int invokeCount;

    public string Current { get; private set; } = "ready";

    public void Reset()
    {
        invokeCount = 0;
        Current = "ready";
    }

    public void RecordInvoke()
    {
        invokeCount++;
        Current = $"invoke:{invokeCount}";
    }

    public void RecordValue(string value) => Current = $"value:{value}";

    public void RecordSelection(string value) => Current = $"selection:{value}";

    public void RecordDropdownState(bool expanded) => Current = expanded ? "dropdown:expanded" : "dropdown:collapsed";

    public void RecordTreeState(string node, bool expanded) => Current = $"tree:{node}:{(expanded ? "expanded" : "collapsed")}";

    public void RecordMouseMove(int x, int y) => Current = $"mouse:move:{x},{y}";

    public void RecordMouseDown(string button) => Current = $"mouse:down:{button}";

    public void RecordMouseDrag(int x, int y) => Current = $"mouse:drag:{x},{y}";

    public void RecordMouseUp(string button) => Current = $"mouse:up:{button}";

    public void RecordMouseWheel(string direction, int position) => Current = $"mouse:wheel:{direction}:{position}";

    public void RecordKeyboard(string value) => Current = $"keyboard:{value}";

    public void RecordClipboard(string value) => Current = $"clipboard:{value}";
}
