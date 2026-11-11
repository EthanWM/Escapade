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
