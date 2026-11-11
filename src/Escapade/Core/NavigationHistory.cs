namespace Escapade.Core;

public sealed class HistoryEntry(string location)
{
    public string Location { get; } = location;

    /// <summary>The item that had focus when the user left this location, restored on Back/Forward.</summary>
    public string? SelectedName { get; set; }
}

/// <summary>Browser-style back/forward history for one pane.</summary>
public sealed class NavigationHistory
{
    private readonly List<HistoryEntry> _entries = [];
    private int _index = -1;
    private int _previousIndex = -1;

    public HistoryEntry? Current => _index >= 0 ? _entries[_index] : null;
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;
    public int Count => _entries.Count;

    /// <summary>
    /// The entries to save, at most <paramref name="max"/>, keeping the current one and favouring
    /// what's behind it (Back) over what's ahead. Index is the current entry's position in them.
    /// </summary>
    public (List<HistoryEntry> Entries, int Index) Export(int max = 50)
    {
        if (_index < 0)
            return ([], -1);
        int forward = Math.Min(_entries.Count - 1 - _index, Math.Max(0, max / 5));
        int start = Math.Max(0, _index - (max - 1 - forward));
        int end = Math.Min(_entries.Count - 1, _index + forward);
        return (_entries.GetRange(start, end - start + 1), _index - start);
    }

    /// <summary>Rebuilds a saved history. An empty list or an out-of-range index gives an empty history.</summary>
    public static NavigationHistory Import(IEnumerable<HistoryEntry> entries, int index)
    {
        var history = new NavigationHistory();
        history._entries.AddRange(entries);
        history._index = history._entries.Count == 0 ? -1 : Math.Clamp(index, 0, history._entries.Count - 1);
        return history;
    }

    public HistoryEntry Push(string location)
    {
        if (_index < _entries.Count - 1)
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);

        _previousIndex = _index;
        var entry = new HistoryEntry(location);
        _entries.Add(entry);
        _index = _entries.Count - 1;
        return entry;
    }

    public HistoryEntry? GoBack()
    {
        if (!CanGoBack)
            return null;
        _previousIndex = _index;
        _index--;
        return Current;
    }

    public HistoryEntry? GoForward()
    {
        if (!CanGoForward)
            return null;
        _previousIndex = _index;
        _index++;
        return Current;
    }

    /// <summary>
    /// The current entry couldn't be shown (deleted, access denied...). Removes it and returns
    /// to the entry the user came from, or null if there is nothing left to return to.
    /// </summary>
    public HistoryEntry? RevertFailed()
    {
        if (_index < 0)
            return null;

        int removed = _index;
        _entries.RemoveAt(removed);

        int target = _previousIndex switch
        {
            < 0 => removed - 1,
            var p when p > removed => p - 1,
            var p => p,
        };
        if (target < 0 && _entries.Count > 0)
            target = 0;

        _index = Math.Min(target, _entries.Count - 1);
        _previousIndex = -1;
        return Current;
    }
}
