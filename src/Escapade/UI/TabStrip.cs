namespace Escapade.UI;

/// <summary>
/// A pane's row of tabs. Click to switch, middle-click or × to close, + or double-click the empty
/// space for a new tab, right-click for more. Never takes focus: the file list keeps it, so the
/// keyboard keeps working wherever the mouse has been. Tabs shrink to fit rather than scroll.
/// </summary>
internal sealed class TabStrip : Control
{
    private const TextFormatFlags TextFlags =
        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
        | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    private readonly ToolTip _toolTip = new() { ShowAlways = true };
    private IReadOnlyList<(string Text, string Tip)> _tabs = [];
    private readonly List<Rectangle> _rects = [];
    private Rectangle _plusRect;
    private int _active;
    private int _hover = -1;
    private bool _hoverClose;
    private bool _paneActive;
    private string? _shownTip;

    public TabStrip()
    {
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        Height = GetPreferredSize(Size.Empty).Height; // the pane's layout row sizes to this
    }

    public event Action<int>? TabClicked;
    public event Action<int>? CloseClicked;
    public event Action? NewTabClicked;

    /// <summary>A right-click: the tab index, or -1 for the empty space, and where (client coordinates).</summary>
    public event Action<int, Point>? MenuRequested;

    public void SetTabs(IReadOnlyList<(string Text, string Tip)> tabs, int active)
    {
        _tabs = tabs;
        _active = active;
        _hover = -1;
        LayoutTabs();
        Invalidate();
    }

    /// <summary>Whether this strip's pane is the active one; its active tab gets the accent.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool PaneActive
    {
        get => _paneActive;
        set
        {
            if (_paneActive == value)
                return;
            _paneActive = value;
            Invalidate();
        }
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Font.Height + S(12));

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Height = GetPreferredSize(Size.Empty).Height;
        LayoutTabs();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutTabs();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = GetPreferredSize(Size.Empty).Height;
        LayoutTabs();
    }

    private int CloseSize => S(16);

    private void LayoutTabs()
    {
        _rects.Clear();
        int plus = Height;
        int available = Math.Max(0, Width - plus - S(4));
        int min = S(60);
        int max = S(200);
        int extra = S(12) * 2 + CloseSize + S(4);

        var widths = _tabs.Select(t => Math.Clamp(TextRenderer.MeasureText(t.Text, Font).Width + extra, min, max)).ToList();
        int total = widths.Sum();
        if (total > available && _tabs.Count > 0)
        {
            int each = Math.Max(S(28), available / _tabs.Count);
            widths = widths.Select(_ => each).ToList();
        }

        int x = S(2);
        foreach (var width in widths)
        {
            _rects.Add(new Rectangle(x, S(3), width, Height - S(3)));
            x += width;
        }
        _plusRect = new Rectangle(Math.Min(x, Width - plus), S(3), plus, Height - S(3));
    }

    private Rectangle CloseRect(int index)
    {
        var r = _rects[index];
        int size = CloseSize;
        return new Rectangle(r.Right - size - S(6), r.Top + (r.Height - size) / 2, size, size);
    }

    /// <summary>The close button shows on the active tab and the hovered one, when there's room.</summary>
    private bool ShowsClose(int index) =>
        _tabs.Count > 1 && (index == _active || index == _hover) && _rects[index].Width >= S(56);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(SystemColors.Control);
        using var line = new Pen(SystemColors.ControlDark);
        g.DrawLine(line, 0, Height - 1, Width, Height - 1);

        for (int i = 0; i < _rects.Count; i++)
        {
            var r = _rects[i];
            bool active = i == _active;
            if (active)
            {
                using var back = new SolidBrush(SystemColors.Window);
                g.FillRectangle(back, r.X, r.Y, r.Width, r.Height);
                g.DrawLine(line, r.Left, r.Top, r.Left, r.Bottom);
                g.DrawLine(line, r.Right - 1, r.Top, r.Right - 1, r.Bottom);
                using var accent = new SolidBrush(_paneActive ? SystemColors.Highlight : SystemColors.ControlDark);
                g.FillRectangle(accent, r.X, r.Y, r.Width, S(2));
            }
            else
            {
                if (i == _hover)
                {
                    using var hover = new SolidBrush(SystemColors.ControlLight);
                    g.FillRectangle(hover, r);
                }
                if (i + 1 != _active && i + 1 < _rects.Count)
                    g.DrawLine(line, r.Right - 1, r.Top + S(5), r.Right - 1, r.Bottom - S(5));
            }

            int textRight = ShowsClose(i) ? CloseRect(i).Left - S(2) : r.Right - S(8);
            var textRect = Rectangle.FromLTRB(r.Left + S(10), r.Top, textRight, r.Bottom);
            TextRenderer.DrawText(g, _tabs[i].Text, Font, textRect, active ? SystemColors.WindowText : SystemColors.ControlText, TextFlags);

            if (ShowsClose(i))
            {
                var c = CloseRect(i);
                if (i == _hover && _hoverClose)
                {
                    using var hot = new SolidBrush(SystemColors.ControlDark);
                    g.FillRectangle(hot, c);
                }
                TextRenderer.DrawText(g, "×", Font, c, SystemColors.ControlText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        TextRenderer.DrawText(g, "+", Font, _plusRect, SystemColors.ControlText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private int HitTab(Point p) => _rects.FindIndex(r => r.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = HitTab(e.Location);
        bool hoverClose = hover >= 0 && ShowsCloseFor(hover) && CloseRect(hover).Contains(e.Location);
        if (hover != _hover || hoverClose != _hoverClose)
        {
            _hover = hover;
            _hoverClose = hoverClose;
            Invalidate();
        }

        string? tip = hover >= 0 ? (hoverClose ? "Close tab (middle-click)" : _tabs[hover].Tip)
            : _plusRect.Contains(e.Location) ? "New tab (double-click the empty space)" : null;
        if (tip != _shownTip)
        {
            _shownTip = tip;
            _toolTip.SetToolTip(this, tip);
        }
    }

    // While hovering, the close button appears on the hovered tab, so hit-testing must assume it.
    private bool ShowsCloseFor(int index) =>
        _tabs.Count > 1 && _rects[index].Width >= S(56);

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _hoverClose = false;
        _shownTip = null;
        _toolTip.SetToolTip(this, null);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int index = HitTab(e.Location);
        switch (e.Button)
        {
            case MouseButtons.Left when index >= 0 && ShowsCloseFor(index) && CloseRect(index).Contains(e.Location):
                CloseClicked?.Invoke(index);
                break;
            case MouseButtons.Left when index >= 0:
                TabClicked?.Invoke(index);
                break;
            case MouseButtons.Left when _plusRect.Contains(e.Location):
                NewTabClicked?.Invoke();
                break;
            case MouseButtons.Middle when index >= 0:
                CloseClicked?.Invoke(index);
                break;
            case MouseButtons.Right:
                MenuRequested?.Invoke(index, e.Location);
                break;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && HitTab(e.Location) < 0 && !_plusRect.Contains(e.Location))
            NewTabClicked?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _toolTip.Dispose();
        base.Dispose(disposing);
    }
}
