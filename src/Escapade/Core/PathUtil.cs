namespace Escapade.Core;

/// <summary>
/// Path handling for pane locations. A location is either a normalized absolute directory path
/// (no trailing separator except on roots, upper-case drive letter) or <see cref="ComputerRoot"/>,
/// the virtual "This PC" level above the drive roots.
/// </summary>
public static class PathUtil
{
    public const string ComputerRoot = "";
    public const string ComputerDisplayName = "This PC";

    public static bool IsComputerRoot(string location) => location.Length == 0;

    /// <summary>
    /// Turns user input from the path bar into a location. Accepts quotes, forward slashes,
    /// environment variables, <c>~</c>, bare drive letters and paths relative to
    /// <paramref name="relativeTo"/>. Returns null if the input can't be a path.
    /// Does not touch the disk.
    /// </summary>
    public static string? Normalize(string? input, string? relativeTo = null)
    {
        if (input is null)
            return null;

        var s = input.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s[1..^1].Trim();

        if (s.Length == 0 || s.Equals(ComputerDisplayName, StringComparison.OrdinalIgnoreCase))
            return ComputerRoot;

        s = Environment.ExpandEnvironmentVariables(s);
        if (s == "~" || s.StartsWith("~/", StringComparison.Ordinal) || s.StartsWith("~\\", StringComparison.Ordinal))
            s = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + s[1..];

        s = s.Replace('/', '\\');

        // "C:" alone means "the current directory on drive C", which is never what's meant here.
        if (s.Length == 2 && s[1] == ':' && char.IsAsciiLetter(s[0]))
            s += "\\";

        try
        {
            if (Path.IsPathFullyQualified(s))
            {
                s = Path.GetFullPath(s);
            }
            else
            {
                if (string.IsNullOrEmpty(relativeTo))
                    return null;
                s = Path.GetFullPath(s, relativeTo);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        s = Path.TrimEndingDirectorySeparator(s);
        if (s.Length >= 2 && s[1] == ':')
            s = char.ToUpperInvariant(s[0]) + s[1..];
        return s;
    }

    /// <summary>The containing location, <see cref="ComputerRoot"/> for drive roots, null for the computer root.</summary>
    public static string? GetParent(string location)
    {
        if (IsComputerRoot(location))
            return null;
        return Path.GetDirectoryName(location) ?? ComputerRoot;
    }

    /// <summary>The name under which <paramref name="location"/> appears in its parent's listing.</summary>
    public static string GetNameInParent(string location)
    {
        var name = Path.GetFileName(location);
        return name.Length > 0 ? name : location; // roots are listed under their own path, e.g. "C:\"
    }

    public static string Combine(string location, FileEntry entry) =>
        entry.Kind == EntryKind.Drive || IsComputerRoot(location)
            ? entry.Name
            : Path.Join(location, entry.Name);

    /// <summary>Text for the path bar.</summary>
    public static string ToDisplay(string location) =>
        IsComputerRoot(location) ? ComputerDisplayName : location;

    /// <summary>Short name for titles and tabs.</summary>
    public static string ShortName(string location) =>
        IsComputerRoot(location) ? ComputerDisplayName : GetNameInParent(location);
}
