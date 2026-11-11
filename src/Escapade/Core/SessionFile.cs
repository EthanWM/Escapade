namespace Escapade.Core;

// The shape of session.json. Kept apart from the live classes so the file format only changes on purpose.

public sealed record SessionDto(int Version, string? Current, List<WorkspaceDto>? Workspaces, WindowBounds? Window);

public sealed record WorkspaceDto(
    string Name,
    string? Root,
    PaneDto? Left,
    PaneDto? Right,
    bool LeftCollapsed,
    double SplitRatio,
    int ActivePane);

public sealed record PaneDto(List<TabDto>? Tabs, int Active);

public sealed record TabDto(
    List<HistoryDto>? History,
    int HistoryIndex,
    List<string>? Selected,
    bool SelectedAll,
    string? Focus,
    string? Top,
    DateTime LastActiveUtc);

public sealed record HistoryDto(string Location, string? Selected);

public static class SessionFile
{
    public const int Version = 1;
    private const int SavedSelectionLimit = 2000;

    public static SessionDto ToDto(Session session) =>
        new(Version,
            session.Current.Name,
            session.Workspaces.Select(w => new WorkspaceDto(
                w.Name, w.Root, ToDto(w.Left), ToDto(w.Right), w.LeftCollapsed, w.SplitRatio, w.ActivePane)).ToList(),
            session.Window);

    private static PaneDto ToDto(PaneTabs pane) =>
        new(pane.Tabs.Select(ToDto).ToList(), pane.ActiveIndex);

    private static TabDto ToDto(TabState tab)
    {
        var (entries, index) = tab.History.Export();
        return new TabDto(
            entries.Select(e => new HistoryDto(e.Location, e.SelectedName)).ToList(),
            index,
            tab.SelectedAll ? null : tab.Selected.Take(SavedSelectionLimit).ToList(),
            tab.SelectedAll,
            tab.Focus,
            tab.Top,
            tab.LastActiveUtc);
    }

    /// <summary>
    /// Rebuilds a session from the file, tolerating missing or odd values (a hand edit, an older
    /// version). Returns null if there's nothing usable, such as a newer format or no workspaces.
    /// </summary>
    public static Session? FromDto(SessionDto? dto)
    {
        if (dto is null || dto.Version > Version || dto.Workspaces is not { Count: > 0 })
            return null;

        var workspaces = new List<Workspace>();
        foreach (var w in dto.Workspaces)
        {
            if (w is null)
                continue;
            var workspace = new Workspace(string.IsNullOrWhiteSpace(w.Name) ? "Workspace" : w.Name.Trim(), FromDto(w.Left), FromDto(w.Right))
            {
                Root = string.IsNullOrWhiteSpace(w.Root) ? null : w.Root,
                LeftCollapsed = w.LeftCollapsed,
                SplitRatio = w.SplitRatio is > 0.05 and < 0.95 ? w.SplitRatio : 0.5,
                ActivePane = w.ActivePane == 1 ? 1 : 0,
            };
            // Names must stay unique: they're how the current workspace is remembered.
            workspace.Name = PathUtil.UniqueName(workspace.Name, n => workspaces.Any(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)));
            workspaces.Add(workspace);
        }
        if (workspaces.Count == 0)
            return null;

        int current = Math.Max(0, workspaces.FindIndex(w => string.Equals(w.Name, dto.Current, StringComparison.OrdinalIgnoreCase)));
        return new Session(workspaces, current) { Window = dto.Window };
    }

    private static PaneTabs FromDto(PaneDto? pane)
    {
        var tabs = new List<TabState>();
        foreach (var t in pane?.Tabs ?? [])
        {
            if (t?.History is not { Count: > 0 } history)
                continue;
            var entries = history
                .Where(h => h?.Location is not null)
                .Select(h => new HistoryEntry(h.Location) { SelectedName = h.Selected })
                .ToList();
            if (entries.Count == 0)
                continue;
            tabs.Add(new TabState(NavigationHistory.Import(entries, t.HistoryIndex))
            {
                Selected = t.Selected ?? [],
                SelectedAll = t.SelectedAll,
                Focus = t.Focus,
                Top = t.Top,
                LastActiveUtc = t.LastActiveUtc,
            });
        }
        // A pane with nothing usable shows This PC rather than failing the whole session.
        return new PaneTabs(tabs, pane?.Active ?? 0);
    }
}
