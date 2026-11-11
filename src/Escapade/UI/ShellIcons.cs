using System.Runtime.InteropServices;
using Escapade.Core;
using Vanara.PInvoke;
using static Escapade.Interop.NativeMethods;

namespace Escapade.UI;

/// <summary>
/// The real shell icon of a specific folder or drive (Downloads' arrow, a USB stick). Unlike
/// <see cref="IconCache"/>, which only asks for a type's generic icon, this reads the item itself.
/// UI thread only.
/// </summary>
internal static class ShellIcons
{
    public static Bitmap? Small(string location)
    {
        var info = default(SHFILEINFOW);
        uint size = (uint)Marshal.SizeOf<SHFILEINFOW>();
        nint ok;
        if (PathUtil.IsComputerRoot(location))
        {
            using var pidl = new Shell32.PIDL(Shell32.KNOWNFOLDERID.FOLDERID_ComputerFolder);
            ok = SHGetFileInfo(pidl.DangerousGetHandle(), 0, ref info, size, SHGFI_ICON | SHGFI_SMALLICON | SHGFI_PIDL);
        }
        else if (IsOnLocalDisk(location))
        {
            ok = SHGetFileInfo(location, 0, ref info, size, SHGFI_ICON | SHGFI_SMALLICON);
        }
        else
        {
            // A network or removable location (a bookmark, say) could stall the UI thread while
            // the shell looks; the generic folder icon needs no disk access.
            ok = SHGetFileInfo(location, FILE_ATTRIBUTE_DIRECTORY, ref info, size, SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
        }
        if (ok == 0 || info.hIcon == 0)
            return null;

        try
        {
            using var icon = Icon.FromHandle(info.hIcon);
            return icon.ToBitmap();
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>A path on a fixed local drive (or a drive root, whose icon the shell knows without reading the media).</summary>
    private static bool IsOnLocalDisk(string location)
    {
        if (location.Length < 2 || location[1] != ':' || !char.IsAsciiLetter(location[0]))
            return false;
        if (location.Length <= 3)
            return true; // "C:\": drive icons come from the drive type, with no media access
        try
        {
            return new DriveInfo(location[..1]).DriveType == DriveType.Fixed;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
