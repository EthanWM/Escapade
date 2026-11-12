using Escapade.Core;

namespace Escapade.Tests;

public class PaneTabsTests
{
    private static List<string> Locations(PaneTabs pane) => pane.Tabs.Select(t => t.Location).ToList();

    [Fact]
    public void New_tabs_open_after_the_active_one_and_become_active()
    {
        var pane = new PaneTabs(@"C:\a");
        pane.Add(@"C:\b");
        pane.ActiveIndex = 0;
        pane.Add(@"C:\c");
        Assert.Equal([@"C:\a", @"C:\c", @"C:\b"], Locations(pane));
        Assert.Equal(@"C:\c", pane.Active.Location);
    }

    [Fact]
    public void Add_range_keeps_input_order_and_activates_the_last_tab()
    {
        var pane = new PaneTabs([new(@"C:\a"), new(@"C:\b"), new(@"C:\c")], 1);

        Assert.Equal(2, pane.AddRange([@"C:\x", @"C:\y"]));

        Assert.Equal([@"C:\a", @"C:\b", @"C:\x", @"C:\y", @"C:\c"], Locations(pane));
        Assert.Equal(@"C:\y", pane.Active.Location);
    }

    [Fact]
    public void The_last_tab_cant_be_closed()
    {
        var pane = new PaneTabs(@"C:\a");
        Assert.Null(pane.Close(0));
        Assert.Equal(1, pane.Count);
    }

    [Fact]
    public void Closing_the_active_tab_moves_right_then_left()
    {
        var pane = new PaneTabs([new(@"C:\a"), new(@"C:\b"), new(@"C:\c")], 1);
        pane.Close(1);
        Assert.Equal(@"C:\c", pane.Active.Location);
        pane.Close(1);
        Assert.Equal(@"C:\a", pane.Active.Location);
    }

    [Fact]
    public void Closing_a_tab_left_of_the_active_one_keeps_the_active_one()
    {
        var pane = new PaneTabs([new(@"C:\a"), new(@"C:\b"), new(@"C:\c")], 2);
        pane.Close(0);
        Assert.Equal(@"C:\c", pane.Active.Location);
    }

    [Fact]
    public void Reopen_brings_back_the_last_closed_tab_where_it_was()
    {
        var pane = new PaneTabs([new(@"C:\a"), new(@"C:\b"), new(@"C:\c")], 0);
        var closed = pane.Close(1)!;
        closed.History.Push(@"C:\b\deeper"); // the reopened tab keeps its history
        pane.Close(1);
        Assert.Equal(@"C:\c", pane.ReopenClosed()!.Location);
        Assert.Equal(@"C:\b\deeper", pane.ReopenClosed()!.Location);
        Assert.Equal([@"C:\a", @"C:\b\deeper", @"C:\c"], Locations(pane));
        Assert.Equal(1, pane.ActiveIndex);
        Assert.Null(pane.ReopenClosed());
    }

    [Fact]
    public void Removing_to_move_a_tab_isnt_remembered_as_closed()
    {
        var pane = new PaneTabs([new(@"C:\a"), new(@"C:\b")], 0);
        Assert.NotNull(pane.Remove(1));
        Assert.False(pane.HasClosed);
    }
}

public class WorkspaceTests
{
    private static readonly DateTime T0 = new(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FindTab_prefers_the_most_recently_used_duplicate_and_ignores_case()
    {
        var left = new PaneTabs([new(@"C:\a"), new(@"C:\dup") { LastActiveUtc = T0 }], 0);
        var right = new PaneTabs([new(@"c:\DUP") { LastActiveUtc = T0.AddMinutes(1) }], 0);
        var workspace = new Workspace("W", left, right);

        Assert.Equal((1, 0), workspace.FindTab(@"C:\dup"));
        Assert.Equal((0, 1), workspace.FindTab(@"C:\dup", onlyPane: 0));
        Assert.Null(workspace.FindTab(@"C:\nope"));
    }

    [Fact]
    public void Names_stay_unique_and_the_last_workspace_stays()
    {
        var session = Session.CreateDefault(@"C:\", @"D:\");
        var second = session.Add(new Workspace("main", new PaneTabs(@"C:\"), new PaneTabs(@"C:\")));
        Assert.Equal("main (2)", second.Name);
        Assert.False(session.Rename(second, "MAIN"));
        Assert.True(session.Rename(second, " Escapade "));
        Assert.Equal("Escapade", second.Name);

        session.Current = second;
        Assert.True(session.Remove(second));
        Assert.Equal("Main", session.Current.Name);
        Assert.False(session.Remove(session.Current));
    }

    [Fact]
    public void Removing_another_workspace_keeps_the_current_one()
    {
        var session = Session.CreateDefault(@"C:\", @"C:\");
        var a = session.Add(new Workspace("A", new PaneTabs(@"C:\"), new PaneTabs(@"C:\")));
        var b = session.Add(new Workspace("B", new PaneTabs(@"C:\"), new PaneTabs(@"C:\")));
        session.Current = b;
        session.Remove(a);
        Assert.Same(b, session.Current);
    }
}

public class SessionFileTests
{
    [Fact]
    public void Round_trips_through_json()
    {
        var tab = new TabState(@"E:\repos")
        {
            Selected = ["a.txt", "b.txt"],
            Focus = "b.txt",
            Top = "a.txt",
            LastActiveUtc = new DateTime(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc),
        };
        tab.History.Current!.SelectedName = "Escapade";
        tab.History.Push(@"E:\repos\Escapade");
        tab.History.GoBack();

        var work = new Workspace("Work", new PaneTabs([tab, new TabState(@"C:\")], 0), new PaneTabs(PathUtil.ComputerRoot))
        {
            Root = @"E:\repos\Escapade",
            LeftCollapsed = true,
            SplitRatio = 0.3,
            ActivePane = 1,
        };
        var session = new Session([new Workspace("Main", new PaneTabs(@"C:\"), new PaneTabs(@"D:\")), work], 1)
        {
            Window = new WindowBounds(10, 20, 1200, 800, Maximized: true),
        };

        var json = SettingsFile.Serialize(SessionFile.ToDto(session));
        var back = SessionFile.FromDto(SettingsFile.Deserialize<SessionDto>(json))!;

        Assert.Equal(["Main", "Work"], back.Workspaces.Select(w => w.Name));
        Assert.Equal("Work", back.Current.Name);
        Assert.Equal(session.Window, back.Window);
        var w = back.Current;
        Assert.Equal((@"E:\repos\Escapade", true, 0.3, 1), (w.Root, w.LeftCollapsed, w.SplitRatio, w.ActivePane));
        Assert.Equal(PathUtil.ComputerRoot, w.Right.Active.Location); // This PC survives

        var t = w.Left.Tabs[0];
        Assert.Equal(@"E:\repos", t.Location);
        Assert.True(t.History.CanGoForward);
        Assert.Equal("Escapade", t.History.Current!.SelectedName);
        Assert.Equal(["a.txt", "b.txt"], t.Selected);
        Assert.Equal(("b.txt", "a.txt"), (t.Focus, t.Top));
        Assert.Equal(tab.LastActiveUtc, t.LastActiveUtc);
    }

    [Fact]
    public void Odd_files_are_tolerated_or_rejected()
    {
        Assert.Null(SessionFile.FromDto(null));
        Assert.Null(SessionFile.FromDto(new SessionDto(1, null, [], null)));
        Assert.Null(SessionFile.FromDto(new SessionDto(SessionFile.Version + 1, null, [new("W", null, null, null, false, 0.5, 0)], null)));

        var session = SessionFile.FromDto(SettingsFile.Deserialize<SessionDto>("""
            { "version": 1, "current": "nope",
              "workspaces": [ { "name": "", "left": { "tabs": [ { "history": [] } ] }, "splitRatio": 7 },
                              { "name": "x" }, { "name": "X" } ] }
            """))!;
        Assert.Equal(["Workspace", "x", "X (2)"], session.Workspaces.Select(w => w.Name));
        Assert.Equal("Workspace", session.Current.Name);
        Assert.Equal(0.5, session.Current.SplitRatio);
        Assert.Equal(1, session.Current.Left.Count); // an unusable tab becomes one showing This PC
        Assert.Equal(PathUtil.ComputerRoot, session.Current.Left.Active.Location);
    }
}

public class HistoryExportTests
{
    [Fact]
    public void Export_keeps_the_current_entry_and_mostly_whats_behind_it()
    {
        var history = new NavigationHistory();
        for (int i = 0; i < 100; i++)
            history.Push($"C:\\{i}");
        for (int i = 0; i < 30; i++)
            history.GoBack(); // current is 69, with 30 ahead

        var (entries, index) = history.Export(max: 50);
        Assert.Equal(50, entries.Count);
        Assert.Equal(@"C:\69", entries[index].Location);
        Assert.Equal(10, entries.Count - 1 - index); // a fifth kept for Forward

        var back = NavigationHistory.Import(entries, index);
        Assert.Equal(@"C:\69", back.Current!.Location);
        Assert.True(back.CanGoBack);
        Assert.True(back.CanGoForward);
    }

    [Fact]
    public void Import_clamps_a_bad_index()
    {
        Assert.Null(NavigationHistory.Import([], 3).Current);
        Assert.Equal("B", NavigationHistory.Import([new("A"), new("B")], 9).Current!.Location);
    }
}

public class GitRepoTests
{
    [Fact]
    public void Finds_the_nearest_folder_with_a_git_entry()
    {
        var gits = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"E:\repos\Escapade\.git", @"E:\.git" };
        Assert.Equal(@"E:\repos\Escapade", GitRepo.FindRoot(@"E:\repos\Escapade\src\Escapade", gits.Contains));
        Assert.Equal(@"E:\repos\Escapade", GitRepo.FindRoot(@"E:\repos\Escapade", gits.Contains));
        Assert.Equal(@"E:\", GitRepo.FindRoot(@"E:\repos\other", gits.Contains));
        Assert.Null(GitRepo.FindRoot(@"C:\Windows", gits.Contains));
        Assert.Null(GitRepo.FindRoot(PathUtil.ComputerRoot, gits.Contains));
    }
}
