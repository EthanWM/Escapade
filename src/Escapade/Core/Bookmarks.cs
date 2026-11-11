namespace Escapade.Core;

public sealed record Bookmark(string Name, string Path);

/// <summary>The user's bookmarked folders, in their order. The first nine have Alt+1…Alt+9.</summary>
public sealed class BookmarkList
{
    private readonly List<Bookmark> _items = [];

    public BookmarkList(IEnumerable<Bookmark>? items = null)
    {
        foreach (var item in items ?? [])
        {
            // Tolerate hand edits: skip blanks and duplicates, tidy the path, default the name.
            if (PathUtil.Normalize(item.Path) is not { Length: > 0 } path || IndexOf(path) >= 0)
                continue;
            var name = string.IsNullOrWhiteSpace(item.Name) ? PathUtil.ShortName(path) : item.Name.Trim();
            _items.Add(new Bookmark(name, path));
        }
    }

    public IReadOnlyList<Bookmark> Items => _items;

    public int IndexOf(string path) =>
        _items.FindIndex(b => PathUtil.SameLocation(b.Path, path));

    public bool Contains(string path) => IndexOf(path) >= 0;

    /// <summary>Adds a bookmark for <paramref name="path"/> at the end, or removes it. Returns true if added.</summary>
    public bool Toggle(string path)
    {
        int index = IndexOf(path);
        if (index >= 0)
        {
            _items.RemoveAt(index);
            return false;
        }
        _items.Add(new Bookmark(PathUtil.ShortName(path), path));
        return true;
    }

    public Bookmark? Remove(string path)
    {
        int index = IndexOf(path);
        if (index < 0)
            return null;
        var removed = _items[index];
        _items.RemoveAt(index);
        return removed;
    }
}
