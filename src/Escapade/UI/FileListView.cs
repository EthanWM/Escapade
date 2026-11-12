using Escapade.Interop;
using static Escapade.Interop.NativeMethods;

namespace Escapade.UI;

/// <summary>
/// A virtual-mode details ListView with double buffering and direct item-state helpers.
/// The helpers go straight to LVM_SETITEMSTATE because WinForms has no "select all" for
/// virtual lists, and selecting items one by one through SelectedIndices is slow.
/// </summary>
internal sealed class FileListView : ListView
{
    public FileListView()
    {
        DoubleBuffered = true; // maps to LVS_EX_DOUBLEBUFFER; stops flicker as batches stream in
        View = View.Details;
        VirtualMode = true;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = true;
        LabelWrap = false;
        AllowColumnReorder = true;
        HeaderStyle = ColumnHeaderStyle.Clickable;
    }

    /// <summary>
    /// Whether the last context menu request came from the mouse. Keyboard requests (the Menu key,
    /// Shift+F10) arrive with lParam -1, so Shift+right-click can mean something different from
    /// Shift+F10.
    /// </summary>
    public bool ContextMenuFromMouse { get; private set; }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CONTEXTMENU)
            ContextMenuFromMouse = unchecked((int)(long)m.LParam) != -1;
        base.WndProc(ref m);
    }

    /// <summary>
    /// Draws the rows here instead of leaving it to the native control, so selected rows can be
    /// the theme's colour (the dark scheme's orange); the native dark theme picks its own. Column
    /// headers stay native. Used in dark mode only; light mode keeps the standard Windows look.
    /// </summary>
    public void UseThemedRows()
    {
        OwnerDraw = true;
        DrawColumnHeader += (_, e) => e.DrawDefault = true;
        DrawItem += (_, _) => { }; // each cell, including its background, is drawn in DrawSubItem
        DrawSubItem += OnDrawSubItem;
    }

    private const TextFormatFlags CellFlags =
        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
        | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is not { } item || e.SubItem is not { } cell)
            return;
        var g = e.Graphics;
        var bounds = e.Bounds;
        bool selected = item.Selected;
        var back = selected ? (Focused ? Theme.SelectionBack : Theme.InactiveSelectionBack) : BackColor;
        using (var brush = new SolidBrush(back))
            g.FillRectangle(brush, bounds);

        int pad = LogicalToDeviceUnits(6);
        Rectangle textRect;
        if (e.ColumnIndex == 0)
        {
            int x = bounds.X + pad / 2;
            if (SmallImageList is { } images && (uint)item.ImageIndex < (uint)images.Images.Count)
            {
                var size = images.ImageSize;
                images.Draw(g, x, bounds.Y + (bounds.Height - size.Height) / 2, item.ImageIndex);
                x += size.Width + pad / 2;
            }
            textRect = Rectangle.FromLTRB(x, bounds.Y, bounds.Right - pad / 2, bounds.Bottom);

            // The keyboard focus, when the list has it: an orange bar at the row's left edge.
            if (item.Focused && Focused)
            {
                using var bar = new SolidBrush(Theme.Accent);
                g.FillRectangle(bar, bounds.X, bounds.Y, Math.Max(2, LogicalToDeviceUnits(2)), bounds.Height);
            }
        }
        else
        {
            textRect = Rectangle.FromLTRB(bounds.X + pad, bounds.Y, bounds.Right - pad, bounds.Bottom);
        }

        var flags = CellFlags;
        if (Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right)
            flags |= TextFormatFlags.Right;
        var color = selected ? Theme.SelectionText : item.ForeColor.IsEmpty ? ForeColor : item.ForeColor;
        TextRenderer.DrawText(g, cell.Text, Font, textRect, color, flags);
    }

    // Selected rows are drawn quieter without focus, so repaint when focus comes and goes.
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (OwnerDraw)
            Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        if (OwnerDraw)
            Invalidate();
    }

    public int FocusedIndex =>
        IsHandleCreated ? (int)SendMessage(Handle, LVM_GETNEXTITEM, -1, LVNI_FOCUSED) : -1;

    public int TopIndex =>
        IsHandleCreated ? (int)SendMessage(Handle, LVM_GETTOPINDEX, 0, 0) : 0;

    public void ClearSelection() => SetItemState(-1, 0, LVIS_SELECTED);

    public void SelectAllItems() => SetItemState(-1, LVIS_SELECTED, LVIS_SELECTED);

    public void SetSelected(int index) => SetItemState(index, LVIS_SELECTED, LVIS_SELECTED);

    public void SetFocused(int index)
    {
        SetItemState(index, LVIS_FOCUSED, LVIS_FOCUSED);
        // The selection mark is the anchor for Shift+arrow range extension.
        if (IsHandleCreated)
            SendMessage(Handle, LVM_SETSELECTIONMARK, 0, index);
    }

    /// <summary>Makes <paramref name="index"/> the only selected item and gives it focus.</summary>
    public void SelectOnly(int index)
    {
        ClearSelection();
        SetSelected(index);
        SetFocused(index);
    }

    private void SetItemState(int index, uint state, uint mask)
    {
        if (!IsHandleCreated)
            return;
        var item = new LVITEMW { mask = LVIF_STATE, state = state, stateMask = mask };
        SendMessage(Handle, LVM_SETITEMSTATE, index, ref item);
    }
}
