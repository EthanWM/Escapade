namespace Escapade.Core;

/// <summary>One remembered folder: how much it's been used (decays over time) and when it was last visited.</summary>
public sealed record FrecencyEntry(string Path, double Rank, DateTime LastVisitUtc);

/// <summary>
/// Folder visit history ranked by "frecency", zoxide's scheme: every visit adds 1 to a folder's
/// rank, the score weights that rank by how recently it was visited, and once the ranks add up
/// past <see cref="MaxTotal"/> they're all scaled down and the faded ones forgotten. So folders
/// you use a lot stay near the top, and a burst of visits to a folder fades once you move on.
/// </summary>
public sealed class Frecency
{
    public const double MaxTotal = 10_000;

    private readonly Dictionary<string, FrecencyEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private double _total;

    public Frecency(IEnumerable<FrecencyEntry>? entries = null)
    {
        foreach (var entry in entries ?? [])
        {
            if (entry.Path.Length > 0 && entry.Rank > 0 && !_entries.ContainsKey(entry.Path))
            {
                _entries[entry.Path] = entry;
                _total += entry.Rank;
            }
        }
    }

    public int Count => _entries.Count;

    public IEnumerable<FrecencyEntry> Entries => _entries.Values;

    public void Visit(string path, DateTime nowUtc)
    {
        if (PathUtil.IsComputerRoot(path))
            return;
        _entries[path] = _entries.TryGetValue(path, out var entry)
            ? entry with { Rank = entry.Rank + 1, LastVisitUtc = nowUtc }
            : new FrecencyEntry(path, 1, nowUtc);
        _total += 1;
        if (_total > MaxTotal)
            Age();
    }

    public bool Remove(string path)
    {
        if (!_entries.Remove(path, out var entry))
            return false;
        _total -= entry.Rank;
        return true;
    }

    public double Score(string path, DateTime nowUtc) =>
        _entries.TryGetValue(path, out var entry) ? Score(entry, nowUtc) : 0;

    public static double Score(FrecencyEntry entry, DateTime nowUtc)
    {
        var age = nowUtc - entry.LastVisitUtc;
        double weight = age < TimeSpan.FromHours(1) ? 4
            : age < TimeSpan.FromDays(1) ? 2
            : age < TimeSpan.FromDays(7) ? 0.5
            : 0.25;
        return entry.Rank * weight;
    }

    /// <summary>Every entry, best first.</summary>
    public List<(FrecencyEntry Entry, double Score)> Ranked(DateTime nowUtc) =>
        _entries.Values
            .Select(e => (e, Score(e, nowUtc)))
            .OrderByDescending(x => x.Item2)
            .ThenByDescending(x => x.e.LastVisitUtc)
            .ToList();

    private void Age()
    {
        double factor = 0.9 * MaxTotal / _total;
        _total = 0;
        foreach (var entry in _entries.Values.ToList())
        {
            double rank = entry.Rank * factor;
            if (rank < 1)
            {
                _entries.Remove(entry.Path);
            }
            else
            {
                _entries[entry.Path] = entry with { Rank = rank };
                _total += rank;
            }
        }
    }
}
