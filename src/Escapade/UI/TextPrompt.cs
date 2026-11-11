namespace Escapade.UI;

/// <summary>A small "type a name" dialog, for naming workspaces.</summary>
internal sealed class TextPrompt : Form
{
    private readonly TextBox _input;
    private readonly Func<string, string?> _validate;
    private readonly Label _problem;

    /// <param name="validate">Returns why the text won't do, or null if it's fine.</param>
    private TextPrompt(string title, string label, string initial, Func<string, string?> validate)
    {
        _validate = validate;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(380, 130);
        Padding = new Padding(12);

        var caption = new Label { Text = label, AutoSize = true, Location = new Point(12, 12) };
        _input = new TextBox { Text = initial, Location = new Point(12, 34), Width = 356, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        _problem = new Label { AutoSize = true, Location = new Point(12, 62), ForeColor = Color.Firebrick };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.None, Location = new Point(212, 92), Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(293, 92), Width = 75 };
        ok.Click += (_, _) =>
        {
            if (_validate(Value) is { } problem)
            {
                _problem.Text = problem;
                _input.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
        };
        _input.TextChanged += (_, _) => _problem.Text = "";
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([caption, _input, _problem, ok, cancel]);
    }

    private string Value => _input.Text.Trim();

    /// <summary>Asks for a name. Returns it trimmed, or null if cancelled.</summary>
    public static string? Ask(IWin32Window owner, string title, string label, string initial, Func<string, string?> validate)
    {
        using var prompt = new TextPrompt(title, label, initial, validate);
        prompt.Shown += (_, _) =>
        {
            prompt._input.Focus();
            prompt._input.SelectAll();
        };
        return prompt.ShowDialog(owner) == DialogResult.OK ? prompt.Value : null;
    }
}
