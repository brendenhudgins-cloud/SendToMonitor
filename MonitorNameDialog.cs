namespace SendToMonitor;

/// <summary>
/// "Name monitor 2" dialog. It opens centred on the monitor being named, so it's obvious which one
/// that is. A blank name goes back to the monitor's model name.
/// </summary>
internal sealed class MonitorNameDialog : Form
{
    public const int MaxNameLength = 40;

    private readonly TextBox _nameBox;
    private readonly RECT _workArea;
    private readonly Icon? _icon;

    public MonitorNameDialog(MonitorDesc monitor)
    {
        _workArea = monitor.WorkArea;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;
        Text = $"Name monitor {monitor.Number}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true; // the app has no other window, so this keeps the dialog findable
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.Manual;
        // Create the window on the monitor being named so it gets that monitor's DPI from the start;
        // OnLoad centres it once its size is known.
        Location = new Point(_workArea.Left + _workArea.Width / 3, _workArea.Top + _workArea.Height / 3);

        using (Stream? stream = typeof(MonitorNameDialog).Assembly.GetManifestResourceStream("SendToMonitor.app.ico"))
            if (stream != null) Icon = _icon = new Icon(stream);

        var details = new Label
        {
            Text = monitor.Details,
            AutoSize = true,
            UseMnemonic = false,
            MaximumSize = new Size(320, 0),
            Margin = new Padding(0, 0, 0, 8),
        };
        _nameBox = new TextBox
        {
            Text = monitor.CustomName ?? string.Empty,
            PlaceholderText = monitor.FriendlyName,
            MaxLength = MaxNameLength,
            Width = 320,
            Margin = new Padding(0, 0, 0, 4),
        };
        var hint = new Label
        {
            Text = $"Shown in the title bar and taskbar menus. Leave it blank to use \"{monitor.FriendlyName}\".",
            AutoSize = true,
            UseMnemonic = false,
            MaximumSize = new Size(320, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 12),
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 0) };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
        };
        cancel.Margin = new Padding(6, 0, 0, 0);
        ok.Margin = Padding.Empty;
        buttons.Controls.Add(cancel); // right-to-left: Cancel ends up on the right
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            Dock = DockStyle.Fill,
        };
        layout.Controls.Add(details);
        layout.Controls.Add(_nameBox);
        layout.Controls.Add(hint);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        ResumeLayout(false);
        PerformLayout();
    }

    /// <summary>The name the user entered, cleaned up, or null to use the model name.</summary>
    public string? MonitorName => Clean(_nameBox.Text);

    /// <summary>One line, no control characters, at most <see cref="MaxNameLength"/> characters; null if empty.</summary>
    public static string? Clean(string? text)
    {
        if (text == null) return null;
        string oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => new string(part.Where(c => !char.IsControl(c)).ToArray()))
            .Where(part => part.Length > 0));
        if (oneLine.Length > MaxNameLength) oneLine = oneLine[..MaxNameLength].TrimEnd();
        return oneLine.Length == 0 ? null : oneLine;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Location = new Point(
            _workArea.Left + Math.Max(0, (_workArea.Width - Width) / 2),
            _workArea.Top + Math.Max(0, (_workArea.Height - Height) / 2));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _nameBox.Focus();
        _nameBox.SelectAll();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon?.Dispose();
        base.Dispose(disposing);
    }
}
