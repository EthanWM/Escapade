using Escapade.Core;

namespace Escapade.UI;

internal sealed class MainForm : Form
{
    private readonly SplitContainer _split;
    private readonly FilePane _left;
    private readonly FilePane _right;
    private readonly string _leftStart;
    private readonly string _rightStart;
    private FilePane _active;

    /// <param name="args">Optional start folders: left pane, then right pane.</param>
    public MainForm(string[] args)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _leftStart = args.ElementAtOrDefault(0) ?? home;
        _rightStart = args.ElementAtOrDefault(1) ?? _leftStart;

        SuspendLayout();

        _left = CreatePane();
        _right = CreatePane();
        _active = _left;
        _left.IsActive = true;

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 5,
            TabStop = false,
        };
        _split.Panel1.Controls.Add(_left);
        _split.Panel2.Controls.Add(_right);

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1280, 720);
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        Text = "Escapade";
        Controls.Add(_split);
        ResumeLayout(false);
    }

    private FilePane CreatePane()
    {
        var pane = new FilePane { Dock = DockStyle.Fill };
        pane.Enter += (_, _) => SetActive(pane);
        pane.CurrentPathChanged += (_, _) => UpdateTitle();
        pane.TransferRequested += OnTransferRequested;
        return pane;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _split.SplitterDistance = _split.Width / 2;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!_left.Navigate(_leftStart))
            _left.Navigate(home);
        if (!_right.Navigate(_rightStart))
            _right.Navigate(home);
        ActiveControl = null;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _left.FocusList();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Tab flips between the panes' lists (Commander style). Elsewhere, e.g. in a path bar,
        // it keeps its normal meaning.
        if (keyData is Keys.Tab or (Keys.Shift | Keys.Tab) && _active.IsListFocused && !_active.IsEditingName)
        {
            OtherPane(_active).FocusList();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private FilePane OtherPane(FilePane pane) => pane == _left ? _right : _left;

    private void SetActive(FilePane pane)
    {
        if (_active == pane)
            return;
        _active.IsActive = false;
        _active = pane;
        _active.IsActive = true;
        UpdateTitle();
    }

    private void UpdateTitle() => Text = $"{PathUtil.ShortName(_active.CurrentPath)} — Escapade";

    /// <summary>F5 / F6: copy or move the source pane's selection into the folder shown in the other pane.</summary>
    private async void OnTransferRequested(FilePane source, TransferKind kind)
    {
        var target = OtherPane(source);
        var sourceFolder = source.CurrentPath;
        var destination = target.CurrentPath;

        if (PathUtil.IsComputerRoot(sourceFolder))
            return;
        if (PathUtil.IsComputerRoot(destination))
        {
            source.ShowError("The other pane isn't showing a folder.");
            return;
        }
        if (PathUtil.SameLocation(sourceFolder, destination))
        {
            source.ShowError("Both panes show the same folder.");
            return;
        }

        var paths = source.GetCommandPaths();
        if (paths.Count == 0)
            return;

        var result = await FileOperationRunner.RunAsync(Handle, FileOperationRunner.Undoable, b =>
        {
            var items = paths.Select(b.Item).ToList();
            var folder = b.Folder(destination);
            if (kind == TransferKind.Move)
                b.Operations.QueueMoveOperation(items, folder);
            else
                b.Operations.QueueCopyOperation(items, folder);
        });
        if (IsDisposed)
            return;
        source.ReportResult(result, kind == TransferKind.Move ? "Move" : "Copy");

        // Select what arrived in the destination. The progress sink also reports items inside
        // copied folders, so keep only the top level.
        var arrived = result.CreatedPaths
            .Where(p => PathUtil.SameLocation(Path.GetDirectoryName(p), destination))
            .Select(p => Path.GetFileName(p))
            .ToList();
        target.NotifyChanged(destination, arrived);
        if (kind == TransferKind.Move)
            source.NotifyChanged(sourceFolder);
    }
}
