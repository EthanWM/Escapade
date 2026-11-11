namespace Escapade.UI;

/// <summary>One row in the palette.</summary>
/// <param name="TitleMarks">Character indexes in the title to highlight (what the query matched).</param>
/// <param name="Run">Runs the item. The argument is true for Ctrl+Enter: "in the other pane".</param>
/// <param name="Remove">Shift+Delete, for items that can be forgotten. Returns a message for the footer.</param>
internal sealed record PaletteItem(
    string Title,
    string? Detail,
    string? Hint,
    int[] TitleMarks,
    int[] DetailMarks,
    Action<bool> Run,
    Func<string>? Remove = null);

/// <summary>What the palette shows for a query, and a footer line with the keys that apply.</summary>
internal sealed record PaletteResults(IReadOnlyList<PaletteItem> Items, string Footer);

/// <summary>
/// The Ctrl+P / Ctrl+Shift+P popup: a search box over a list of items supplied per keystroke.
/// A separate borderless window owned by the main form, so its keys never reach the main form's
/// shortcuts. Closes when it loses focus. Sized in the owner's DPI.
/// </summary>
internal sealed class CommandPalette : Form
{
    private const int MaxRows = 12;
    private const TextFormatFlags TextFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;

    private readonly Form _owner;
    private readonly Func<string, PaletteResults> _source;
    private readonly Panel _inner;
    private readonly TextBox _input;
    private readonly Panel _gap;
    private readonly ListBox _list;
    private readonly Label _footer;
    private readonly Font _bold;
    private readonly Font _inputFont;
    private IReadOnlyList<PaletteItem> _items = [];
    private string? _footerNote;
    private bool _finished;

    public CommandPalette(Form owner, Func<string, PaletteResults> source)
    {
        _owner = owner;
        _source = source;

        AutoScaleMode = AutoScaleMode.None; // everything below is already in the owner's device units
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Font = owner.Font;
        BackColor = SystemColors.ControlDark; // shows as a 1px border around _inner
        Padding = new Padding(1);
        Text = "Escapade palette";

        _bold = new Font(Font, FontStyle.Bold);
        _inputFont = new Font(Font.FontFamily, Font.Size * 1.2f, Font.Style, Font.Unit);

        _inner = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window,
            Padding = new Padding(S(6)),
        };
        _input = new TextBox
        {
            Dock = DockStyle.Top,
            Font = _inputFont,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _gap = new Panel { Dock = DockStyle.Top, Height = S(4) };
        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            IntegralHeight = false,
            TabStop = false,
            ItemHeight = Math.Min(255, Font.Height + S(10)),
        };
        _footer = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = Font.Height + S(8),
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true,
        };

        // Docking runs in reverse order of adding: the input claims the top first.
        _inner.Controls.Add(_list);
        _inner.Controls.Add(_footer);
        _inner.Controls.Add(_gap);
        _inner.Controls.Add(_input);
        Controls.Add(_inner);

        _input.TextChanged += (_, _) =>
        {
            _footerNote = null;
            Refill(keepIndex: false);
        };
        _list.DrawItem += OnDrawItem;
        _list.MouseClick += (_, e) =>
        {
            int index = _list.IndexFromPoint(e.Location);
            if (index >= 0)
            {
                _list.SelectedIndex = index;
                RunSelected(other: (ModifierKeys & Keys.Control) != 0);
            }
        };
        // Clicks land on the list, but typing should always go to the search box.
        // Deferred: taking focus away mid-mouse-down would make the list box abandon the click.
        _list.GotFocus += (_, _) => BeginInvoke(_input.Focus);
        Deactivate += (_, _) =>
        {
            if (!_finished)
                BeginInvoke(Close);
        };
    }

    private int S(int logical) => _owner.LogicalToDeviceUnits(logical);

    /// <summary>Shows the palette centred near the top of the owner's client area, <paramref name="top"/> pixels down.</summary>
    public void Open(string text, int top)
    {
        var client = _owner.RectangleToScreen(_owner.ClientRectangle);
        int width = Math.Max(S(320), Math.Min(S(720), client.Width - S(40)));
        Bounds = new Rectangle(client.Left + (client.Width - width) / 2, client.Top + top, width, Height);

        _input.Text = text;
        _input.SelectionStart = text.Length;
        Refill(keepIndex: false);
        Show(_owner);
        Activate();
        _input.Focus();
    }

    /// <summary>Replaces the query, for Ctrl+P / Ctrl+Shift+P pressed while already open.</summary>
    public void SetQuery(string text)
    {
        _input.Text = text;
        _input.SelectionStart = text.Length;
        _input.Focus();
    }

    private void Refill(bool keepIndex)
    {
        int previous = _list.SelectedIndex;
        var results = _source(_input.Text);
        _items = results.Items;

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var item in _items)
            _list.Items.Add(item.Title);
        if (_items.Count > 0)
            _list.SelectedIndex = keepIndex ? Math.Clamp(previous, 0, _items.Count - 1) : 0;
        _list.EndUpdate();

        var footer = _items.Count == 0 ? $"No matches.   {results.Footer}" : results.Footer;
        _footer.Text = _footerNote is null ? footer : $"{_footerNote}   {footer}";

        int rows = Math.Clamp(_items.Count, 1, MaxRows);
        Height = Padding.Vertical + _inner.Padding.Vertical + _input.Height + _gap.Height
            + rows * _list.ItemHeight + _footer.Height;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Down:
                MoveSelection(1);
                return true;
            case Keys.Up:
                MoveSelection(-1);
                return true;
            case Keys.PageDown:
                MoveSelection(MaxRows - 1);
                return true;
            case Keys.PageUp:
                MoveSelection(-(MaxRows - 1));
                return true;
            case Keys.Enter:
                RunSelected(other: false);
                return true;
            case Keys.Control | Keys.Enter:
                RunSelected(other: true);
                return true;
            case Keys.Escape:
                _finished = true;
                Close();
                return true;
            case Keys.Shift | Keys.Delete when Selected?.Remove is not null:
                RemoveSelected();
                return true;
            // Pressing the palette's own keys again flips between folders and commands, or moves down.
            case Keys.Control | Keys.P:
                if (_input.Text.StartsWith('>'))
                    SetQuery("");
                else
                    MoveSelection(1);
                return true;
            case Keys.Control | Keys.Shift | Keys.P:
                if (_input.Text.StartsWith('>'))
                    MoveSelection(1);
                else
                    SetQuery(">");
                return true;
            case Keys.Control | Keys.Back:
                DeleteWordBeforeCaret(); // the plain edit control would insert a box character
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void DeleteWordBeforeCaret()
    {
        int caret = _input.SelectionStart;
        if (_input.SelectionLength > 0)
        {
            _input.SelectedText = "";
            return;
        }
        var text = _input.Text;
        int start = caret;
        while (start > 0 && char.IsWhiteSpace(text[start - 1]))
            start--;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] is not ('\\' or '/'))
            start--;
        if (start == caret && start > 0)
            start--; // just a separator before the caret
        _input.Text = text[..start] + text[caret..];
        _input.SelectionStart = start;
    }

    private PaletteItem? Selected =>
        (uint)_list.SelectedIndex < (uint)_items.Count ? _items[_list.SelectedIndex] : null;

    private void MoveSelection(int delta)
    {
        if (_items.Count == 0)
            return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, _items.Count - 1);
    }

    private void RunSelected(bool other)
    {
        if (Selected is not { } item)
            return;
        _finished = true;
        Close();
        // After closing, so focus is back in the main window when the item runs.
        _owner.BeginInvoke(() => item.Run(other));
    }

    private void RemoveSelected()
    {
        if (Selected?.Remove is not { } remove)
            return;
        _footerNote = remove();
        Refill(keepIndex: true);
    }

    // ------------------------------------------------------------------
    // Drawing: title, then detail in grey, hint right-aligned; matched characters in bold
    // ------------------------------------------------------------------

    private void OnDrawItem(object? sender, DrawItemEventArgs e)
    {
        if ((uint)e.Index >= (uint)_items.Count)
            return;
        var item = _items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        var fore = selected ? SystemColors.HighlightText : SystemColors.WindowText;
        var dim = selected ? SystemColors.HighlightText : SystemColors.GrayText;
        using (var back = new SolidBrush(selected ? SystemColors.Highlight : SystemColors.Window))
            e.Graphics.FillRectangle(back, e.Bounds);

        int pad = S(8);
        var bounds = e.Bounds;
        int right = bounds.Right - pad;

        if (item.Hint is { Length: > 0 } hint)
        {
            int width = TextRenderer.MeasureText(e.Graphics, hint, Font, Size.Empty, TextFlags).Width;
            right -= width;
            TextRenderer.DrawText(e.Graphics, hint, Font, new Rectangle(right, bounds.Top, width, bounds.Height), dim, TextFlags);
            right -= pad * 2;
        }

        int x = DrawMarked(e.Graphics, item.Title, item.TitleMarks, fore, bounds.Left + pad, bounds, right);
        if (item.Detail is { Length: > 0 } detail)
            DrawMarked(e.Graphics, detail, item.DetailMarks, dim, x + pad * 2, bounds, right);
    }

    /// <summary>Draws text in runs, bold where marked, cut off with "…" at <paramref name="right"/>. Returns where it ended.</summary>
    private int DrawMarked(Graphics g, string text, int[] marks, Color color, int x, Rectangle bounds, int right)
    {
        var marked = new HashSet<int>(marks);
        int start = 0;
        while (start < text.Length && x < right)
        {
            bool bold = marked.Contains(start);
            int end = start + 1;
            while (end < text.Length && marked.Contains(end) == bold)
                end++;

            var run = text[start..end];
            var font = bold ? _bold : Font;
            int width = TextRenderer.MeasureText(g, run, font, Size.Empty, TextFlags).Width;
            if (x + width > right)
            {
                TextRenderer.DrawText(g, text[start..], font, new Rectangle(x, bounds.Top, right - x, bounds.Height),
                    color, TextFlags | TextFormatFlags.EndEllipsis);
                return right;
            }
            TextRenderer.DrawText(g, run, font, new Rectangle(x, bounds.Top, width, bounds.Height), color, TextFlags);
            x += width;
            start = end;
        }
        return x;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bold.Dispose();
            _inputFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
