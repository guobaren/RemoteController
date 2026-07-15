using System.Windows;
using System.Windows.Automation;
using Rc.Contracts;

namespace Rc.UiAgent;

public static class UiAutomationController
{
    public static UiAutomationTreeResponse GetTree(UiAutomationTreeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StaThreadDispatcher.Run(() =>
        {
            var root = GetWindowRoot(request.Target);
            var remaining = request.MaximumElements;
            return new UiAutomationTreeResponse(CreateSnapshot(root, request.MaximumDepth, ref remaining));
        });
    }

    public static UiAutomationActionResponse Act(UiAutomationActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StaThreadDispatcher.Run(() =>
        {
            var root = GetWindowRoot(request.Target);
            var element = FindByRuntimeId(root, request.RuntimeId)
                ?? throw new InvalidOperationException("The requested UI Automation element is no longer available in the target window.");
            ExecuteAction(element, request);
            var remaining = 1;
            return new UiAutomationActionResponse(CreateSnapshot(element, 0, ref remaining));
        });
    }

    public static UiAutomationElementSnapshot GetBrowserDocument(WindowTarget target, int maximumDepth, int maximumElements)
    {
        return StaThreadDispatcher.Run(() =>
        {
            var root = GetWindowRoot(target);
            var document = FindDocument(root) ?? throw new InvalidOperationException("The browser has not exposed a web document yet.");
            var remaining = maximumElements;
            return CreateSnapshot(document, maximumDepth, ref remaining);
        });
    }

    private static AutomationElement GetWindowRoot(WindowTarget target)
    {
        if (!DesktopSnapshotProvider.IsInCurrentSession(target.WindowHandle))
        {
            throw new InvalidOperationException("The requested window is unavailable in this active UI session.");
        }
        var root = AutomationElement.FromHandle(new IntPtr(target.WindowHandle));
        if (root is null)
        {
            throw new InvalidOperationException("The requested window has no UI Automation root.");
        }
        return root;
    }

    private static UiAutomationElementSnapshot CreateSnapshot(AutomationElement element, int depth, ref int remaining)
    {
        if (remaining-- <= 0)
        {
            throw new InvalidOperationException("The UI Automation tree exceeded its configured element limit.");
        }

        var children = new List<UiAutomationElementSnapshot>();
        if (depth > 0)
        {
            for (var child = TreeWalker.RawViewWalker.GetFirstChild(element); child is not null; child = TreeWalker.RawViewWalker.GetNextSibling(child))
            {
                if (remaining == 0)
                {
                    break;
                }
                try
                {
                    children.Add(CreateSnapshot(child, depth - 1, ref remaining));
                }
                catch (ElementNotAvailableException)
                {
                }
            }
        }

        var current = element.Current;
        var bounds = current.BoundingRectangle;
        var runtimeId = element.GetRuntimeId();
        if (runtimeId is null || runtimeId.Length == 0)
        {
            throw new InvalidOperationException("The UI Automation element has no stable runtime ID.");
        }
        return new UiAutomationElementSnapshot(
            runtimeId,
            current.Name,
            current.AutomationId,
            current.ControlType?.ProgrammaticName ?? string.Empty,
            current.ClassName,
            current.NativeWindowHandle,
            ToInt(bounds.X),
            ToInt(bounds.Y),
            ToInt(bounds.Width),
            ToInt(bounds.Height),
            current.IsEnabled,
            current.IsOffscreen,
            children);
    }

    private static AutomationElement? FindByRuntimeId(AutomationElement root, IReadOnlyList<int> runtimeId)
    {
        if (root.GetRuntimeId().SequenceEqual(runtimeId))
        {
            return root;
        }
        for (var child = TreeWalker.RawViewWalker.GetFirstChild(root); child is not null; child = TreeWalker.RawViewWalker.GetNextSibling(child))
        {
            try
            {
                var match = FindByRuntimeId(child, runtimeId);
                if (match is not null)
                {
                    return match;
                }
            }
            catch (ElementNotAvailableException)
            {
            }
        }
        return null;
    }

    private static AutomationElement? FindDocument(AutomationElement root)
    {
        if (root.Current.ControlType == ControlType.Document)
        {
            return root;
        }
        for (var child = TreeWalker.RawViewWalker.GetFirstChild(root); child is not null; child = TreeWalker.RawViewWalker.GetNextSibling(child))
        {
            try
            {
                var match = FindDocument(child);
                if (match is not null)
                {
                    return match;
                }
            }
            catch (ElementNotAvailableException)
            {
            }
        }
        return null;
    }

    private static void ExecuteAction(AutomationElement element, UiAutomationActionRequest request)
    {
        switch (request.Action)
        {
            case UiAutomationAction.Focus:
                element.SetFocus();
                return;
            case UiAutomationAction.Invoke:
                GetPattern<InvokePattern>(element, InvokePattern.Pattern, "invoke").Invoke();
                return;
            case UiAutomationAction.SetValue:
                var value = GetPattern<ValuePattern>(element, ValuePattern.Pattern, "set a value");
                if (value.Current.IsReadOnly)
                {
                    throw new InvalidOperationException("The requested UI Automation element is read-only.");
                }
                value.SetValue(request.Value!);
                return;
            case UiAutomationAction.Select:
                GetPattern<SelectionItemPattern>(element, SelectionItemPattern.Pattern, "select").Select();
                return;
            case UiAutomationAction.Expand:
                GetPattern<ExpandCollapsePattern>(element, ExpandCollapsePattern.Pattern, "expand").Expand();
                return;
            case UiAutomationAction.Collapse:
                GetPattern<ExpandCollapsePattern>(element, ExpandCollapsePattern.Pattern, "collapse").Collapse();
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    private static TPattern GetPattern<TPattern>(AutomationElement element, AutomationPattern pattern, string operation)
        where TPattern : BasePattern
    {
        if (!element.TryGetCurrentPattern(pattern, out var value) || value is not TPattern typed)
        {
            throw new InvalidOperationException($"The requested UI Automation element does not support {operation}.");
        }
        return typed;
    }

    private static int ToInt(double value) => value switch
    {
        <= int.MinValue => int.MinValue,
        >= int.MaxValue => int.MaxValue,
        _ => (int)Math.Round(value),
    };
}
