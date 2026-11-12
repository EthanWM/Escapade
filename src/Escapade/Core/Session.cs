namespace Escapade.Core;

/// <summary>
/// One tab: a folder with its own back/forward history, plus what was selected and scrolled to
/// when the user last left it, so switching back (or restarting) puts everything where it was.
/// </summary>
public sealed class TabState
{
    public TabState(string location)
    {
        History = new NavigationHistory();
        History.Push(location);
    }

    public TabState(NavigationHistory history) => History = history;

    public NavigationHistory History { get; private set; }

    /// <summary>The folder shown: the history's current entry.</summary>
    public string Location => History.Current?.Location ?? PathUtil.ComputerRoot;

    public List<string> Selected { get; set; } = [];
    public bool SelectedAll { get; set; }
    public string? Focus { get; set; }
    public string? Top { get; set; }

    /// <summary>When this tab was last the one in use, for Ctrl+Tab's most-recent-first order.</summary>
    public DateTime LastActiveUtc { get; set; }
}

/// <summary>A pane's tabs, which one is showing, and the recently closed ones (for Ctrl+Shift+T).</summary>
public sealed class PaneTabs
{
    private const int ClosedLimit = 25;
    private readonly List<TabState> _tabs = [];
    private readonly List<(TabState Tab, int Index)> _closed = [];
    private int _active;

    public PaneTabs(string location) : this([new TabState(location)], 0)
    {
    }

    public PaneTabs(IEnumerable<TabState> tabs, int active)
    {
        _tabs.AddRange(tabs);
        if (_tabs.Count == 0)
            _tabs.Add(new TabState(PathUtil.ComputerRoot));
        _active = Math.Clamp(active, 0, _tabs.Count - 1);
    }

    public IReadOnlyList<TabState> Tabs => _tabs;
    public int Count => _tabs.Count;
    public TabState Active => _tabs[_active];

    public int ActiveIndex
    {
        get => _active;
        set => _active = Math.Clamp(value, 0, _tabs.Count - 1);
    }

    public bool HasClosed => _closed.Count > 0;

    public int IndexOf(TabState tab) => _tabs.IndexOf(tab);

    /// <summary>Adds a tab just after the active one and makes it active.</summary>
    public TabState Add(TabState tab)
    {
        _active = _tabs.Count == 0 ? 0 : _active + 1;
        _tabs.Insert(_active, tab);
        return tab;
    }

    public TabState Add(string location) => Add(new TabState(location));

    /// <summary>Adds tabs after the active one in input order and activates the last added tab.</summary>
    public int AddRange(IEnumerable<string> locations)
    {
        int added = 0;
        foreach (var location in locations)
        {
            Add(location);
            added++;
        }
        return added;
    }

    /// <summary>
    /// Closes a tab, remembering it for <see cref="ReopenClosed"/>. The last tab can't be closed:
    /// returns null. Closing the active tab activates its right-hand neighbour (else the left).
    /// </summary>
    public TabState? Close(int index) => Remove(index, remember: true);

    /// <summary>Takes a tab out (to move it elsewhere) without remembering it as closed.</summary>
    public TabState? Remove(int index, bool remember = false)
    {
        if (_tabs.Count <= 1 || (uint)index >= (uint)_tabs.Count)
            return null;
        var tab = _tabs[index];
        _tabs.RemoveAt(index);
        if (index < _active || _active >= _tabs.Count)
            _active--;
        if (remember)
        {
            _closed.Add((tab, index));
            if (_closed.Count > ClosedLimit)
                _closed.RemoveAt(0);
        }
        return tab;
    }

    /// <summary>Brings back the most recently closed tab, where it was, and activates it.</summary>
    public TabState? ReopenClosed()
    {
        if (_closed.Count == 0)
            return null;
        var (tab, index) = _closed[^1];
        _closed.RemoveAt(_closed.Count - 1);
        index = Math.Min(index, _tabs.Count);
        _tabs.Insert(index, tab);
        _active = index;
        return tab;
    }
}

public sealed class Workspace(string name, PaneTabs left, PaneTabs right)
{
    public string Name { get; set; } = name;

    /// <summary>The git repository this workspace belongs to, if it was made for one.</summary>
    public string? Root { get; set; }

    public PaneTabs Left { get; } = left;
    public PaneTabs Right { get; } = right;
    public bool LeftCollapsed { get; set; }

    /// <summary>Left pane width as a fraction of the window.</summary>
    public double SplitRatio { get; set; } = 0.5;

    /// <summary>0 for the left pane, 1 for the right.</summary>
    public int ActivePane { get; set; }

    public PaneTabs Pane(int side) => side == 0 ? Left : Right;

    public IEnumerable<(int Pane, int Index, TabState Tab)> AllTabs()
    {
        for (int side = 0; side < 2; side++)
        {
            var pane = Pane(side);
            for (int i = 0; i < pane.Count; i++)
                yield return (side, i, pane.Tabs[i]);
        }
    }

    /// <summary>
    /// The tab showing <paramref name="location"/>, for duplicate detection: the most recently
    /// used one if there are several, optionally only within one pane.
    /// </summary>
    public (int Pane, int Index)? FindTab(string location, int? onlyPane = null) =>
        AllTabs()
            .Where(t => (onlyPane is null || t.Pane == onlyPane) && PathUtil.SameLocation(t.Tab.Location, location))
            .OrderByDescending(t => t.Tab.LastActiveUtc)
            .Select(t => ((int, int)?)(t.Pane, t.Index))
            .FirstOrDefault();
}

public sealed record WindowBounds(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>Everything restored on start-up: the workspaces, which one is open, and the window.</summary>
public sealed class Session
{
    private readonly List<Workspace> _workspaces = [];
    private int _current;

    public Session(IEnumerable<Workspace> workspaces, int current)
    {
        _workspaces.AddRange(workspaces);
        if (_workspaces.Count == 0)
            throw new ArgumentException("A session needs a workspace.", nameof(workspaces));
        _current = Math.Clamp(current, 0, _workspaces.Count - 1);
    }

    public static Session CreateDefault(string left, string right) =>
        new([new Workspace("Main", new PaneTabs(left), new PaneTabs(right))], 0);

    public IReadOnlyList<Workspace> Workspaces => _workspaces;

    public Workspace Current
    {
        get => _workspaces[_current];
        set => _current = _workspaces.IndexOf(value) is >= 0 and var i ? i : throw new ArgumentException("Not in this session.");
    }

    public WindowBounds? Window { get; set; }

    public bool NameTaken(string name, Workspace? except = null) =>
        _workspaces.Any(w => w != except && string.Equals(w.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>"Escapade", then "Escapade (2)"... for a name not already used.</summary>
    public string UniqueName(string baseName) => PathUtil.UniqueName(baseName.Trim(), n => NameTaken(n));

    public Workspace Add(Workspace workspace)
    {
        workspace.Name = UniqueName(workspace.Name);
        _workspaces.Add(workspace);
        return workspace;
    }

    /// <summary>Removes a workspace. The last one can't go. Removing the current one moves to a neighbour.</summary>
    public bool Remove(Workspace workspace)
    {
        int index = _workspaces.IndexOf(workspace);
        if (index < 0 || _workspaces.Count == 1)
            return false;
        var current = Current;
        _workspaces.RemoveAt(index);
        _current = current == workspace ? Math.Min(index, _workspaces.Count - 1) : _workspaces.IndexOf(current);
        return true;
    }

    public bool Rename(Workspace workspace, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || NameTaken(name, workspace))
            return false;
        workspace.Name = name;
        return true;
    }

    public Workspace? FindByRoot(string root) =>
        _workspaces.FirstOrDefault(w => w.Root is not null && PathUtil.SameLocation(w.Root, root));
}

/// <summary>Finds the git repository a folder is in.</summary>
public static class GitRepo
{
    /// <summary>
    /// The nearest folder at or above <paramref name="folder"/> containing ".git" (a folder, or a
    /// file for worktrees and submodules), or null. <paramref name="exists"/> checks a path; it
    /// touches the disk, so call this off the UI thread.
    /// </summary>
    public static string? FindRoot(string folder, Func<string, bool> exists)
    {
        for (var dir = folder; !PathUtil.IsComputerRoot(dir); dir = PathUtil.GetParent(dir) ?? PathUtil.ComputerRoot)
        {
            if (exists(Path.Join(dir, ".git")))
                return dir;
        }
        return null;
    }
}
