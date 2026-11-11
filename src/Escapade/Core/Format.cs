using System.Globalization;

namespace Escapade.Core;

public static class Format
{
    public static string Size(FileEntry entry) => entry.Kind switch
    {
        EntryKind.File when entry.Size >= 0 => FileSize(entry.Size),
        EntryKind.Drive when entry.Size >= 0 => HumanSize(entry.Size),
        _ => "",
    };

    /// <summary>Explorer style: whole kilobytes, rounded up, so a 10-byte file shows as 1 KB.</summary>
    public static string FileSize(long bytes)
    {
        long kb = (bytes + 1023) / 1024;
        return kb.ToString("N0", CultureInfo.CurrentCulture) + " KB";
    }

    public static string HumanSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes:N0} bytes"
            : value.ToString("0.#", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string Date(FileEntry entry) =>
        entry.LastWriteUtc == default
            ? ""
            : entry.LastWriteUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
