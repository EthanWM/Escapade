using Escapade.Core;

namespace Escapade.UI;

internal sealed class MainForm : Form
{
    private readonly FilePane _pane;
    private readonly string _startLocation;

    public MainForm(string? startLocation)
    {
        _startLocation = startLocation ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        SuspendLayout();
        _pane = new FilePane { Dock = DockStyle.Fill };
        _pane.CurrentPathChanged += (_, _) => UpdateTitle();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 640);
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        Text = "Escapade";
        Controls.Add(_pane);
        ResumeLayout(false);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (!_pane.Navigate(_startLocation))
            _pane.Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        ActiveControl = null;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _pane.FocusList();
    }

    private void UpdateTitle() => Text = $"{PathUtil.ShortName(_pane.CurrentPath)} — Escapade";
}
