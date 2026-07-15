using System.Windows.Forms;

namespace Rc.UiTestApp;

public sealed class UiTestForm : Form
{
    private readonly UiTestVerification verification = new();
    private readonly Label verificationLabel = CreateLabel("UiTestVerification", "Verification: ready");
    private readonly TextBox valueBox;
    private readonly ComboBox selectionBox;
    private readonly ListBox selectionList;
    private readonly TreeView tree;
    private readonly VScrollBar wheelIndicator;
    private readonly MouseSurface mouseSurface;
    private readonly TextBox keyboardBox;

    public UiTestForm()
    {
        Name = "UiTestWindow";
        Text = "RemoteController UI Acceptance Test";
        AccessibleName = Text;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 620);
        MinimumSize = new Size(640, 520);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 10,
            Padding = new Padding(16),
            AutoScroll = true,
            AccessibleName = "UI test controls",
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));

        var instructions = CreateLabel("UiTestInstructions", "This window is a safe RemoteController UI acceptance target. Each action updates Verification.");
        instructions.AutoSize = false;
        instructions.Height = 40;
        var resetButton = new Button { Name = "UiTestResetButton", Text = "Reset acceptance state", AccessibleName = "Reset acceptance state", AutoSize = true };
        resetButton.Click += (_, _) => ResetAcceptanceState();
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        header.Controls.Add(instructions);
        header.Controls.Add(resetButton);
        layout.Controls.Add(header, 0, 0);
        layout.SetColumnSpan(header, 2);

        var invokeButton = new Button { Name = "UiTestInvokeButton", Text = "Invoke test button", AccessibleName = "Invoke test button", AutoSize = true };
        invokeButton.Click += (_, _) => UpdateVerification(() => verification.RecordInvoke());
        AddRow(layout, 1, "Invoke", invokeButton);

        valueBox = new TextBox { Name = "UiTestValueBox", AccessibleName = "SetValue test input", Text = "initial", Dock = DockStyle.Fill };
        valueBox.TextChanged += (_, _) => UpdateVerification(() => verification.RecordValue(valueBox.Text));
        AddRow(layout, 2, "SetValue", valueBox);

        selectionBox = new ComboBox { Name = "UiTestSelectionBox", AccessibleName = "Selection test drop-down", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Left, Width = 260 };
        selectionBox.Items.AddRange(["Alpha", "Beta", "Gamma"]);
        selectionBox.SelectedIndexChanged += (_, _) => UpdateVerification(() => verification.RecordSelection(selectionBox.SelectedItem?.ToString() ?? string.Empty));
        selectionBox.DropDown += (_, _) => UpdateVerification(() => verification.RecordDropdownState(true));
        selectionBox.DropDownClosed += (_, _) => UpdateVerification(() => verification.RecordDropdownState(false));
        selectionBox.SelectedIndex = 0;
        AddRow(layout, 3, "Drop-down expand / collapse", selectionBox);

        selectionList = new ListBox { Name = "UiTestSelectionList", AccessibleName = "Selection test list", Dock = DockStyle.Left, Width = 260, Height = 64, SelectionMode = SelectionMode.One };
        selectionList.Items.AddRange(["Alpha", "Beta", "Gamma"]);
        selectionList.SelectedIndexChanged += (_, _) => UpdateVerification(() => verification.RecordSelection(selectionList.SelectedItem?.ToString() ?? string.Empty));
        selectionList.SelectedIndex = 0;
        AddRow(layout, 4, "Select", selectionList);

        tree = new TreeView { Name = "UiTestTree", AccessibleName = "Two-level expand collapse test tree", Dock = DockStyle.Left, Width = 300, Height = 100, HideSelection = false };
        var expandNode = tree.Nodes.Add("UiTestExpandNode", "Top expandable test node");
        var nestedNode = expandNode.Nodes.Add("UiTestNestedExpandNode", "Nested expandable test node");
        nestedNode.Nodes.Add("UiTestTreeLeaf", "Visible only after both nodes expand");
        tree.AfterExpand += (_, eventArgs) =>
        {
            if (eventArgs.Node?.Name == "UiTestExpandNode") UpdateVerification(() => verification.RecordTreeState("top", true));
            if (eventArgs.Node?.Name == "UiTestNestedExpandNode") UpdateVerification(() => verification.RecordTreeState("nested", true));
        };
        tree.AfterCollapse += (_, eventArgs) =>
        {
            if (eventArgs.Node?.Name == "UiTestNestedExpandNode") UpdateVerification(() => verification.RecordTreeState("nested", false));
            if (eventArgs.Node?.Name == "UiTestExpandNode") UpdateVerification(() => verification.RecordTreeState("top", false));
        };
        AddRow(layout, 5, "Expand both / collapse in sequence", tree);

        var mouseContainer = new Panel { Dock = DockStyle.Fill, Height = 110 };
        wheelIndicator = new VScrollBar { Name = "UiTestWheelScroll", AccessibleName = "Mouse wheel scroll position", Dock = DockStyle.Right, Minimum = 0, Maximum = 10, SmallChange = 1, LargeChange = 1, Value = 5 };
        mouseSurface = new MouseSurface { Name = "UiTestMouseSurface", AccessibleName = "Mouse test surface: red marker appears where a button is pressed", Dock = DockStyle.Fill, BackColor = Color.LightSteelBlue, BorderStyle = BorderStyle.FixedSingle };
        mouseSurface.MousePressed += (_, eventArgs) => UpdateVerification(() => verification.RecordMouseDown(eventArgs.Button.ToString().ToLowerInvariant()));
        mouseSurface.MouseDragged += (_, eventArgs) => UpdateVerification(() => verification.RecordMouseDrag(eventArgs.X, eventArgs.Y));
        mouseSurface.MouseReleased += (_, eventArgs) => UpdateVerification(() => verification.RecordMouseUp(eventArgs.Button.ToString().ToLowerInvariant()));
        mouseSurface.MouseWheelObserved += (_, eventArgs) =>
        {
            var direction = eventArgs.Delta > 0 ? "up" : "down";
            wheelIndicator.Value = Math.Clamp(wheelIndicator.Value - Math.Sign(eventArgs.Delta), wheelIndicator.Minimum, wheelIndicator.Maximum - wheelIndicator.LargeChange + 1);
            UpdateVerification(() => verification.RecordMouseWheel(direction, wheelIndicator.Value));
        };
        mouseContainer.Controls.Add(mouseSurface);
        mouseContainer.Controls.Add(wheelIndicator);
        AddRow(layout, 6, "Mouse: red press marker / drag / wheel", mouseContainer);

        keyboardBox = new TextBox { Name = "UiTestKeyboardBox", AccessibleName = "Keyboard key-down test (IME disabled)", Dock = DockStyle.Fill, ImeMode = ImeMode.Disable };
        // Do not use TextChanged as the acceptance signal. An IME may compose,
        // replace, or defer text while the underlying virtual key is delivered.
        keyboardBox.KeyDown += (_, eventArgs) => UpdateVerification(() => verification.RecordKeyboard($"key:{eventArgs.KeyCode}"));
        AddRow(layout, 7, "Keyboard", keyboardBox);

        var clipboardButton = new Button { Name = "UiTestClipboardButton", Text = "Read clipboard into verification", AccessibleName = "Read clipboard into verification", AutoSize = true };
        clipboardButton.Click += (_, _) => UpdateVerification(() => verification.RecordClipboard(Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty));
        AddRow(layout, 8, "Clipboard", clipboardButton);

        verificationLabel.AutoSize = false;
        verificationLabel.Dock = DockStyle.Fill;
        verificationLabel.Height = 40;
        verificationLabel.BorderStyle = BorderStyle.Fixed3D;
        layout.Controls.Add(verificationLabel, 0, 9);
        layout.SetColumnSpan(verificationLabel, 2);
        Controls.Add(layout);
    }

    private void UpdateVerification(Action action)
    {
        action();
        var value = $"Verification: {verification.Current}";
        verificationLabel.Text = value;
        verificationLabel.AccessibleName = value;
    }

    private void ResetAcceptanceState()
    {
        valueBox.Text = "initial";
        selectionBox.SelectedIndex = 0;
        selectionList.SelectedIndex = 0;
        tree.CollapseAll();
        wheelIndicator.Value = 5;
        keyboardBox.Clear();
        mouseSurface.ResetVisualState();
        verification.Reset();
        verificationLabel.Text = "Verification: ready";
        verificationLabel.AccessibleName = verificationLabel.Text;
    }

    private static void AddRow(TableLayoutPanel layout, int row, string caption, Control control)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(CreateLabel($"UiTestLabel{row}", caption), 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private static Label CreateLabel(string name, string text) => new()
    {
        Name = name,
        Text = text,
        AccessibleName = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 8, 8, 8),
    };

    private sealed class MouseSurface : Panel
    {
        private bool isPressed;
        private Point? pressedAt;
        private Point? lastDragPoint;

        public event EventHandler<MouseEventArgs>? MousePressed;
        public event EventHandler<MouseEventArgs>? MouseDragged;
        public event EventHandler<MouseEventArgs>? MouseReleased;
        public event EventHandler<MouseEventArgs>? MouseWheelObserved;

        public MouseSurface()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        public void ResetVisualState()
        {
            isPressed = false;
            pressedAt = null;
            lastDragPoint = null;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs eventArgs)
        {
            base.OnMouseMove(eventArgs);
            if (isPressed)
            {
                if (lastDragPoint is { } previous && Math.Abs(eventArgs.X - previous.X) <= 1 && Math.Abs(eventArgs.Y - previous.Y) <= 1)
                {
                    return;
                }
                lastDragPoint = eventArgs.Location;
                Invalidate();
                MouseDragged?.Invoke(this, eventArgs);
                return;
            }

            // Hover movement is intentionally not recorded: it can occur as a
            // side effect of focusing another control and would make an
            // unrelated assertion race with the test action. Pressed movement
            // remains observable through MouseDragged.
        }

        protected override void OnMouseDown(MouseEventArgs eventArgs)
        {
            Focus();
            isPressed = true;
            pressedAt = eventArgs.Location;
            lastDragPoint = eventArgs.Location;
            Invalidate();
            base.OnMouseDown(eventArgs);
            MousePressed?.Invoke(this, eventArgs);
        }

        protected override void OnMouseUp(MouseEventArgs eventArgs)
        {
            isPressed = false;
            lastDragPoint = eventArgs.Location;
            Invalidate();
            base.OnMouseUp(eventArgs);
            MouseReleased?.Invoke(this, eventArgs);
        }

        protected override void OnMouseWheel(MouseEventArgs eventArgs)
        {
            base.OnMouseWheel(eventArgs);
            MouseWheelObserved?.Invoke(this, eventArgs);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            if (pressedAt is not { } point)
            {
                return;
            }

            using var marker = new SolidBrush(Color.Red);
            eventArgs.Graphics.FillRectangle(marker, point.X - 3, point.Y - 3, 7, 7);
            if (lastDragPoint is { } drag && drag != point)
            {
                using var pen = new Pen(Color.Red, 2);
                eventArgs.Graphics.DrawLine(pen, point, drag);
            }
        }
    }
}
