using System.Runtime.InteropServices;
using Escapade.Core;
using static Escapade.Interop.NativeMethods;

namespace Escapade.UI;

/// <summary>
/// Small icons cached per extension (one folder icon, one per drive). Uses
/// SHGFI_USEFILEATTRIBUTES so files and folders never touch the disk: the icon comes from the
/// registered type alone. Per-file icons (.exe, .ico, .lnk) are a later refinement.
/// Must be called on the UI (STA) thread.
/// </summary>
internal sealed class IconCache : IDisposable
{
    private readonly Dictionary<string, int> _indices = new(StringComparer.OrdinalIgnoreCase);

    public IconCache(Size iconSize)
    {
        ImageList = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = iconSize };
    }

    public ImageList ImageList { get; }

    public int GetIconIndex(FileEntry entry, string fullPath)
    {
        var key = entry.Kind switch
        {
            EntryKind.Drive => "drive:" + fullPath,
            EntryKind.Directory => "dir:",
            _ => "ext:" + Path.GetExtension(entry.Name),
        };
        if (!_indices.TryGetValue(key, out int index))
        {
            index = Load(entry, fullPath);
            _indices[key] = index;
        }
        return index;
    }

    private int Load(FileEntry entry, string fullPath)
    {
        uint flags = SHGFI_ICON | SHGFI_SMALLICON;
        string path;
        uint attributes;
        switch (entry.Kind)
        {
            case EntryKind.Drive:
                path = fullPath; // drives need the real path to get their specific icon
                attributes = 0;
                break;
            case EntryKind.Directory:
                flags |= SHGFI_USEFILEATTRIBUTES;
                path = "folder";
                attributes = FILE_ATTRIBUTE_DIRECTORY;
                break;
            default:
                flags |= SHGFI_USEFILEATTRIBUTES;
                path = "file" + Path.GetExtension(entry.Name);
                attributes = FILE_ATTRIBUTE_NORMAL;
                break;
        }

        var info = default(SHFILEINFOW);
        var ok = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), flags);
        if (ok == 0 || info.hIcon == 0)
            return -1;

        try
        {
            using var icon = Icon.FromHandle(info.hIcon);
            ImageList.Images.Add(icon.ToBitmap()); // copy out before the handle is destroyed
            return ImageList.Images.Count - 1;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    public void Dispose() => ImageList.Dispose();
}
