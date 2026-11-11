using System.Runtime.InteropServices;
using Escapade.Core;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using static Escapade.Interop.NativeMethods;
using CMF = Vanara.PInvoke.Shell32.CMF;
using GCS = Vanara.PInvoke.Shell32.GCS;
using IContextMenu = Vanara.PInvoke.Shell32.IContextMenu;
using IContextMenu2 = Vanara.PInvoke.Shell32.IContextMenu2;
using IContextMenu3 = Vanara.PInvoke.Shell32.IContextMenu3;
using KNOWNFOLDERID = Vanara.PInvoke.Shell32.KNOWNFOLDERID;
using PIDL = Vanara.PInvoke.Shell32.PIDL;

namespace Escapade.UI;

/// <summary>
/// Hosts the shell's own context menu (IContextMenu): the classic Explorer menu, which Windows 11
/// shows under "Show more options". Follows Raymond Chen's "How to host an IContextMenu" series:
/// populate a popup with QueryContextMenu, show it with TrackPopupMenuEx(TPM_RETURNCMD), forward
/// owner-draw and submenu messages to IContextMenu2/3 while it's open, then InvokeCommand.
/// Use on an STA thread; dispose afterwards to release the COM objects.
/// </summary>
internal sealed class ShellContextMenuHost : IDisposable
{
    private const uint FirstCommandId = 1;
    private const uint LastCommandId = 0x7FFF;

    private static readonly Guid IID_IContextMenu = typeof(IContextMenu).GUID;

    private readonly List<IDisposable> _owned;
    private readonly List<PIDL> _pidls; // absolute PIDLs whose last ids were handed to GetUIObjectOf
    private IContextMenu? _menu;
    private IContextMenu2? _menu2;
    private IContextMenu3? _menu3;
    private bool _tracking;

    private ShellContextMenuHost(IContextMenu menu, List<IDisposable> owned, List<PIDL> pidls)
    {
        _menu = menu;
        _menu2 = menu as IContextMenu2;
        _menu3 = menu as IContextMenu3;
        _owned = owned;
        _pidls = pidls;
    }

    /// <summary>The menu for items that share one parent folder (a pane's selection).</summary>
    public static ShellContextMenuHost ForPaths(IReadOnlyList<string> paths, nint owner)
    {
        var items = paths.Select(p => new ShellItem(p)).ToList();
        var owned = new List<IDisposable>(items);
        try
        {
            var parent = items[0].Parent ?? throw new InvalidOperationException("The item has no parent folder.");
            owned.Add(parent);
            return ForItems(parent, items, owner, owned);
        }
        catch
        {
            owned.ForEach(o => o.Dispose());
            throw;
        }
    }

    /// <summary>The menu for children of <paramref name="parent"/>. The caller keeps ownership of both.</summary>
    public static ShellContextMenuHost ForItems(ShellFolder parent, IReadOnlyList<ShellItem> items, nint owner) =>
        ForItems(parent, items, owner, []);

    /// <summary>The folder background menu (New, Paste, Properties...), as for a right-click on empty space.</summary>
    public static ShellContextMenuHost ForBackground(string folder, nint owner)
    {
        var shellFolder = PathUtil.IsComputerRoot(folder)
            ? new ShellFolder(KNOWNFOLDERID.FOLDERID_ComputerFolder)
            : new ShellFolder(folder);
        try
        {
            var iid = IID_IContextMenu;
            shellFolder.IShellFolder.CreateViewObject((HWND)owner, iid, out var obj).ThrowIfFailed();
            return new ShellContextMenuHost((IContextMenu)obj!, [shellFolder], []);
        }
        catch
        {
            shellFolder.Dispose();
            throw;
        }
    }

    private static ShellContextMenuHost ForItems(ShellFolder parent, IReadOnlyList<ShellItem> items, nint owner, List<IDisposable> owned)
    {
        // GetUIObjectOf wants PIDLs relative to the parent: the last id of each absolute PIDL.
        // ILFindLastID points into the absolute PIDL, so those must stay alive until disposal.
        var pidls = items.Select(i => i.PIDL).ToList();
        var children = pidls.Select(p => Shell32.ILFindLastID(p.DangerousGetHandle())).ToArray();
        var iid = IID_IContextMenu;
        parent.IShellFolder.GetUIObjectOf((HWND)owner, (uint)children.Length, children, iid, default, out var obj).ThrowIfFailed();
        return new ShellContextMenuHost((IContextMenu)obj!, owned, pidls);
    }

    /// <summary>
    /// Shows the menu at a screen point. If the user picks a command, <paramref name="intercept"/>
    /// sees its canonical verb first (it may be null: not every command has one) and returns true
    /// to handle it itself. Otherwise the shell runs it. Returns the failure, if invoking failed.
    /// </summary>
    /// <param name="owner">Receives the menu's messages; its WndProc must call <see cref="HandleMenuMessage"/>.</param>
    public HRESULT ShowAndInvoke(nint owner, Point screen, CMF flags, string? directory, Func<string?, bool> intercept)
    {
        var menu = _menu ?? throw new ObjectDisposedException(nameof(ShellContextMenuHost));
        var hmenu = CreatePopupMenu();
        if (hmenu == 0)
            return HRESULT.E_OUTOFMEMORY;
        try
        {
            var hr = menu.QueryContextMenu(hmenu, 0, FirstCommandId, LastCommandId, flags);
            if (hr.Failed)
                return hr;

            int command;
            _tracking = true;
            try
            {
                command = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screen.X, screen.Y, owner, 0);
            }
            finally
            {
                _tracking = false;
            }
            if (command <= 0)
                return HRESULT.S_OK;

            int offset = command - (int)FirstCommandId;
            if (intercept(GetVerb(offset)))
                return HRESULT.S_OK;
            return Invoke(owner, offset, null, directory, screen);
        }
        finally
        {
            DestroyMenu(hmenu);
        }
    }

    /// <summary>Runs a command by canonical verb without showing the menu (e.g. "undelete" in the Recycle Bin).</summary>
    public HRESULT InvokeVerb(nint owner, string verb)
    {
        var menu = _menu ?? throw new ObjectDisposedException(nameof(ShellContextMenuHost));
        // Some handlers only know their verbs after QueryContextMenu, so populate a throwaway menu.
        var hmenu = CreatePopupMenu();
        try
        {
            menu.QueryContextMenu(hmenu, 0, FirstCommandId, LastCommandId, CMF.CMF_NORMAL);
            return Invoke(owner, 0, verb, null, null);
        }
        finally
        {
            if (hmenu != 0)
                DestroyMenu(hmenu);
        }
    }

    /// <summary>
    /// Forwards the messages the menu sends its owner window while it's open. Without this the
    /// "Send to", "Open with" and "New" submenus come up empty and owner-drawn icons are missing.
    /// Returns true if the message was handled.
    /// </summary>
    public bool HandleMenuMessage(ref Message m)
    {
        if (!_tracking)
            return false;

        switch (m.Msg)
        {
            case WM_DRAWITEM or WM_MEASUREITEM when m.WParam != 0:
                return false; // from a control, not a menu
            case WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR:
                break;
            default:
                return false;
        }

        try
        {
            if (_menu3 is not null)
            {
                if (_menu3.HandleMenuMsg2((uint)m.Msg, m.WParam, m.LParam, out var result).Succeeded)
                {
                    m.Result = result;
                    return true;
                }
            }
            else if (_menu2 is not null && m.Msg != WM_MENUCHAR)
            {
                if (_menu2.HandleMenuMsg((uint)m.Msg, m.WParam, m.LParam).Succeeded)
                {
                    m.Result = m.Msg == WM_INITMENUPOPUP ? 0 : 1;
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // A misbehaving shell extension must not take the menu down with it.
        }
        return false;
    }

    private string? GetVerb(int offset)
    {
        var buffer = Marshal.AllocHGlobal(512 * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var hr = _menu!.GetCommandString((nuint)offset, GCS.GCS_VERBW, 0, buffer, 512);
            return hr.Succeeded ? Marshal.PtrToStringUni(buffer) : null;
        }
        catch (Exception)
        {
            return null; // some handlers throw rather than return E_NOTIMPL
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private unsafe HRESULT Invoke(nint owner, int offset, string? verb, string? directory, Point? point)
    {
        var allocated = new List<nint>();
        nint Ansi(string? s) => s is null ? 0 : Track(Marshal.StringToHGlobalAnsi(s));
        nint Unicode(string? s) => s is null ? 0 : Track(Marshal.StringToHGlobalUni(s));
        nint Track(nint p)
        {
            allocated.Add(p);
            return p;
        }

        try
        {
            var modifiers = Control.ModifierKeys;
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = sizeof(CMINVOKECOMMANDINFOEX),
                fMask = CMIC_MASK_UNICODE
                    | (point is null ? 0 : CMIC_MASK_PTINVOKE)
                    | ((modifiers & Keys.Shift) != 0 ? CMIC_MASK_SHIFT_DOWN : 0)
                    | ((modifiers & Keys.Control) != 0 ? CMIC_MASK_CONTROL_DOWN : 0),
                hwnd = owner,
                // MAKEINTRESOURCE(offset) when invoking by position, otherwise the verb string.
                lpVerb = verb is null ? offset : Ansi(verb),
                lpVerbW = verb is null ? offset : Unicode(verb),
                lpDirectory = Ansi(directory),
                lpDirectoryW = Unicode(directory),
                nShow = SW_SHOWNORMAL,
                ptInvoke = point is { } p ? new Escapade.Interop.NativeMethods.POINT { X = p.X, Y = p.Y } : default,
            };
            return _menu!.InvokeCommand((nint)(&info));
        }
        finally
        {
            allocated.ForEach(Marshal.FreeHGlobal);
        }
    }

    public void Dispose()
    {
        if (_menu is not null)
        {
            Marshal.FinalReleaseComObject(_menu);
            _menu = null;
            _menu2 = null;
            _menu3 = null;
        }
        _owned.ForEach(o => o.Dispose());
        _owned.Clear();
        GC.KeepAlive(_pidls);
        _pidls.Clear();
    }
}
