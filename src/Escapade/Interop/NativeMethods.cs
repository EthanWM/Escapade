using System.Runtime.InteropServices;

namespace Escapade.Interop;

internal static partial class NativeMethods
{
    // --- shlwapi ---------------------------------------------------------

    /// <summary>Explorer's "natural" ordering: file2 sorts before file10.</summary>
    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int StrCmpLogicalW(string psz1, string psz2);

    // --- shell32: icons --------------------------------------------------

    internal const uint SHGFI_ICON = 0x000000100;
    internal const uint SHGFI_SMALLICON = 0x000000001;
    internal const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SHFILEINFOW
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        public fixed char szDisplayName[260];
        public fixed char szTypeName[80];
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    internal const uint SHGFI_PIDL = 0x000000008;

    /// <summary>Overload for SHGFI_PIDL: <paramref name="pidl"/> is an absolute ITEMIDLIST.</summary>
    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
    internal static partial nint SHGetFileInfo(nint pidl, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint hIcon);

    // --- user32: popup menus (hosting the shell's IContextMenu) ----------

    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_RETURNCMD = 0x0100;

    internal const int WM_CONTEXTMENU = 0x007B;
    internal const int WM_DRAWITEM = 0x002B;
    internal const int WM_MEASUREITEM = 0x002C;
    internal const int WM_INITMENUPOPUP = 0x0117;
    internal const int WM_MENUCHAR = 0x0120;

    [LibraryImport("user32.dll")]
    internal static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(nint hMenu);

    /// <summary>With TPM_RETURNCMD, returns the chosen command id, or 0 if the menu was dismissed.</summary>
    [LibraryImport("user32.dll")]
    internal static partial int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hwnd, nint lptpm);

    // --- shell32: IContextMenu::InvokeCommand ----------------------------

    internal const uint CMIC_MASK_UNICODE = 0x00004000;
    internal const uint CMIC_MASK_SHIFT_DOWN = 0x10000000;
    internal const uint CMIC_MASK_PTINVOKE = 0x20000000;
    internal const uint CMIC_MASK_CONTROL_DOWN = 0x40000000;
    internal const int SW_SHOWNORMAL = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// Declared here rather than taken from Vanara so every string field is a raw pointer we
    /// control: lpVerb/lpVerbW carry either a command offset (MAKEINTRESOURCE) or a verb string.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public nint hwnd;
        public nint lpVerb;
        public nint lpParameters;
        public nint lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public nint hIcon;
        public nint lpTitle;
        public nint lpVerbW;
        public nint lpParametersW;
        public nint lpDirectoryW;
        public nint lpTitleW;
        public POINT ptInvoke;
    }

    // --- comctl32 ListView messages ---------------------------------------

    internal const uint LVM_FIRST = 0x1000;
    internal const uint LVM_GETNEXTITEM = LVM_FIRST + 12;
    internal const uint LVM_GETEDITCONTROL = LVM_FIRST + 24;
    internal const uint LVM_GETTOPINDEX = LVM_FIRST + 39;
    internal const uint EM_SETSEL = 0x00B1;
    internal const uint LVM_SETITEMSTATE = LVM_FIRST + 43;
    internal const uint LVM_SETSELECTIONMARK = LVM_FIRST + 67;

    internal const int LVNI_FOCUSED = 0x0001;
    internal const uint LVIS_FOCUSED = 0x0001;
    internal const uint LVIS_SELECTED = 0x0002;
    internal const uint LVIF_STATE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    internal struct LVITEMW
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public nint pszText;
        public int cchTextMax;
        public int iImage;
        public nint lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public nint puColumns;
        public nint piColFmt;
        public int iGroup;
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static partial nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static partial nint SendMessage(nint hWnd, uint msg, nint wParam, ref LVITEMW lParam);
}
