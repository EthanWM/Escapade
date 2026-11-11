using Escapade.Interop;

namespace Escapade.Core;

public enum SortColumn
{
    Name,
    Modified,
    Type,
    Size,
}

/// <summary>
/// Folders (and drives) always first, then the chosen column, then Explorer's natural name order.
/// Ties are broken down to an ordinal compare so the order is total and merges are deterministic.
/// </summary>
public sealed class EntryComparer(SortColumn column, bool descending) : IComparer<FileEntry>
{
    public static readonly EntryComparer Default = new(SortColumn.Name, descending: false);

    public SortColumn Column { get; } = column;
    public bool Descending { get; } = descending;

    public int Compare(FileEntry? x, FileEntry? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        // Grouping ignores direction: folders stay on top when sorting descending, as in Explorer.
        int group = Group(x).CompareTo(Group(y));
        if (group != 0)
            return group;

        int result = Column switch
        {
            SortColumn.Modified => x.LastWriteUtc.CompareTo(y.LastWriteUtc),
            SortColumn.Type => string.Compare(x.TypeName, y.TypeName, StringComparison.OrdinalIgnoreCase),
            SortColumn.Size => x.Size.CompareTo(y.Size),
            _ => 0,
        };
        if (result == 0)
            result = NativeMethods.StrCmpLogicalW(x.Name, y.Name);
        if (result == 0)
            result = string.CompareOrdinal(x.Name, y.Name);

        return Descending ? -result : result;
    }

    private static int Group(FileEntry e) => e.IsContainer ? 0 : 1;

    /// <summary>
    /// Merges two lists that are each sorted by <paramref name="comparer"/> into a new sorted list.
    /// O(n + m), so streaming batches into a large folder doesn't re-sort everything each time.
    /// </summary>
    public static List<FileEntry> Merge(IReadOnlyList<FileEntry> a, IReadOnlyList<FileEntry> b, IComparer<FileEntry> comparer)
    {
        var result = new List<FileEntry>(a.Count + b.Count);
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
            result.Add(comparer.Compare(a[i], b[j]) <= 0 ? a[i++] : b[j++]);
        while (i < a.Count)
            result.Add(a[i++]);
        while (j < b.Count)
            result.Add(b[j++]);
        return result;
    }
}
