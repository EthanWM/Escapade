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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint hIcon);

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
